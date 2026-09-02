module Partas.Build.Baked

open Partas.Build

module Types =
    module DotNet =
        [<Struct>]
        type Configuration =
            | Release
            | Debug
            override this.ToString() =
                match this with
                | Release -> "Release"
                | Debug -> "Debug"
    module Versioning =
        [<Struct>]
        type PreReleaseType =
            | Alpha
            | Beta
            | ReleaseCandidate
            | Preview
        [<Struct>]
        type Bump =
            | Major
            | Minor
            | Patch
            | PreRelease of preReleaseType: PreReleaseType
            | Target of target: string

open Types

module private Common =
    module NuGet =
        let apiKey<'T>: ActionInput<'T> -> _ =
            Input.arity Arity.ExactlyOne
            >> Input.desc "NuGet API key"
            >> Input.helpName "APIKEY"
    module DotNet =
        let config<'T>: ActionInput<'T> -> _ =
            Input.desc "Build configuration"
            >> Input.arity Arity.ExactlyOne
            >> Input.helpName "Debug|Release"
            >> Input.acceptOnlyFromAmong [ nameof DotNet.Release; nameof DotNet.Debug ]
        let parsedConfig<'T> remapper: ActionInput<'T> -> _ =
            config
            >> Input.customParser (
                fun tok ->
                    if tok.Tokens.Count <> 1 then tok.AddError("Requires exactly one argument."); None else
                    match tok.Tokens[0].Value with
                    | "Debug" -> Some DotNet.Debug
                    | "Release" -> Some DotNet.Release
                    | v -> tok.AddError $"Invalid value '%s{v}'. Must be one of <Release|Debug>"; None
                >> remapper
                )
    module Versioning =
        let bump<'T> remapper: ActionInput<'T> -> _ =
            Input.desc "Bump version by type. Defaults to patch. [major|minor|patch|alpha|beta|rc|preview|<SEMVER>]"
            >> Input.helpName "BUMP_TYPE"
            >> Input.customParser (
                fun result ->
                    if result.Tokens.Count = 0 then Some Versioning.Bump.Patch else
                    match result.Tokens[0].Value with
                    | "major" | "Major" | "MAJOR" | "M" -> Some Versioning.Bump.Major
                    | "minor" | "Minor" | "MINOR" | "m" -> Some Versioning.Bump.Minor
                    | "patch" | "Patch" | "PATCH" | "p" -> Some Versioning.Bump.Patch
                    | "prelease" | "Prerelease" | "PRERELEASE" | "pr" -> Some (Versioning.Bump.PreRelease Versioning.PreReleaseType.Alpha)
                    | "alpha" | "Alpha" | "ALPHA" | "a" -> Some (Versioning.Bump.PreRelease Versioning.PreReleaseType.Alpha)
                    | "beta" | "Beta" | "BETA" | "b" -> Some (Versioning.Bump.PreRelease Versioning.PreReleaseType.Beta)
                    | "rc" | "RC" | "RC" -> Some (Versioning.Bump.PreRelease Versioning.PreReleaseType.ReleaseCandidate)
                    | "preview" | "Preview" | "PREVIEW" | "pv" -> Some (Versioning.Bump.PreRelease Versioning.PreReleaseType.Preview)
                    | value -> Some <| Versioning.Bump.Target value
                >> remapper
                )
    module Project =
        // The custom parser is not optional: `Option<string list>`/`Argument<string list>` have no built-in
        // converter, so without one every token fails to bind at parse time.
        let target listOfTargets: ActionInput<string list> -> _ =
            Input.arity Arity.OneOrMore
            >> Input.desc "Target project(s)"
            >> Input.acceptOnlyFromAmong listOfTargets
            >> Input.allowMultipleArgumentsPerToken
            >> Input.customParser (fun result -> result.Tokens |> Seq.map _.Value |> List.ofSeq)


[<RequireQualifiedAccess>]
module Input =
    module NuGet =
        let apiKey =
            Input.optionMaybe<string> "--nuget-key"
            |> Input.alias "--nuget"
            |> Common.NuGet.apiKey
        let apiKeyOrEnv =
            Input.optionMaybe<string> "--nuget-key"
            |> Input.alias "--nuget"
            |> Common.NuGet.apiKey
            |> Input.def (
                    try
                    let result = System.Environment.GetEnvironmentVariable "NUGET_API_KEY"
                    if System.String.IsNullOrEmpty result then None else Some result
                    with _ -> None
                )

    module DotNet =
        let config =
            Input.optionMaybe<DotNet.Configuration> "--configuration"
            |> Input.alias "-c"
            |> Input.arity Arity.ExactlyOne
            |> Common.DotNet.parsedConfig id
        let configString =
            Input.optionMaybe<string> "--configuration"
            |> Input.alias "-c"
            |> Input.arity Arity.ExactlyOne
            |> Common.DotNet.config

    module Versioning =
        let bump =
            Input.optionMaybe<Versioning.Bump> "--bump"
            |> Input.arity Arity.ZeroOrOne
            |> Common.Versioning.bump (Option.orElse (Some Versioning.Bump.Patch))

    module Project =
        let target targets =
            Input.option<string list> "--project"
            |> Input.alias "-p"
            |> Input.arity Arity.OneOrMore
            |> Common.Project.target targets

    module CI =
        let isCI =
            Input.option<bool> "--ci"
            |> Input.desc "Indicates that the build is running in a CI environment; defaults to true if environment variables indicate so"
            |> Input.def (
                let vars = System.Environment.GetEnvironmentVariables()
                vars.Contains "CI"
                || vars.Contains "TRAVIS"
                || vars.Contains "CIRCLECI"
                || vars.Contains "BUILD_ID"
                || vars.Contains "GITLAB_CI"
                || vars.Contains "GITHUB_ACTIONS"
                )

[<RequireQualifiedAccess>]
module Argument =
    module NuGet =
        let apiKey =
            Input.argument<string> "nuget-key"
            |> Common.NuGet.apiKey
            |> Input.required
        let apiKeyOrEnv =
            Input.argumentMaybe<string> "nuget-key"
            |> Common.NuGet.apiKey
            |> Input.def (
                    try
                    let result = System.Environment.GetEnvironmentVariable "NUGET_API_KEY"
                    if System.String.IsNullOrEmpty result then None else Some result
                    with _ -> None
                )
            |> Input.required

    module DotNet =
        let config =
            Input.argument<DotNet.Configuration> "configuration"
            |> Common.DotNet.parsedConfig (Option.defaultValue DotNet.Release)
            |> Input.required
        let configString =
            Input.argument<string> "configuration"
            |> Common.DotNet.config
            |> Input.def "Release"
            |> Input.required

    module Versioning =
        // ZeroOrOne rather than the argument default of ExactlyOne, so that omitting it falls through to
        // the parser's `patch` rather than failing with a missing-argument error.
        let bump =
            Input.argument<Versioning.Bump> "bump"
            |> Common.Versioning.bump (Option.defaultValue Versioning.Bump.Patch)
            |> Input.arity Arity.ZeroOrOne
            |> Input.def Versioning.Bump.Patch
            |> Input.required

    module Project =
        let target targets =
            Input.argument<string list> "project"
            |> Common.Project.target targets
            |> Input.arity Arity.OneOrMore
            |> Input.def []
            |> Input.required

/// <summary>
/// Semantic version arithmetic for <see cref="T:Partas.Build.Baked.Types.Versioning.Bump"/>.
/// </summary>
/// <remarks>
/// Build metadata (<c>+sha</c>) is dropped, and a pre-release tag is read as <c>label.number</c>:
/// anything that does not end in a number counts as number 0, so <c>1.2.3-beta</c> bumps to <c>1.2.3-beta.1</c>.
/// </remarks>
module Version =
    open System

    /// What an absent or unparseable <c>&lt;Version&gt;</c> is bumped from.
    [<Literal>]
    let zero = "0.0.0"

    let label preReleaseType =
        match preReleaseType with
        | Versioning.Alpha -> "alpha"
        | Versioning.Beta -> "beta"
        | Versioning.ReleaseCandidate -> "rc"
        | Versioning.Preview -> "preview"

    type private Parsed = { Major: int; Minor: int; Patch: int; Label: string; Number: int }

    let private parse (version: string) =
        let version =
            match version.IndexOf '+' with
            | -1 -> version
            | i -> version.Substring(0, i)

        let core, pre =
            match version.IndexOf '-' with
            | -1 -> version, ""
            | i -> version.Substring(0, i), version.Substring(i + 1)

        let parts = core.Split '.'

        let number i =
            if parts.Length <= i then 0 else
            match Int32.TryParse (parts[i].Trim()) with
            | true, value -> value
            | _ -> 0

        let label, count =
            if pre = "" then "", 0 else
            let segments = pre.Split '.'
            match Int32.TryParse segments[segments.Length - 1] with
            | true, value when segments.Length > 1 -> String.Join(".", segments, 0, segments.Length - 1), value
            | true, value -> "", value
            | _ -> pre, 0

        { Major = number 0; Minor = number 1; Patch = number 2; Label = label; Number = count }

    let private render parsed =
        let core = $"%d{parsed.Major}.%d{parsed.Minor}.%d{parsed.Patch}"
        if parsed.Label = "" then core else $"%s{core}-%s{parsed.Label}.%d{parsed.Number}"

    /// <summary>The assembly version that goes with <paramref name="version"/>: its major, and nothing else.</summary>
    /// <remarks>
    /// Deliberately not <paramref name="version"/> itself. An assembly's version is its identity to everything
    /// already compiled against it, so moving it on a patch bump means anything not rebuilt in the same pass
    /// fails at load with <c>Could not load file or assembly '&lt;name&gt;, Version=…'</c>. Holding it at the major
    /// keeps that to the one bump where the contract is allowed to break, while the package version moves freely.
    /// </remarks>
    let assembly (version: string) =
        let major =
            match version.Split([| '.'; '-'; '+' |]) with
            | [||] -> "0"
            | parts ->
                match Int32.TryParse (parts[0].Trim()) with
                | true, value -> string value
                | _ -> "0"

        $"%s{major}.0.0.0"

    /// <summary>Applies <paramref name="bump"/> to <paramref name="version"/>.</summary>
    /// <remarks>
    /// <c>major</c>/<c>minor</c> zero the fields below them, and both they and <c>patch</c> drop any pre-release
    /// tag — so <c>patch</c> on <c>1.2.3-rc.2</c> releases it as <c>1.2.3</c> rather than moving to <c>1.2.4</c>.
    /// A pre-release bump increments the counter when the label is unchanged and otherwise starts a new one,
    /// opening a fresh patch if the version was a release. <c>Target</c> is taken verbatim, unparsed.
    /// </remarks>
    let apply bump (version: string) =
        match bump with
        | Versioning.Target target -> target
        | _ ->

        let parsed = parse version

        match bump with
        | Versioning.Target target -> target
        | Versioning.Major -> render { parsed with Major = parsed.Major + 1; Minor = 0; Patch = 0; Label = ""; Number = 0 }
        | Versioning.Minor -> render { parsed with Minor = parsed.Minor + 1; Patch = 0; Label = ""; Number = 0 }
        | Versioning.Patch ->
            if parsed.Label <> ""
            then render { parsed with Label = ""; Number = 0 }
            else render { parsed with Patch = parsed.Patch + 1 }
        | Versioning.PreRelease preReleaseType ->
            let next = label preReleaseType
            if parsed.Label = next then render { parsed with Number = parsed.Number + 1 }
            elif parsed.Label <> "" then render { parsed with Label = next; Number = 1 }
            else render { parsed with Patch = parsed.Patch + 1; Label = next; Number = 1 }

module IO =
    open System.IO
    open System.Text
    open System.Xml
    open System.Xml.Linq

    /// <summary>Writes <paramref name="doc"/> back over the file it was loaded from.</summary>
    /// <remarks>
    /// Not <c>XDocument.Save(path)</c>: that prepends an <c>&lt;?xml ?&gt;</c> declaration no project file has
    /// and encodes with a byte-order mark whether or not the original had one, so a one-line version change
    /// lands in review as a rewrite of the first line and of the file's encoding.
    /// </remarks>
    let private save (projPath: string) (doc: XDocument) =
        let hasByteOrderMark =
            use stream = File.OpenRead projPath
            let head = Array.zeroCreate 3
            stream.Read(head, 0, 3) = 3 && head[0] = 0xEFuy && head[1] = 0xBBuy && head[2] = 0xBFuy

        let settings = XmlWriterSettings(OmitXmlDeclaration = true, Indent = false, Encoding = UTF8Encoding hasByteOrderMark)
        use writer = XmlWriter.Create(projPath, settings)
        doc.Save writer

    /// Sets one MSBuild property in the first `PropertyGroup`, adding the element if it is not there yet,
    /// and answers what it held before.
    let private writeProperty (propertyGroup: XElement) (property: string) (map: string option -> string) =
        match propertyGroup.Element(XName.Get property) with
        | null ->
            propertyGroup.Add(XElement(XName.Get property, map None))
            None
        | element ->
            let previous = Some element.Value
            element.Value <- map previous
            previous

    /// <summary>Rewrites <c>&lt;Version&gt;</c> and the <c>&lt;AssemblyVersion&gt;</c> that follows from it.</summary>
    /// <returns>What <c>&lt;Version&gt;</c> held before, if it held anything.</returns>
    /// <remarks>
    /// Both are written in one pass so the project file states its own identity outright, rather than leaving
    /// <c>AssemblyVersion</c> to be derived by an MSBuild rule somewhere up the directory tree. See
    /// <see cref="M:Partas.Build.Baked.Version.assembly"/> for why the two are not the same string.
    /// </remarks>
    let writeVersion (projPath: string) (versionMap: string option -> string) =
        let projFile = FileInfo(projPath)
        if not <| projFile.Exists then Error(FileNotFoundException() :> exn)
        elif projFile.Extension <> ".fsproj" then Error(FileLoadException("Not a .fsproj file.") :> exn) else
        try
        let doc = XDocument.Load(projPath, LoadOptions.PreserveWhitespace)
        let pg = doc.Root.Elements(XName.Get "PropertyGroup") |> Seq.head
        let mutable next = ""
        let prevMaybe =
            writeProperty pg "Version" (fun previous ->
                next <- versionMap previous
                next)
        writeProperty pg "AssemblyVersion" (fun _ -> Version.assembly next) |> ignore
        save projPath doc
        Ok(prevMaybe)
        with e -> Error e
    let setVersion projPath version = writeVersion projPath (fun _ -> version)

    /// <summary>Applies <paramref name="bump"/> to the project's <c>&lt;Version&gt;</c>, in place.</summary>
    /// <returns>The version before and after the bump.</returns>
    /// <remarks>A project with no <c>&lt;Version&gt;</c> is bumped from <see cref="F:Partas.Build.Baked.Version.zero"/>.</remarks>
    let bumpVersion projPath bump =
        let mutable next = Version.zero

        writeVersion projPath (fun previous ->
            next <- Version.apply bump (defaultArg previous Version.zero)
            next)
        |> Result.map (fun previous -> defaultArg previous Version.zero, next)

/// Ready-made stages for the commands every build CLI ends up wanting.
module Pipelines =

    /// <summary>The <c>bump</c> stage, over whichever source supplies the bump kind.</summary>
    /// <remarks>
    /// <paramref name="bumpSource"/> and <paramref name="projects"/> arrive as sources rather than as read
    /// values because <c>InputSpec</c> is applicative: a spec built inside <c>return</c> nests as
    /// <c>InputSpec&lt;InputSpec&lt;_&gt;&gt;</c>, and the inner <c>Inputs</c> are then unreachable without a
    /// <c>ParseResult</c> — the circularity the whole design exists to avoid (<c>PLAN.md</c>, finding 5).
    /// Every source is therefore bound in one <c>let!</c>/<c>and!</c> group here, and the callers below vary
    /// only which spec they hand in.
    /// </remarks>
    let private bumpImpl (bumpSource: InputSpec<Versioning.Bump>) (allProjects: string list) (projects: InputSpec<string list>) = input {
        let! ci = Input.CI.isCI
        and! bump = bumpSource
        and! projects = projects

        return stage "bump" {
            when' (not ci)
            run (fun (_: Internal.StageContext) ->
                match projects with
                | [ "all" ] when not <| List.isEmpty allProjects ->
                    allProjects
                | [ "all" ] -> []
                | projects -> projects
                |> List.map (fun project ->
                    match IO.bumpVersion project bump with
                    | Ok (previous, next) ->
                        printfn $"%s{project}: %s{previous} -> %s{next}"
                        Ok()
                    | Error error -> Error $"%s{project}: %s{error.Message}"
                    )
                |> List.tryPick (function Error _ as error -> Some error | Ok () -> None)
                |> Option.defaultValue (Ok())
            )
        }
    }

    /// <summary>
    /// The bump kind as an argument - `&lt;command> minor -p src/Foo` - defaulting to a patch when omitted.
    /// </summary>
    /// <param name="allProjects">
    /// List of paths to projects you want to bump if the project input includes "all" or is empty.
    /// Can be kept empty otherwise
    /// </param>
    /// <param name="projects">
    /// The input spec for the project path(s) to bump.
    /// </param>
    let bumpArgument (allProjects: string list) (projects: InputSpec<string list>): InputSpec<Internal.StageContext> =
        bumpImpl (InputSpec.ofInput Argument.Versioning.bump) allProjects projects

    /// <summary>
    /// The bump kind as an option - `&lt;command> --bump minor -p src/Foo`.
    /// No action if option is not present. Defaults to patch.
    /// </summary>
    /// <param name="allProjects">
    /// List of paths to projects you want to bump if the project input includes "all" or is empty.
    /// Can be kept empty otherwise
    /// </param>
    /// <param name="projects">
    /// The input spec for the project path(s) to bump.
    /// </param>
    let bumpOption (allProjects: string list) (projects: InputSpec<string list>): InputSpec<Internal.StageContext> =
        input {
            let! bump = Input.Versioning.bump
            and! bumpImpl =
                let source =
                    InputSpec.ofInput Input.Versioning.bump
                    |> InputSpec.map (Option.defaultValue Versioning.Bump.Patch)
                bumpImpl source allProjects projects
            return StageContext.addPredicate (fun _ -> bump.IsSome) bumpImpl
        }
