/// <summary>
/// The generate stages: what they declare, and what they leave on disk.
/// </summary>
/// <remarks>
/// There are three of them because a pipeline may know both paths, know one, or know neither, and
/// each must declare exactly the options it reads and no others - an option declared but unread is
/// help-text that lies, and an option read but undeclared parses as a CLR default. The end-to-end
/// tests go through <c>Parse</c>/<c>Invoke</c> for that reason rather than calling the generator.
/// </remarks>
module Partas.Build.ExternalAnnotationsTests.GenerateTests

open System.IO
open System.Xml.Linq
open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.ExternalAnnotations
open Partas.Build.Tests.Helpers
open Partas.Build.ExternalAnnotationsTests.Helpers

/// The members an annotations file names.
let private membersIn (path: string) = [
    for element in XDocument.Load(path).Descendants (XName.Get "member") ->
        element.Attribute(XName.Get "name").Value
]

/// The attributes it annotates them with.
let private attributesIn (path: string) =
    XDocument.Load(path).Descendants (XName.Get "attribute")
    |> Seq.map (fun element -> element.Attribute(XName.Get "ctor").Value)
    |> Seq.map (fun ctor -> ctor.Substring (ctor.IndexOf "M:" + 2) |> fun s -> s.Substring (0, s.IndexOf ".#ctor"))
    |> Set.ofSeq

/// A command wrapping one stage spec, which is the only way to run one with its options registered.
let private commandOf (name: string) (spec: InputSpec<StageContext>) =
    command name {
        description "under test"
        pipeline name { spec }
    }

[<Tests>]
let tests =
    testList "generate" [
        testList "declared inputs" [
            test "generateTo reads the flags but not the paths, which its caller already knows" {
                let spec = generateTo "in.dll" "out.xml"
                Expect.equal (inputNames spec.Inputs) [ "--strict"; "--attribute" ] "the options"
                Expect.equal (spec.Read (parse spec.Inputs "")).Name "external annotations" "the stage name"
            }

            test "generateOnlyTo reads neither paths nor attributes, having been given the filter" {
                let spec = generateOnlyTo Partas.ExternalAnnotations.AttributeFilter.JetBrains "in.dll" "out.xml"

                // --attribute in particular must be absent: offering it while ignoring it would be
                // a flag that silently does nothing.
                Expect.equal (inputNames spec.Inputs) [ "--strict" ] "the options"
            }

            test "generateStage reads everything, having been given nothing" {
                Expect.equal
                    (inputNames generateStage.Inputs)
                    [ "--strict"; "--attribute"; "--assembly"; "--output" ]
                    "the options"
            }
        ]

        testList "running" [
            test "generate writes an annotations file for the assembly it is pointed at" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")
                    let exitCode = invoke generateCommand $"--assembly \"{fixtureAssembly}\" --output \"{output}\""

                    Expect.equal exitCode 0 "the exit code"
                    Expect.isTrue (File.Exists output) "the file"

                    let members = membersIn output
                    Expect.isNonEmpty members "the fixture carries JetBrains annotations, so some member must be named"

                    // Every kind of site the fixture carries: a member, a property, and a type.
                    for expected in [
                        "M:Fixture.Surface.JetBrainsSurface.Check(System.String)"
                        "M:Fixture.Surface.JetBrainsSurface.Inject(System.String)"
                        "P:Fixture.Surface.JetBrainsSurface.Name"
                        "T:Fixture.Surface.JetBrainsSurface"
                    ] do
                        Expect.contains members expected "the annotated member")
            }

            test "the default collects the JetBrains namespace rather than every attribute" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")
                    invoke generateCommand $"--assembly \"{fixtureAssembly}\" --output \"{output}\"" |> ignore

                    let attributes = attributesIn output

                    Expect.contains attributes "JetBrains.Annotations.NotNullAttribute" "a JetBrains attribute"

                    // The fixture is covered in MarkAttribute; none of it belongs in the output.
                    Expect.isFalse
                        (attributes |> Set.exists _.StartsWith("Fixture."))
                        $"the fixture's own attributes leaked into the file: {attributes}")
            }

            test "--attribute narrows the file to what was named" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")

                    invoke generateCommand $"--assembly \"{fixtureAssembly}\" --output \"{output}\" --attribute MarkAttribute"
                    |> ignore

                    let attributes = attributesIn output

                    Expect.equal attributes (Set.ofList [ "Fixture.Annotations.MarkAttribute" ]) "only what was asked for")
            }

            test "--attribute takes several names at once" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")

                    invoke
                        generateCommand
                        $"--assembly \"{fixtureAssembly}\" --output \"{output}\" --attribute MarkAttribute GradeAttribute"
                    |> ignore

                    Expect.equal
                        (attributesIn output)
                        (Set.ofList [ "Fixture.Annotations.GradeAttribute"; "Fixture.Annotations.MarkAttribute" ])
                        "both")
            }

            test "the output directory is created rather than required" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "nested", "deeper", "out.xml")
                    let exitCode = invoke generateCommand $"--assembly \"{fixtureAssembly}\" --output \"{output}\""

                    // The natural target is an obj/ or a lib/ that the pack has not made yet.
                    Expect.equal exitCode 0 "the exit code"
                    Expect.isTrue (File.Exists output) "the file")
            }

            test "a missing assembly fails rather than writing an empty file" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")
                    let missing = Path.Combine (dir, "not-here.dll")

                    Expect.notEqual (invoke generateCommand $"--assembly \"{missing}\" --output \"{output}\"") 0 "the exit code"
                    Expect.isFalse (File.Exists output) "no file")
            }
        ]

        testList "strict" [
            // An assembly whose references cannot be resolved is the one reproducible way to make
            // the generator skip members, and skipping is exactly the silent-absence failure
            // --strict exists to turn into a build failure.
            let stranded (body: string -> string -> unit) =
                inDirectory (fun dir ->
                    let copy = Path.Combine (dir, Path.GetFileName fixtureAssembly)
                    File.Copy (fixtureAssembly, copy)
                    body copy (Path.Combine (dir, "out.xml")))

            yield
                testCase "skipped members warn and the build carries on by default" (fun () ->
                    stranded (fun assembly output ->
                        let exitCode = invoke generateCommand $"--assembly \"{assembly}\" --output \"{output}\""

                        Expect.equal exitCode 0 "the exit code"
                        Expect.isTrue (File.Exists output) "the file is still written, minus what was skipped"))

            yield
                testCase "--strict turns the same skip into a failure" (fun () ->
                    stranded (fun assembly output ->
                        let exitCode = invoke generateCommand $"--assembly \"{assembly}\" --output \"{output}\" --strict"

                        Expect.equal exitCode 1 "the exit code"))

            yield
                testCase "--strict passes when nothing is skipped" (fun () ->
                    // Without this, the test above would pass just as well if --strict always failed.
                    inDirectory (fun dir ->
                        let output = Path.Combine (dir, "out.xml")

                        Expect.equal
                            (invoke generateCommand $"--assembly \"{fixtureAssembly}\" --output \"{output}\" --strict")
                            0
                            "the exit code"))
        ]

        testList "as a stage" [
            test "generateTo writes the file the pipeline named" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")
                    let cmd = commandOf "generate-to" (generateTo fixtureAssembly output)

                    Expect.equal (invoke cmd "") 0 "the exit code"
                    Expect.isNonEmpty (membersIn output) "the members")
            }

            test "generateOnlyTo uses its own filter and ignores the command line" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")
                    let filter = Partas.ExternalAnnotations.AttributeFilter.Named [ "GradeAttribute" ]
                    let cmd = commandOf "generate-only-to" (generateOnlyTo filter fixtureAssembly output)

                    Expect.equal (invoke cmd "") 0 "the exit code"
                    Expect.equal (attributesIn output) (Set.ofList [ "Fixture.Annotations.GradeAttribute" ]) "the filter held"

                    // The flag it never declared stays a parse error rather than quietly widening it.
                    Expect.isNonEmpty (parseErrors cmd "--attribute MarkAttribute") "--attribute is not a flag of this command")
            }
        ]
    ]
