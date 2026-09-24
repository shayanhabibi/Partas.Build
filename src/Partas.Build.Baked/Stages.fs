/// <summary>Ready-made stages for the steps every .NET build CLI repeats.</summary>
/// <remarks>
/// Each prefab is a function answering an <c>InputSpec&lt;StageContext&gt;</c>, which a <c>pipeline</c>, a
/// <c>command</c> or an enclosing <c>stage</c> yields directly and whose options reach the command's
/// <c>--help</c>. A prefab reads Baked's own options (<see cref="P:Partas.Build.Baked.Common.quick"/>,
/// <see cref="P:Partas.Build.Baked.Common.skipTests"/>, <see cref="P:Partas.Build.Baked.Common.isCI"/>,
/// <see cref="P:Partas.Build.Baked.Dotnet.configOrRelease"/>, <see cref="P:Partas.Build.Baked.NuGet.apiKey"/>);
/// its <c>…With</c> counterpart takes each of those as an <c>InputSpec</c> instead, for a consumer with options
/// of its own. A skip condition carries a reason for <c>--explain</c>, naming the option when the spec reads
/// exactly one.
/// <para>
/// A prefab's stage is an ordinary <c>StageContext</c>: <c>InputSpec.map</c> over it to rename it
/// (<c>{ s with Name = … }</c>), to toggle its parallelism (<c>StageContext.toggleParallel</c>) or to add a
/// condition (<c>StageContext.addPredicateBecause</c>).
/// </para>
/// </remarks>
/// <example>
/// A whole build CLI. <c>test --help</c> lists <c>--quick</c>, <c>--configuration</c>, <c>--skip-tests</c> and
/// <c>--ci</c>; <c>publish --help</c> lists <c>--configuration</c> and <c>--nuget-key</c>:
/// <code lang="fsharp">
/// open Partas.Build.Baked
///
/// let projects = [ "src/MyLib/MyLib.fsproj" ]
///
/// rootCommandOfScript {
///     command "test" {
///         Command.pipeline {
///             Stages.restore "MyLib.slnx"
///             Stages.clean [ "**/bin" ] [ "*.nupkg" ]
///             Stages.build projects
///             Stages.expecto "tests/MyLib.Tests/MyLib.Tests.fsproj" [ "--sequenced" ]
///         }
///     }
///     command "publish" {
///         Command.pipeline {
///             Stages.build projects
///             Stages.pack "bin" projects
///             Stages.nugetPush "bin/*.nupkg"
///         }
///     }
/// }
/// |> exit
/// </code>
/// </example>
module Partas.Build.Baked.Stages

open System.IO
open Partas.Build

/// The text <c>--explain</c> shows for a stage skipped because <paramref name="skip"/> read <c>true</c>.
let private skipReason (skip: InputSpec<bool>) =
    match skip.Inputs |> List.map _.Source with
    | [ ParsedOption option ] -> $"%s{option.Name} is set"
    | _ -> "its skip condition holds"

/// Binds <paramref name="skip"/> and skips the stage <paramref name="spec"/> answers when it reads <c>true</c>.
let private skippedBy (skip: InputSpec<bool>) (spec: InputSpec<StageContext>) : InputSpec<StageContext> =
    let reason = ValueSome (skipReason skip)
    InputSpec.map2 (fun skipped stage -> StageContext.addPredicateBecause reason (fun _ -> not skipped) stage) skip spec

let private quick = InputSpec.ofInput Common.quick
let private skipTests = InputSpec.ofInput Common.skipTests
let private ci = InputSpec.ofInput Common.isCI

/// A stage's name for a project: the file name without its extension.
let private projectName (project: string) = Path.GetFileNameWithoutExtension project

/// <summary><c>restore</c>: <c>dotnet tool restore</c>, then <c>dotnet restore</c> of
/// <paramref name="solution"/>; skipped when <paramref name="skip"/> reads <c>true</c>.</summary>
let restoreWith (skip: InputSpec<bool>) (solution: string) =
    stage "restore" {
        run (cmd $"dotnet tool restore --verbosity q")
        run (cmd $"dotnet restore {solution}")
    }
    |> InputSpec.ret
    |> skippedBy skip

/// <summary><see cref="M:Partas.Build.Baked.Stages.restoreWith"/>, skipped by <c>--quick</c>.</summary>
let restore (solution: string) = restoreWith quick solution

/// <summary><c>clean</c>: empties the directories <paramref name="directories"/> select and deletes the files
/// <paramref name="files"/> select, both relative to the stage's working directory; skipped when
/// <paramref name="skip"/> reads <c>true</c>.</summary>
/// <remarks>
/// Patterns follow <see cref="T:Partas.Build.Baked.Clean"/>: <c>[ "**/bin"; "!bin"; "tmp" ]</c> empties every
/// <c>bin</c> directory except the root's, and creates or empties <c>tmp</c>. The step's label, shown by
/// <c>--explain</c>, lists the patterns.
/// </remarks>
let cleanWith (skip: InputSpec<bool>) (directories: string list) (files: string list) =
    let label =
        [ if not directories.IsEmpty then "empty " + String.concat " " directories
          if not files.IsEmpty then "delete " + String.concat " " files ]
        |> String.concat "; "
    let step (ctx: StageContext) (_: StepIndex) = async {
        let root = StageContext.getWorkingDir ctx |> ValueOption.defaultWith Directory.GetCurrentDirectory
        let emptied, deleted = Clean.run root directories files
        StageContext.writeLine ctx StdStream.Out $"emptied %d{emptied.Length} directories, deleted %d{deleted.Length} files under %s{root}"
        return Ok ()
    }
    stage "clean" { () }
    |> StageContext.addLabelledStepFn label step
    |> InputSpec.ret
    |> skippedBy skip

/// <summary><see cref="M:Partas.Build.Baked.Stages.cleanWith"/>, skipped by <c>--quick</c>.</summary>
let clean (directories: string list) (files: string list) = cleanWith quick directories files

/// <summary><c>build</c>: one <c>dotnet build &lt;project&gt; -c &lt;configuration&gt;</c> sub-stage per
/// project, run under <c>parallel'</c>.</summary>
let buildWith (configuration: InputSpec<string>) (projects: string list) = input {
    let! configuration = configuration
    return stage "build" {
        parallel'
        for project in projects do
            stage $"build %s{projectName project}" { run (cmd $"dotnet build {project} -c {configuration}") }
    }
}

/// <summary><see cref="M:Partas.Build.Baked.Stages.buildWith"/> in the <c>--configuration</c>, <c>Release</c>
/// by default.</summary>
let build (projects: string list) = buildWith Dotnet.configOrRelease projects

/// <summary><c>pack</c>: one <c>dotnet pack --no-build --no-restore</c> sub-stage per project, writing to
/// <paramref name="outDir"/>, run under <c>parallel'</c>.</summary>
/// <remarks>Packs what a <c>build</c> in the same configuration left behind.</remarks>
let packWith (configuration: InputSpec<string>) (outDir: string) (projects: string list) = input {
    let! configuration = configuration
    return stage "pack" {
        parallel'
        for project in projects do
            stage $"pack %s{projectName project}" {
                run (cmd $"dotnet pack {project} -c {configuration} --no-build --no-restore -o {outDir}")
            }
    }
}

/// <summary><see cref="M:Partas.Build.Baked.Stages.packWith"/> in the <c>--configuration</c>, <c>Release</c>
/// by default.</summary>
let pack (outDir: string) (projects: string list) = packWith Dotnet.configOrRelease outDir projects

/// <summary><c>test &lt;project&gt;</c>: runs an already built Expecto suite through
/// <c>dotnet run --no-build</c>, passing <paramref name="arguments"/> after <c>--</c>; skipped when
/// <paramref name="skip"/> reads <c>true</c>.</summary>
/// <remarks>
/// The suite runs through <c>dotnet run --no-build --project</c>: the test assembly is the one MSBuild resolves for
/// <paramref name="project"/> in <paramref name="configuration"/>.
/// When <paramref name="isCI"/> reads <c>true</c>, the suite also takes <c>--summary</c> and its output is
/// captured: a passing run prints nothing, and a failing one carries the whole output in its error.
/// </remarks>
/// <example>
/// A suite skipped by a consumer's own <c>--fast</c> flag instead of <c>--skip-tests</c>:
/// <code lang="fsharp">
/// let fast = Input.option&lt;bool&gt; "--fast" |> InputSpec.ofInput
///
/// Stages.expectoWith fast Dotnet.configOrRelease (InputSpec.ofInput Common.isCI) "tests/Unit/Unit.fsproj" []
/// </code>
/// </example>
let expectoWith (skip: InputSpec<bool>) (configuration: InputSpec<string>) (isCI: InputSpec<bool>) (project: string) (arguments: string list) =
    input {
        let! configuration = configuration
        and! isCI = isCI
        return
            stage $"test %s{projectName project}" {
                run (
                    cmd $"dotnet run --project {project} --no-build -c {configuration} --"
                    |> Cmd.argIf isCI [ "--summary" ]
                    |> Cmd.args arguments
                )
            }
            |> StageContext.setOutput (if isCI then ValueSome (StageOutput.Captured (OutputCapture.create ())) else ValueNone)
    }
    |> skippedBy skip

/// <summary><see cref="M:Partas.Build.Baked.Stages.expectoWith"/> in the <c>--configuration</c>, skipped by
/// <c>--skip-tests</c>, summarised and captured under <c>--ci</c>.</summary>
let expecto (project: string) (arguments: string list) = expectoWith skipTests Dotnet.configOrRelease ci project arguments

/// <summary><c>push</c>: <c>dotnet nuget push &lt;packages&gt; --skip-duplicate</c>, to
/// <paramref name="source"/> with the key when <paramref name="apiKey"/> reads one, and to
/// <paramref name="localSource"/> otherwise.</summary>
/// <remarks>
/// The key is a secret argument of the command: every printed form of the step, <c>--explain</c> included,
/// shows it as <c>***</c>. <paramref name="packages"/> is passed through for <c>dotnet nuget push</c> to expand,
/// so <c>bin/*.nupkg</c> pushes every package there.
/// </remarks>
let nugetPushWith (apiKey: InputSpec<string option>) (localSource: string) (source: string) (packages: string) = input {
    let! apiKey = apiKey
    return stage "push" {
        match apiKey with
        | Some key ->
            stage "push to source" {
                run (
                    cmd $"dotnet nuget push {packages} --source {source}"
                    |> Cmd.secretOption "--api-key" key
                    |> Cmd.arg "--skip-duplicate"
                )
            }
        | None -> stage "push to local feed" { run (cmd $"dotnet nuget push {packages} --source {localSource} --skip-duplicate") }
    }
}

/// <summary><see cref="M:Partas.Build.Baked.Stages.nugetPushWith"/> with the <c>--nuget-key</c> option
/// (<c>NUGET_API_KEY</c> by default), pushing to nuget.org, or to the NuGet source named <c>local</c> without a
/// key.</summary>
let nugetPush (packages: string) =
    nugetPushWith (InputSpec.ofInput NuGet.apiKey.option) "local" "https://api.nuget.org/v3/index.json" packages

/// <summary><c>format</c>: <c>dotnet fantomas</c> over <paramref name="paths"/>, only checking them when
/// <paramref name="check"/> is set; skipped when <paramref name="skip"/> reads <c>true</c>.</summary>
/// <remarks>Fantomas is expected as a local tool, restored by <c>restore</c>.</remarks>
let fantomasWith (skip: InputSpec<bool>) (check: bool) (paths: string list) =
    stage "format" {
        run (cmd $"dotnet fantomas" |> Cmd.argIf check [ "--check" ] |> Cmd.args paths)
    }
    |> InputSpec.ret
    |> skippedBy skip

/// <summary><see cref="M:Partas.Build.Baked.Stages.fantomasWith"/>, rewriting the files, skipped by
/// <c>--quick</c>.</summary>
let fantomas (paths: string list) = fantomasWith quick false paths

/// <summary><c>npm install</c>: installs the packages of <paramref name="directory"/> through
/// <c>npm ci</c> when <paramref name="isCI"/> reads <c>true</c> and <c>npm install</c> otherwise; skipped when
/// <paramref name="skip"/> reads <c>true</c>.</summary>
let npmInstallWith (skip: InputSpec<bool>) (isCI: InputSpec<bool>) (directory: string) =
    input {
        let! isCI = isCI
        return stage "npm install" { run (cmd $"""npm {if isCI then "ci" else "install"} --prefix {directory}""") }
    }
    |> skippedBy skip

/// <summary><see cref="M:Partas.Build.Baked.Stages.npmInstallWith"/>, <c>npm ci</c> under <c>--ci</c>, skipped by
/// <c>--quick</c>.</summary>
let npmInstall (directory: string) = npmInstallWith quick ci directory
