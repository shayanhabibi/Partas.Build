/// <summary>
/// The <c>Directory.Build.targets</c> that <c>init</c> writes, and the pack arguments that stand in
/// for it.
/// </summary>
/// <remarks>
/// The written file is meant to be committed, so what it contains is a published artifact rather
/// than an implementation detail: the sentinel that makes it idempotent, the chain-import that
/// keeps a parent <c>Directory.Build.targets</c> alive, and the pinned tool command that makes the
/// file say what it runs. Each is asserted here because each fails silently - as a doubled import,
/// a disabled parent, or a pack that quietly generates nothing.
/// </remarks>
module Partas.Build.ExternalAnnotationsTests.InitTests

open System.IO
open System.Xml.Linq
open Expecto
open Partas.Build
open Partas.Build.ExternalAnnotations
open Partas.Build.Tests.Helpers
open Partas.Build.ExternalAnnotationsTests.Helpers

/// The file as init writes it, with no tool pinned.
let private written (toolCommand: string option) (body: string -> string -> unit) =
    inDirectory (fun dir ->
        let path = Path.Combine (dir, "Directory.Build.targets")
        writeTargets path toolCommand
        body path (File.ReadAllText path))

[<Tests>]
let tests =
    testList "init" [
        testList "the targets file" [
            test "is valid MSBuild XML under a Project element" {
                written None (fun path _ ->
                    // Parsed rather than string-matched: a file that does not load takes the whole
                    // build down at evaluation, before any target of ours runs.
                    let doc = XDocument.Load path
                    Expect.equal doc.Root.Name.LocalName "Project" "the root element")
            }

            test "carries the sentinel that makes a second import a no-op" {
                written None (fun _ content ->
                    // Both routes - a committed Directory.Build.targets and a -p: injection - may
                    // be active at once, and without the sentinel that appends the pack target twice.
                    Expect.stringContains content "PartasExternalAnnotationsImported" "the sentinel")
            }

            test "re-imports the parent it would otherwise shadow" {
                written None (fun _ content ->
                    // MSBuild stops at the first Directory.Build.targets walking up, so writing one
                    // silently disables the repository's own unless this import is here.
                    Expect.stringContains content "GetPathOfFileAbove" "the chain import")
            }

            test "hooks the pack rather than the build" {
                written None (fun _ content ->
                    Expect.stringContains content "TargetsForTfmSpecificContentInPackage" "the pack hook"
                    Expect.stringContains content "lib\\$(TargetFramework)" "the package path ReSharper reads")
            }

            test "pins no tool command unless one was given" {
                written None (fun _ content ->
                    Expect.isFalse
                        (content.Contains "<PartasExternalAnnotationsTool ")
                        "an unasked-for tool property would run a command the user never chose")
            }

            test "pins the tool command when one was given" {
                written (Some "dotnet partas-annotations") (fun path content ->
                    Expect.stringContains content "<PartasExternalAnnotationsTool" "the property"
                    Expect.stringContains content "dotnet partas-annotations" "the command"

                    // Conditioned, so an ambient property still wins over the committed default.
                    Expect.stringContains content "'$(PartasExternalAnnotationsTool)' == ''" "the condition"

                    // And still a file MSBuild can read, which the string injection could break.
                    XDocument.Load path |> ignore)
            }

            test "keeps everything it had when a command is pinned into it" {
                inDirectory (fun dir ->
                    let bare = Path.Combine (dir, "bare.targets")
                    let pinned = Path.Combine (dir, "pinned.targets")
                    writeTargets bare None
                    writeTargets pinned (Some "dotnet partas-annotations")

                    let targetsOf (path: string) = [
                        for element in XDocument.Load(path).Descendants (XName.Get "Target") ->
                            element.Attribute(XName.Get "Name").Value
                    ]

                    Expect.isNonEmpty (targetsOf bare) "there are targets to compare"
                    Expect.equal (targetsOf pinned) (targetsOf bare) "the targets")
            }

            test "creates the directory it is asked to write into" {
                inDirectory (fun dir ->
                    let path = Path.Combine (dir, "nested", "deeper", "Directory.Build.targets")
                    writeTargets path None
                    Expect.isTrue (File.Exists path) "the file")
            }
        ]

        testList "the stage" [
            test "initStage reads the tool, the force flag and the directory" {
                Expect.equal (inputNames initStage.Inputs) [ "--annotations-tool"; "--force"; "--directory" ] "the options"
                Expect.equal (initStage.Read (parse initStage.Inputs "")).Name "init annotations" "the stage name"
            }

            test "initIn reads the flags but not the directory, which its caller already knows" {
                let spec = initIn "."
                Expect.equal (inputNames spec.Inputs) [ "--annotations-tool"; "--force" ] "the options"
            }

            test "writes Directory.Build.targets into the directory it was given" {
                inDirectory (fun dir ->
                    let exitCode = invoke initCommand $"--directory \"{dir}\""

                    Expect.equal exitCode 0 "the exit code"
                    Expect.isTrue (File.Exists (Path.Combine (dir, "Directory.Build.targets"))) "the file")
            }

            test "refuses to overwrite a file that is already there" {
                inDirectory (fun dir ->
                    let path = Path.Combine (dir, "Directory.Build.targets")
                    File.WriteAllText (path, "<Project><!-- hand written --></Project>")

                    let exitCode = invoke initCommand $"--directory \"{dir}\""

                    // Silently replacing it would destroy build configuration that is not ours.
                    Expect.equal exitCode 1 "the exit code"
                    Expect.stringContains (File.ReadAllText path) "hand written" "the file is untouched")
            }

            test "--force overwrites it" {
                inDirectory (fun dir ->
                    let path = Path.Combine (dir, "Directory.Build.targets")
                    File.WriteAllText (path, "<Project><!-- hand written --></Project>")

                    Expect.equal (invoke initCommand $"--directory \"{dir}\" --force") 0 "the exit code"
                    Expect.stringContains (File.ReadAllText path) "PartasExternalAnnotationsImported" "the file was replaced")
            }

            test "--annotations-tool reaches the written file" {
                inDirectory (fun dir ->
                    invoke initCommand $"--directory \"{dir}\" --annotations-tool \"dotnet partas-annotations\"" |> ignore

                    Expect.stringContains
                        (File.ReadAllText (Path.Combine (dir, "Directory.Build.targets")))
                        "dotnet partas-annotations"
                        "the pinned command")
            }

            test "initIn writes where the pipeline said, not where the process is" {
                inDirectory (fun dir ->
                    let cmd = command "init-in" {
                        description "under test"
                        pipeline "init" { initIn dir }
                    }

                    Expect.equal (invoke cmd "") 0 "the exit code"
                    Expect.isTrue (File.Exists (Path.Combine (dir, "Directory.Build.targets"))) "the file")
            }
        ]

        testList "packArgs" [
            test "injects the targets file through CustomAfterMicrosoftCommonTargets" {
                inDirectory (fun dir ->
                    let path = Path.Combine (dir, "annotations.targets")
                    writeTargets path None

                    match packArgs path with
                    | [ single ] -> Expect.stringStarts single "-p:CustomAfterMicrosoftCommonTargets=" "the property"
                    | args -> failtestf "expected one argument, got %A" args)
            }

            test "makes the path absolute, because MSBuild would resolve it per project" {
                // A relative path here resolves against each project's own directory, so it works
                // for a solution-root project and silently does nothing for every other one.
                let arg = packArgs "annotations.targets" |> List.exactlyOne
                let path = arg.Substring (arg.IndexOf '=' + 1)

                Expect.isTrue (Path.IsPathRooted path) $"{path} is not rooted"
            }
        ]
    ]
