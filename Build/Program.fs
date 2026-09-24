/// <summary>
/// The build CLI, written against Partas.Build itself.
///
/// A step is a stage of a pipeline, and a stage that needs a flag binds it in an
/// <c>input { }</c> block — which is what puts the flag in <c>--help</c>. Nothing here
/// registers an option: a command harvests them from the pipelines it runs.
///
///     dotnet run --project Build.fsproj -- --help
/// </summary>
module Build

open System
open System.IO
open Partas.Build
open Partas.TypeProvider.BuildHelper

[<Literal>]
let __REPOSITORY_DIRECTORY__ =
    __SOURCE_DIRECTORY__
  + "/.."
type Repo =
    BuildHelperProvider<__REPOSITORY_DIRECTORY__,
                        capabilityFullOverride = true,
                        virtualPathConfig = """
        bin/
        tmp/
    """>

let private root = Repo.FileSystem.``.``.ToString()

module Project =
    let projects = Repo.Project.AllProjects()
    let testProjects =
        projects
        |> List.filter (function
            | { Name = name } when name.Contains("Test") -> true
            | { Path = path } when path.Contains("test") -> true
            | _ -> false
            )
    let srcProjects =
        projects
        |> List.except testProjects
        |> List.filter (fun proj -> proj.Directory.Contains("src") || proj.Name.Contains("ExternalAnnotations"))

    let allProjects =
        [
            "build", Repo.Project.``Partas.Build``.Path
            "baked", Repo.Project.``Partas.Build.Baked``.Path
            "cmd", Repo.Project.``Partas.Build.Cmd``.Path
            "external-annotations", Repo.Project.``Partas.ExternalAnnotations``.Path
            "external-annotations-tool", Repo.Project.``Partas.ExternalAnnotations.Tool``.Path
            "build-external-annotations", Repo.Project.``Partas.Build.ExternalAnnotations``.Path
        ]
    let target =
        Input.option<string list> "--project"
        |> Input.alias "-p"
        |> Input.arity Arity.OneOrMore
        |> Input.desc "The project(s) to target"
        |> Input.allowMultipleArgumentsPerToken
        |> Input.mapFromManyWith StringComparer.OrdinalIgnoreCase [
            yield! allProjects
        ]
        |> Input.def (allProjects |> List.map snd)

/// <summary>Stages every command opens with. All are skipped by <c>--quick</c>.</summary>
module Prelude =
    let restore = Baked.Stages.restore Repo.Project.SolutionFile
    /// Empties every <c>bin</c> directory except the root's, and deletes the packages in the root's.
    let clean = Baked.Stages.clean [ "**/bin"; "!bin"; "tmp" ] [ "bin/**/*.nupkg" ]

module ProjectManagement =
    let private packages = Repo.VirtualFileSystem.bin.ToString()

    let buildAll = Baked.Stages.build (Project.allProjects |> List.map snd)
    let packAll = Baked.Stages.pack packages (Project.allProjects |> List.map snd)
    let publishAll = Baked.Stages.nugetPush (Path.Combine(packages, "*.nupkg"))
    let bumpArgument =
        Baked.SemVer.Stages.bumpArgument (InputSpec.ofInput Project.target)

module Tests =
    /// Builds the test projects one at a time; skipped by <c>--skip-tests</c>.
    let buildAll =
        Baked.Stages.buildWith Baked.Dotnet.configOrRelease (Project.testProjects |> List.map _.Path)
        |> InputSpec.map (fun build -> { StageContext.toggleParallel false build with Name = "build tests" })
        |> InputSpec.map2 (fun skipTests -> StageContext.addPredicateBecause (ValueSome "--skip-tests is set") (fun _ -> not skipTests))
            (InputSpec.ofInput Baked.Common.skipTests)

    let private expectoArguments = [ "--colours"; "256"; "--sequenced" ]

    let execute = input {
        let! skipTests = Baked.Common.skipTests
        and! ci = Baked.Common.isCI
        and! suites =
            [ Repo.Project.``Partas.Build.Cmd.NetStandard.Tests``.Path
              Repo.Project.``Partas.Build.ExternalAnnotations.Tests``.Path
              Repo.Project.``Partas.Build.Tests``.Path
              Repo.Project.``Partas.ExternalAnnotations.Tests``.Path ]
            |> InputSpec.traverse (fun project -> Baked.Stages.expecto project expectoArguments)
        return stage "test" {
            when' (not skipTests)
            outputTo (if ci then StageOutput.Captured(OutputCapture.create()) else StageOutput.Console)
            suites
            // Built by the run, in both configurations regardless of `--configuration`: Release is what catches FS1118.
            for probeConfig in [ "Debug"; "Release" ] do stage $"compiler probe ({probeConfig})" {
                run (
                    cmd $"dotnet run --project {Repo.Project.``Partas.Build.CompilerProbe``.Path} -c {probeConfig} --"
                    |> Cmd.argIf ci [ "--summary" ]
                    |> Cmd.args expectoArguments
                )
            }
        }
    }

module Documentation =
    /// Serves under --watch, builds otherwise.
    let generate = input {
        let! watch = Baked.Common.watch
        and! noHotReload = Input.option<bool> "--no-hot-reload"
        return stage "docs" {
            run (
                if watch && noHotReload then "dotnet watch --no-hot-reload run --project docs/docs.fsproj -- watch"
                elif watch then "dotnet run --project docs/docs.fsproj -- watch"
                else "dotnet run --project docs/docs.fsproj -- build"
                )
        }
    }

    /// <summary>Prepends the <c>docs/static/llms.txt</c> header to <c>output/llms.txt</c>, which Nacara generates
    /// as a verbatim copy of that same file, so the body appears twice. <c>output/llms-full.txt</c> does not
    /// exist, so that half of the merge has no effect.</summary>
    /// <remarks>
    /// Written for fsdocs, which generated both files at the site root as a link inventory under such a heading.
    /// </remarks>
    let llms = input {
        let! watch = Baked.Common.watch
        return stage "llms" {
            when' (not watch)
            run (fun ctx ->
                let header = File.ReadAllText(Path.Combine(root, "docs", "static", "llms.txt")).TrimEnd()
                for name in [ "llms.txt"; "llms-full.txt" ] do
                    let path = Path.Combine(root, "output", name)
                    if File.Exists path then
                        let body = File.ReadAllLines path |> Array.skipWhile (fun line -> line.Trim() = "" || line.StartsWith "# ")
                        File.WriteAllLines(path, Array.append [| header; "" |] body)
                        StageContext.writeLine ctx StdStream.Out $"merged docs/llms.txt into output/{name}")
        }
    }

module Commands =
    let build =
        command "build" {
            description "Builds the solution"
            Command.pipeline {
                workingDir root
                Prelude.restore
                Prelude.clean
                ProjectManagement.buildAll
            }
        }

    let test =
        command "test" {
            description "Builds and runs the test suite"
            Command.pipeline {
                workingDir root
                Prelude.restore
                Prelude.clean
                ProjectManagement.buildAll
                Tests.buildAll
                Tests.execute
            }
        }

    let publish =
        command "publish" {
            description "Packs the solution and pushes it to NuGet"
            Command.pipeline {
                workingDir root
                Prelude.restore
                Prelude.clean
                ProjectManagement.buildAll
                Tests.buildAll
                Tests.execute
                ProjectManagement.packAll
                ProjectManagement.publishAll
            }
        }

    let bump =
        command "bump" {
            description "Bumps the <Version> of the target project(s): dotnet run bump [major|minor|patch|alpha|beta|rc|preview|<SEMVER>] -p <project>"
            pipeline "bump" {
                workingDir root
                ProjectManagement.bumpArgument
            }
        }

    let docs =
        command "docs" {
            description "Builds the documentation, or serves it with --watch"

            pipeline "docs" {
                workingDir root
                Prelude.restore
                ProjectManagement.buildAll
                Documentation.generate
                Documentation.llms
            }
        }

let mainBuilder argsv =
    rootCommand argsv {
        description "Partas.Build"
        addCommands
            [ Commands.build
              Commands.test
              Commands.publish
              Commands.bump
              Commands.docs ]
    }

[<EntryPoint>]
let main argsv = mainBuilder argsv
