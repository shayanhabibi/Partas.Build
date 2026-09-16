module Partas.Build.Baked.SemVer

open System.IO
open System.Xml.Linq
open Partas.Build

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


let bump =
    BuildOptionInput.create "bump"
    |> BuildOption.createMaybe
    |> BuildOption.map (
        Input.desc "Bump version by type. Defaults to patch. [major|minor|patch|alpha|beta|rc|preview|<SEMVER>]"
        >> Input.helpName "BUMP_TYPE"
        >> Input.customParser (
            fun result ->
                if result.Tokens.Count = 0 then Some Bump.Patch else
                match result.Tokens[0].Value with
                | "major" | "Major" | "MAJOR" | "M" -> Some Bump.Major
                | "minor" | "Minor" | "MINOR" | "m" -> Some Bump.Minor
                | "patch" | "Patch" | "PATCH" | "p" -> Some Bump.Patch
                | "prelease" | "Prerelease" | "PRERELEASE" | "pr" -> Some (Bump.PreRelease PreReleaseType.Alpha)
                | "alpha" | "Alpha" | "ALPHA" | "a" -> Some (Bump.PreRelease PreReleaseType.Alpha)
                | "beta" | "Beta" | "BETA" | "b" -> Some (Bump.PreRelease PreReleaseType.Beta)
                | "rc" | "RC" | "RC" -> Some (Bump.PreRelease PreReleaseType.ReleaseCandidate)
                | "preview" | "Preview" | "PREVIEW" | "pv" -> Some (Bump.PreRelease PreReleaseType.Preview)
                | value -> Some <| Bump.Target value
            )
        )
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
        | Alpha -> "alpha"
        | Beta -> "beta"
        | ReleaseCandidate -> "rc"
        | Preview -> "preview"

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
    let apply (bump: Bump) (version: string) =
        match bump with
        | Target target -> target
        | _ ->

        let parsed = parse version

        match bump with
        | Target target -> target
        | Major -> render { parsed with Major = parsed.Major + 1; Minor = 0; Patch = 0; Label = ""; Number = 0 }
        | Minor -> render { parsed with Minor = parsed.Minor + 1; Patch = 0; Label = ""; Number = 0 }
        | Patch ->
            if parsed.Label <> ""
            then render { parsed with Label = ""; Number = 0 }
            else render { parsed with Patch = parsed.Patch + 1 }
        | PreRelease preReleaseType ->
            let next = label preReleaseType
            if parsed.Label = next then render { parsed with Number = parsed.Number + 1 }
            elif parsed.Label <> "" then render { parsed with Label = next; Number = 1 }
            else render { parsed with Patch = parsed.Patch + 1; Label = next; Number = 1 }

    module IO =
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
                IO.writeProperty pg "Version" (fun previous ->
                    next <- versionMap previous
                    next)
            IO.writeProperty pg "AssemblyVersion" (fun _ -> assembly next) |> ignore
            IO.save projPath doc
            Ok(prevMaybe)
            with e -> Error e

        let setVersion projPath version = writeVersion projPath (fun _ -> version)

        /// <summary>Applies bump to the project's <c>&lt;Version&gt;</c>, in place.</summary>
        /// <returns>The version before and after the bump.</returns>
        /// <remarks>A project with no <c>&lt;Version&gt;</c> is bumped from <see cref="F:Partas.Build.Baked.Version.zero"/>.</remarks>
        let bumpVersion projPath bump =
            let mutable next = zero

            writeVersion projPath (fun previous ->
                next <- apply bump (defaultArg previous zero)
                next)
            |> Result.map (fun previous -> defaultArg previous zero, next)

/// Ready-made stages for the commands every build CLI ends up wanting.
module Stages =

    /// <summary>The <c>bump</c> stage, over whichever source supplies the bump kind.</summary>
    /// <remarks>
    /// <paramref name="bumpSource"/> and <paramref name="projects"/> arrive as sources rather than as read
    /// values because <c>InputSpec</c> is applicative: a spec built inside <c>return</c> nests as
    /// <c>InputSpec&lt;InputSpec&lt;_&gt;&gt;</c>, and the inner <c>Inputs</c> are then unreachable without a
    /// <c>ParseResult</c> — the circularity the whole design exists to avoid (<c>PLAN.md</c>, finding 5).
    /// Every source is therefore bound in one <c>let!</c>/<c>and!</c> group here, and the callers below vary
    /// only which spec they hand in.
    /// </remarks>
    let private bumpImpl (bumpSource: InputSpec<Bump>) (projects: InputSpec<string list>) = input {
        let! ci = Common.isCI
        and! bump = bumpSource
        and! projects = projects

        return stage "bump" {
            when' (not ci)
            run (fun (_: Internal.StageContext) ->
                projects
                |> List.map (fun project ->
                    match Version.IO.bumpVersion project bump with
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
    let bumpArgument (projects: InputSpec<string list>): InputSpec<Internal.StageContext> =
        bumpImpl (InputSpec.ofInput bump.argument |> InputSpec.map (Option.defaultValue Patch)) projects

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
    let bumpOption (projects: InputSpec<string list>): InputSpec<Internal.StageContext> =
        input {
            let! bump = bump.option
            and! bumpImpl =
                let source =
                    InputSpec.ofInput bump.option
                    |> InputSpec.map (Option.defaultValue Patch)
                bumpImpl source projects
            return StageContext.addPredicate (fun _ -> bump.IsSome) bumpImpl
        }
