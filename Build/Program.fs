/// <summary>
/// The build CLI, written against Partas.Build itself.
///
/// A step is a stage of a pipeline, and a stage that needs a flag binds it in an
/// <c>inputs { }</c> block — which is what puts the flag in <c>--help</c>. Nothing here
/// registers an option: a command harvests them from the pipelines it runs.
///
///     dotnet run --project Build.fsproj -- --help
/// </summary>
module Build

open System.IO
open Fake.Core.Context
open Fake.IO
open Fake.IO.Globbing.Operators
open Partas.Build
open Partas.Build.Internal
open Partas.TypeProvider.BuildHelper

let execContext = FakeExecutionContext.Create false "build.fsx" []
setExecutionContext (RuntimeContext.Fake execContext)

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

let formatFiles =
    !! "**/*.fs"
    -- "**/obj/**/*.*"
    -- "**/AssemblyInfo.fs"

module Options =
    let quick =
        Input.option<bool> "--quick"
        |> Input.alias "-q"
        |> Input.desc "Skips restores, installations, formatting etc"
    let skipTests =
        Input.option<bool> "--skip-tests"
        |> Input.desc "Skips running tests"
    let watch =
        Input.option<bool> "--watch"
        |> Input.desc "Runs the operation in watch mode."

    let config =
        Baked.Input.DotNet.configString
        |> InputSpec.ofInput
        |> InputSpec.map (Option.defaultValue "Release")

module Project =
    let allProjects =
        [
            "build", Repo.Project.``Partas.Build``.Path
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
        |> Input.acceptOnlyFromAmong (allProjects |> List.map fst)
        |> Input.customParser (fun tok ->
            match Seq.toArray tok.Tokens with
            | [||] -> []
            | projects ->
                let map =
                    allProjects
                    |> Map.ofList
                projects
                |> Array.map (fun project -> map |> Map.find project.Value )
                |> Array.toList
            )

/// <summary>Stages every command opens with. All are skipped by <c>--quick</c>.</summary>
module Prelude =
    let restore = input {
        let! quick = Options.quick
        return stage "restore" {
            when' (not quick)
            run "dotnet tool restore --verbosity q"
            run (cmd $"dotnet restore {Repo.Project.SolutionFile}")
        }
    }
    let clean = input {
        let! quick = Options.quick

        return stage "clean" {
            when' (not quick)

            run (fun (_: StageContext) ->
                Repo.VirtualFileSystem.bin.``.``.EnumerateFiles("*.nupkg", SearchOption.AllDirectories)
                |> Seq.iter (_.ToString() >> Shell.rm)
                !! "**/**/bin"
                ++ Repo.VirtualFileSystem.tmp.ToString()
                -- Repo.VirtualFileSystem.bin.ToString()
                |> Shell.cleanDirs
                )
        }
    }

module ProjectManagement =
    let build (project: InputSpec<string>) = input {
        let! config = Options.config
        and! project = project
        return stage $"build {project}" {
            run (cmd $"dotnet build {project} -c {config}")
        }
    }
    let buildAll = stage "build" {
        parallel'
        for _, project in Project.allProjects do
        build (InputSpec.ret project)
    }

    let pack (project: InputSpec<string>) = input {
        let! project = project
        return stage $"pack {project}" {
            run (cmd $"dotnet pack {project} --no-restore -o {Repo.VirtualFileSystem.bin.ToString()}")
        }
    }
    let packAll = stage "pack" {
        parallel'
        for _, project in Project.allProjects do
        InputSpec.ret project
        |> pack
    }
    let publish (project: InputSpec<string>) = input {
        let! key = Baked.Input.NuGet.apiKeyOrEnv
        and! project = project
        return stage $"publish {project}" {
            stage "local publish" {
                when' key.IsNone
                echo "Publishing to local feed"
                run $"dotnet nuget push {project} --source local --skip-duplicate"
            }
            stage "nuget publish" {
                when' key.IsSome
                echo "Publishing to nuget.org"
                runSensitive
                    $"dotnet nuget push {project} --source https://api.nuget.org/v3/index.json --api-key {key.Value} --skip-duplicate"
            }
        }
    }
    let publishAll = stage "publish" {
        Path.Combine(Repo.VirtualFileSystem.bin.ToString(), "*.nupkg")
        |> InputSpec.ret
        |> publish
    }
    let bumpArgument =
        Baked.Pipelines.bumpArgument (Project.allProjects |> List.map snd) (InputSpec.ofInput Project.target)

module Tests =
    let buildAll = input {
        let! skipTests = Options.skipTests
        and! projects =
            [
                Repo.Project.``Partas.Build.ExternalAnnotations.Tests``.Path
                Repo.Project.``Partas.Build.Tests``.Path
                Repo.Project.``Partas.ExternalAnnotations.Tests``.Path
            ]
            |> List.map (InputSpec.ret >> ProjectManagement.build)
            |> InputSpec.sequence
        return stage "build tests" {
            when' (not skipTests)
            projects
        }
    }
    let execute = input {
        let! skipTests = Options.skipTests
        and! config = Options.config
        and! ci = Baked.Input.CI.isCI
        return stage "test" {
            when' (not skipTests)
            outputTo (if ci then StageOutput.Captured(OutputCapture()) else StageOutput.Console)
            for project in [
                Repo.Project.``Partas.Build.ExternalAnnotations.Tests``.Path
                Repo.Project.``Partas.Build.Tests``.Path
                Repo.Project.``Partas.ExternalAnnotations.Tests``.Path
            ] do stage $"test {project}" {
                run (Cmd.ofString $"""dotnet run --project {project} --no-build -c {config} -- {if ci then "--summary" else null} --colours 256 --sequenced""")
            }

        }
    }

module Documentation =
    /// Serves under --watch, builds otherwise.
    let generate = input {
        let! watch = Options.watch
        return stage "docs" {
            run (if watch then "dotnet fsdocs watch --eval --saveimages" else "dotnet fsdocs build --eval --clean --saveimages")
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
                Documentation.generate
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
