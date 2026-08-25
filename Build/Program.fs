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
open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open Partas.Build
open Partas.Build.Internal
open Spec

initializeContext ()

let private root = Root.``.``

/// Release notes drive the assembly and package version.
let release = lazy ReleaseNotes.load "docs/RELEASE_NOTES.md"

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

module HouseKeeping =
    let clean = input {
        let! quick = Options.quick

        return stage "clean" {
            when' (not quick)

            run (fun (_: StageContext) ->
                VRoot.bin.``.``.EnumerateFiles("*.nupkg", SearchOption.AllDirectories)
                |> Seq.iter (_.ToString() >> Shell.rm)
                !! "**/**/bin"
                ++ VRoot.tmp.ToString()
                -- VRoot.bin.ToString()
                |> Shell.cleanDirs
                )
        }
    }

module ProjectManagement =
    let build = input {
        let! config = Options.config

        return stage "build" {
            run (fun (_: StageContext) ->
                let version = release.Value.AssemblyVersion
                Repo.Project.``Partas.Build``.Build(
                    ["-c"; config; $"-p:PackageVersion={version}"; $"-p:Version={version}"]
                    )
                |> Cmd.ofList "dotnet"
            )
        }
    }

    let pack = stage "pack" {
        run (fun (_: StageContext) ->
            let version = release.Value.AssemblyVersion
            Repo.Project.``Partas.Build``.Pack(["--no-restore"; "-o"; VRoot.bin.ToString(); $"-p:PackageVersion={version}"; $"-p:Version={version}"])
            |> Cmd.ofList "dotnet")
    }

    /// <summary>Pushes every package in <c>bin</c>, to nuget.org with a key and to the <c>local</c> feed without one.</summary>
    /// <remarks>
    /// The glob is left to <c>dotnet nuget push</c>, which expands it itself — no shell is involved. It is built
    /// with the platform separator because NuGet resolves a wildcard by taking <c>Path.GetDirectoryName</c> of it:
    /// on Windows <c>bin/*.nupkg</c> fails with <c>File does not exist</c> where <c>bin\*.nupkg</c> succeeds.
    /// The arguments are given as a list rather than interpolated so that only the key is masked — <c>runSensitive</c>
    /// and <c>Cmd.ofFormattable true</c> mask every hole, which would hide the package path too.
    /// </remarks>
    let publish = input {
        let! key = Options.NuGet.key
        let envKey = Environment.environVarOrNone "NUGET_API_KEY"
        let packages = Path.Combine (VRoot.bin.ToString(), "*.nupkg")

        let push =
            match key |> Option.orElse envKey with
            | Some key ->
                let args =
                    [ "nuget"; "push"; packages
                      "--source"; "https://api.nuget.org/v3/index.json"
                      "--api-key"; key
                      "--skip-duplicate" ]

                { Cmd.ofList "dotnet" args with Secrets = Set.singleton (List.findIndex ((=) key) args) }
            | None -> Cmd.ofString $"dotnet nuget push {packages} --source local --skip-duplicate"

        return stage "publish" {
            echo (if key.IsSome then "Publishing to nuget.org." else "No NuGet API key provided. Publishing to the local feed if it exists.")
            run push
        }
    }

module Tests =
    let build = input {
        let! skipTests = Options.skipTests
        and! config = Options.config

        return stage "build tests" {
            when' (not skipTests)
            run (Cmd.ofList "dotnet" (Repo.Project.``Partas.Build.Tests``.Build(["-c"; config])))
        }
    }

    /// The suite is its own executable, so it is run rather than discovered: `--no-build` is safe because the
    /// stage above just built it in the same configuration.
    let execute = input {
        let! skipTests = Options.skipTests
        and! config = Options.config

        return stage "test" {
            when' (not skipTests)
            run (Cmd.ofList "dotnet" (Repo.Project.``Partas.Build.Tests``.Run(["-c"; config; "--no-build"; "--"; "--summary"; "--colours"; "256"])))
        }
    }

module Documentation =
    /// Serves under --watch, builds otherwise.
    let generate = input {
        let! watch = Options.watch

        return stage "docs" {
            run (if watch then "dotnet fsdocs watch --eval" else "dotnet fsdocs build --eval --clean")
        }
    }

module Commands =
    let build =
        command "build" {
            description "Builds the solution"

            pipeline "build" {
                workingDir root
                Prelude.restore
                HouseKeeping.clean
                ProjectManagement.build
            }
        }

    let test =
        command "test" {
            description "Builds and runs the test suite"

            pipeline "test" {
                workingDir root
                Prelude.restore
                HouseKeeping.clean
                ProjectManagement.build
                Tests.build
                Tests.execute
            }
        }

    let publish =
        command "publish" {
            description "Packs the solution and pushes it to NuGet"

            pipeline "publish" {
                workingDir root
                Prelude.restore
                HouseKeeping.clean
                ProjectManagement.build
                Tests.build
                Tests.execute
                ProjectManagement.pack
                ProjectManagement.publish
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
              Commands.docs ]
    }

[<EntryPoint>]
let main argsv = mainBuilder argsv
