/// <summary>
/// Partas.Build stages for shipping ReSharper external annotations with a NuGet package.
///
/// Annotations declared in an assembly's own metadata are not reliably honoured across a binary
/// reference. A <c>&lt;AssemblyName&gt;.ExternalAnnotations.xml</c> sidecar next to the assembly
/// is, so this generates one and gets it into <c>lib/&lt;tfm&gt;/</c>.
///
/// The generation itself lives in <c>Partas.ExternalAnnotations</c>, which has no dependency on
/// Partas.Build; this module is only the build-pipeline surface over it.
/// </summary>
module Partas.Build.ExternalAnnotations

open System.IO
open System.IO.Compression
open System.Reflection
open System.Xml.Linq
open Partas.Build

/// <summary>The MSBuild logic, carried as an embedded resource so <c>init</c> can write it out.</summary>
module private Assets =
    [<Literal>]
    let TargetsFileName = "Partas.ExternalAnnotations.targets"

    let readTargets () =
        let asm = Assembly.GetExecutingAssembly ()

        let name =
            asm.GetManifestResourceNames ()
            |> Array.find _.EndsWith(TargetsFileName)

        use stream = asm.GetManifestResourceStream name
        use reader = new StreamReader (stream)
        reader.ReadToEnd ()

    /// <summary>
    /// Pins the generator command in the emitted file, so the committed artifact says what runs
    /// rather than depending on an ambient property.
    /// </summary>
    let withToolCommand (toolCommand: string option) (content: string) =
        match toolCommand with
        | None -> content
        | Some command ->
            let injected =
                "<Project>\n\n  <PropertyGroup>\n"
                + $"    <PartasExternalAnnotationsTool Condition=\"'$(PartasExternalAnnotationsTool)' == ''\">{command}</PartasExternalAnnotationsTool>\n"
                + "  </PropertyGroup>"

            let index = content.IndexOf "<Project>"

            if index < 0 then
                failwith $"{TargetsFileName} does not start with a <Project> element"

            content.Substring (0, index) + injected + content.Substring (index + "<Project>".Length)

[<AutoOpen>]
module ExternalAnnotations =

    module Options =
        let strict =
            Input.option<bool> "--strict"
            |> Input.desc "Fail when any member is skipped, rather than warning and continuing"

        let annotationsTool =
            Input.optionMaybe<string> "--annotations-tool"
            |> Input.arity Arity.ExactlyOne
            |> Input.desc "Command that generates annotations during pack, e.g. 'dotnet partas-annotations'"
            |> Input.helpName "COMMAND"

        let force =
            Input.option<bool> "--force"
            |> Input.desc "Overwrite an existing Directory.Build.targets"

        let assembly =
            Input.option<string> "--assembly"
            |> Input.required
            |> Input.acceptLegalFilePathsOnly
            |> Input.desc "Assembly to scan for annotations"
            |> Input.helpName "PATH"

        let output =
            Input.option<string> "--output"
            |> Input.required
            |> Input.acceptLegalFilePathsOnly
            |> Input.desc "Annotations file to write"
            |> Input.helpName "PATH"

        let attribute =
            Input.option<string array> "--attribute"
            |> Input.def [||]
            |> Input.arity Arity.ZeroOrMore
            |> Input.allowMultipleArgumentsPerToken
            |> Input.desc
                "Collect only these attributes, by simple name; the default is every JetBrains.Annotations attribute"
            |> Input.helpName "NAME"

        let minMembers =
            Input.option<int> "--min-members"
            |> Input.def 0
            |> Input.desc "Fail when any assembly's sidecar annotates fewer members than this"
            |> Input.helpName "N"

        let package =
            Input.option<string> "--package"
            |> Input.required
            |> Input.acceptLegalFilePathsOnly
            |> Input.desc "The .nupkg to check"
            |> Input.helpName "PATH"

        let directory =
            Input.option<string> "--directory"
            |> Input.def "."
            |> Input.acceptLegalFilePathsOnly
            |> Input.desc "Directory to write Directory.Build.targets into"
            |> Input.helpName "PATH"

    /// <summary>
    /// The generation itself. Shared by the stage and the CLI command, so neither can drift from
    /// the other.
    /// </summary>
    /// <remarks>
    /// Skipped members warn by default and fail under <c>--strict</c>; a skip means those
    /// annotations are silently absent from the output, which is exactly the failure this is meant
    /// to surface.
    /// </remarks>
    /// <summary>
    /// Turns the repeatable <c>--attribute</c> into a filter. Empty means the whole
    /// <c>JetBrains.Annotations</c> namespace, which is what an assembly's consumers want; naming
    /// attributes narrow it.
    /// </summary>
    let private filterOf (attributes: string array) =
        if Array.isEmpty attributes
        then Partas.ExternalAnnotations.AttributeFilter.JetBrains
        else Partas.ExternalAnnotations.AttributeFilter.Named (List.ofArray attributes)

    let private runGenerate (filter: Partas.ExternalAnnotations.AttributeFilter) (strict: bool) (assembly: string) (output: string) =
        let result = Partas.ExternalAnnotations.generateWith filter [] assembly output

        printfn
            $"external annotations: %d{result.Members} members, %d{result.Sites} sites, %d{result.Types} types scanned"

        match result.Skipped with
        | [] -> ()
        | skipped ->
            for name, reason in List.truncate 10 skipped do
                printfn $"  skipped %s{name} -- %s{reason}"

            if strict then
                failwith
                    $"%d{skipped.Length} member(s) skipped and --strict was given; their annotations are absent from %s{output}"

    let [<Literal>] private GenerateStageName = "external annotations"

    /// <summary>
    /// Generates an external annotations file from <paramref name="assembly" />, for a pipeline
    /// that already knows both paths.
    /// </summary>
    /// <remarks>Belongs after the build and before the pack.</remarks>
    /// <param name="assembly">The assembly to scan for annotations.</param>
    /// <param name="output">The file to write to.</param>
    let generateTo (assembly: string) (output: string) =
        input {
            let! strict = Options.strict
            and! attributes = Options.attribute

            return
                stage GenerateStageName {
                    run (fun _ -> runGenerate (filterOf attributes) strict assembly output)
                }
        }

    /// <summary>
    /// As <c>generateTo</c>, but with the attribute set fixed by the pipeline rather than left to
    /// <c>--attribute</c>.
    /// </summary>
    let generateOnlyTo (filter: Partas.ExternalAnnotations.AttributeFilter) (assembly: string) (output: string) =
        input {
            let! strict = Options.strict

            return
                stage GenerateStageName { run (fun _ -> runGenerate filter strict assembly output) }
        }

    /// <summary>The same generation, with both paths taken from the command line.</summary>
    let generateStage =
        input {
            let! strict = Options.strict
            and! attributes = Options.attribute
            and! assembly = Options.assembly
            and! output = Options.output

            return
                stage GenerateStageName {
                    run (fun _ -> runGenerate (filterOf attributes) strict assembly output)
                }
        }

    /// <summary>
    /// Asserts that every assembly in <paramref name="nupkg" /> has an annotations sidecar beside
    /// it.
    /// </summary>
    /// <remarks>
    /// The characteristic failure here is silent absence — a green build and a package quietly
    /// missing the file — so this checks the artifact a consumer actually downloads rather than
    /// any proxy for it.
    /// </remarks>
    /// <param name="nupkg">The .nupkg to check.</param>
    /// <param name="minMembers">The minimum number of members the sidecar must annotate.</param>
    /// <exception cref="System.IO.FileNotFoundException">The file does not exist.</exception>
    let private runVerify (minMembers: int) (nupkg: string) =
        use archive = ZipFile.OpenRead nupkg

        let assemblies =
            archive.Entries
            |> Seq.filter (fun entry -> entry.FullName.StartsWith "lib/" && entry.FullName.EndsWith ".dll")
            |> Seq.map _.FullName
            |> Seq.toList

        let sidecarOf (assembly: string) =
            assembly.Substring (0, assembly.Length - ".dll".Length)
            + ".ExternalAnnotations.xml"

        /// None when the sidecar is absent; otherwise how many members it annotates.
        let memberCount (assembly: string) =
            match archive.GetEntry (sidecarOf assembly) with
            | null -> None
            | entry ->
                use stream = entry.Open ()
                let doc = XDocument.Load stream
                doc.Descendants (XName.Get "member") |> Seq.length |> Some

        let counted = assemblies |> List.map (fun assembly -> assembly, memberCount assembly)

        // An assembly with no annotations legitimately yields an empty file, so an empty one is
        // only an error when the caller says how many it expects. Absence is always an error.
        let missing =
            counted
            |> List.filter (fun (_, count) ->
                match count with
                | None -> true
                | Some count -> count < minMembers)

        match assemblies, missing with
        | [], _ -> failwith $"{nupkg} contains no lib/ assemblies to verify"
        | _, [] ->
            let total = counted |> List.sumBy (snd >> Option.defaultValue 0)

            printfn
                $"verified annotations for %d{assemblies.Length} assemblies in {Path.GetFileName nupkg} (%d{total} members)"
        | _, missing ->
            let detail =
                missing
                |> List.map (fun (assembly, count) ->
                    match count with
                    | None -> $"{sidecarOf assembly} (missing)"
                    | Some 1 -> $"{sidecarOf assembly} (1 member, expected at least {minMembers})"
                    | Some count -> $"{sidecarOf assembly} ({count} members, expected at least {minMembers})")
                |> String.concat ", "

            failwith $"{Path.GetFileName nupkg} failed the external annotations check: {detail}"

    let [<Literal>] private VerifyStageName = "verify annotations"

    /// <summary>Checks the annotations in <paramref name="nupkg" />, which the pipeline names.</summary>
    /// <param name="nupkg">The .nupkg to check.</param>
    let verifyPackage (nupkg: string) =
        stage VerifyStageName { run (fun _ -> runVerify 0 nupkg) }

    /// <summary>As <c>verifyPackage</c>, but also fails a sidecar that annotates too few members.</summary>
    let verifyPackageOf (minMembers: int) (nupkg: string) =
        stage VerifyStageName { run (fun _ -> runVerify minMembers nupkg) }

    /// <summary>The same check, with the package taken from the command line.</summary>
    let verifyStage =
        input {
            let! package = Options.package
            and! minMembers = Options.minMembers

            return stage VerifyStageName { run (fun _ -> runVerify minMembers package) }
        }

    /// <summary>
    /// Arguments that inject the MSBuild logic into a <c>dotnet pack</c> without any file being
    /// committed to the project being packed.
    /// </summary>
    /// <remarks>
    /// The normal route is a committed <c>Directory.Build.targets</c> written by <c>init</c>, so
    /// this is for packing projects you do not own. The path must be absolute — MSBuild resolves
    /// it per project otherwise.
    /// </remarks>
    let packArgs (targetsFile: string) =
        [ $"-p:CustomAfterMicrosoftCommonTargets={Path.GetFullPath targetsFile}" ]

    /// <summary>
    /// Writes the MSBuild logic to <paramref name="path" />, optionally pinning the generator
    /// command inside it.
    /// </summary>
    /// <param name="path">The file to write to.</param>
    /// <param name="toolCommand">The command that generates annotations during pack, if any.</param>
    let writeTargets (path: string) (toolCommand: string option) =
        let content = Assets.readTargets () |> Assets.withToolCommand toolCommand

        match Path.GetDirectoryName path with
        | null
        | "" -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        File.WriteAllText (path, content)

    /// <summary>
    /// Installs the MSBuild logic as <c>Directory.Build.targets</c> in
    /// <paramref name="directory" />.
    /// </summary>
    /// <remarks>
    /// The result is meant to be **committed**: it is ordinary build configuration, and having it
    /// in the repository is what makes every pack correct, including Rider's Pack button and a
    /// bare <c>dotnet pack</c>. Deliberately not run from CI, which would make package contents
    /// differ between machines with nothing to show for it.
    /// </remarks>
    /// <param name="toolCommand">The command that generates annotations during pack, if any.</param>
    /// <param name="force">Whether to overwrite an existing file.</param>
    /// <param name="directory">The directory to write the file into.</param>
    let private runInit (toolCommand: string option) (force: bool) (directory: string) =
        let path = Path.Combine (directory, "Directory.Build.targets")

        if File.Exists path && not force then
            failwith $"{path} already exists. Merge it by hand, or pass --force to overwrite."

        writeTargets path toolCommand
        printfn $"wrote {path}"
        printfn "commit this file; it is what makes annotations ship for every pack"

    let [<Literal>] private InitStageName = "init annotations"

    /// <summary>Writes the targets into <paramref name="directory" />, which the pipeline names.</summary>
    /// <param name="directory">The directory to write the file into.</param>
    let initIn (directory: string) =
        input {
            let! toolCommand = Options.annotationsTool
            and! force = Options.force

            return stage InitStageName { run (fun _ -> runInit toolCommand force directory) }
        }

    /// <summary>The same, with the directory taken from the command line (defaulting to the CWD).</summary>
    let initStage =
        input {
            let! toolCommand = Options.annotationsTool
            and! force = Options.force
            and! directory = Options.directory

            return stage InitStageName { run (fun _ -> runInit toolCommand force directory) }
        }

    /// <summary>
    /// The commands, ready to hand to <c>addCommands</c>. The tool is nothing but these three; a
    /// build CLI can equally adopt one without taking on the others.
    /// </summary>
    let generateCommand =
        command "generate" {
            description "Generates a ReSharper external annotations file from an assembly"

            pipeline "generate" { generateStage }
        }

    /// <summary>Fails the build when a package would ship without annotations.</summary>
    let verifyCommand =
        command "verify" {
            description "Fails unless every assembly in a .nupkg has an annotations sidecar"

            pipeline "verify" { verifyStage }
        }

    /// <summary>Writes the Directory.Build.targets that makes every subsequent pack correct.</summary>
    let initCommand =
        command "init" {
            description "Writes the external annotations Directory.Build.targets (commit the result)"

            pipeline "init" { initStage }
        }
