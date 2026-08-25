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
    type PartasBuild = Repo.Project.``Partas.Build``
    type PartasExternalAnnotations = Repo.Project.``Partas.ExternalAnnotations``
    type PartasExternalAnnotationsTool = Repo.Project.``Partas.ExternalAnnotations.Tool``
    type PartasBuildExternalAnnotations = Repo.Project.``Partas.Build.ExternalAnnotations``
    let build = input {
        let! config = Baked.Input.DotNet.config
        let config = Option.map _.ToString() config |> Option.defaultValue "Release"
        return stage "build" {
            parallel'
            // references all projects one way or another
            run (Cmd.ofList "dotnet" (PartasBuild.Build [ "-c"; config ]))
            run (Cmd.ofList "dotnet" (PartasBuildExternalAnnotations.Build ["-c"; config]))
            run (Cmd.ofList "dotnet" (PartasExternalAnnotations.Build [ "-c"; config ]))
            run (Cmd.ofList "dotnet" (PartasExternalAnnotationsTool.Build ["-c"; config]))
        }
    }

    let private packArgs = [ "--no-restore"; "-o"; VRoot.bin.ToString() ]
    /// <remarks>
    /// No <c>Version</c> property is passed: the version is whatever <c>&lt;Version&gt;</c> in the project file says,
    /// which <c>bump</c> writes locally and CI only reads. Overriding it here would make the packed version a
    /// property of the machine that ran the pack rather than of the commit.
    /// </remarks>
    let pack = stage "pack" {
        parallel'
        run (Cmd.ofList "dotnet" (PartasBuild.Pack packArgs))
        run (Cmd.ofList "dotnet" (PartasBuildExternalAnnotations.Pack packArgs))
        run (Cmd.ofList "dotnet" (PartasExternalAnnotations.Pack packArgs))
        run (Cmd.ofList "dotnet" (PartasExternalAnnotationsTool.Pack packArgs))
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
        let! key = Baked.Input.NuGet.apiKeyOrEnv
        let packages = Path.Combine (VRoot.bin.ToString(), "*.nupkg")

        let push =
            match key with
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

module Versioning =
    /// <summary>Rewrites <c>&lt;Version&gt;</c> in each <c>--project</c>, in place.</summary>
    /// <remarks>
    /// Skipped when <c>--ci</c> is set (which it is by default under GitHub Actions and friends): a version is
    /// bumped locally and committed, so CI packs what the project file already carries rather than deciding a
    /// version of its own.
    /// </remarks>
    let bump = input {
        let! bump = Baked.Argument.Versioning.bump
        and! projects = Options.Project.target
        and! ci = Baked.Input.CI.isCI

        return stage "bump" {
            when' (not ci)

            run (fun (_: StageContext) ->
                projects
                |> List.map (fun project ->
                    match Options.Project.getProj project with
                    // Unreachable while `acceptOnlyFromAmong` is fed from the same list, but the two are only
                    // conventionally in step, and a typo here should not silently bump nothing.
                    | None -> Error $"'%s{project}' is not a known project."
                    | Some path ->
                        match Baked.IO.bumpVersion path bump with
                        | Ok (previous, next) ->
                            printfn $"%s{project}: %s{previous} -> %s{next}"
                            Ok ()
                        | Error error -> Error $"%s{project}: %s{error.Message}")
                |> List.tryPick (function Error error -> Some (Error error) | Ok () -> None)
                |> Option.defaultValue (Ok ()))
        }
    }

module Tests =
    /// <remarks>
    /// Sequential, unlike the stage that builds the library: all three suites reference the same fixture
    /// projects, and two MSBuild invocations racing to write one <c>AnnotationsFixture.Cs.dll</c> fail the
    /// build with <c>CS2012 … used by another process</c> often enough to matter.
    /// </remarks>
    let build = input {
        let! skipTests = Options.skipTests
        and! config = Baked.Input.DotNet.configString
        let config = Option.defaultValue "Release" config
        return stage "build tests" {
            when' (not skipTests)
            run (Cmd.ofList "dotnet" (Repo.Project.``Partas.Build.Tests``.Build(["-c"; config])))
            run (Cmd.ofList "dotnet" (Repo.Project.``Partas.Build.ExternalAnnotations.Tests``.Build(["-c"; config])))
            run (Cmd.ofList "dotnet" (Repo.Project.``Partas.ExternalAnnotations.Tests``.Build(["-c"; config])))
        }
    }

    /// <summary>Runs the three suites, each of which is its own executable.</summary>
    /// <remarks>
    /// <c>--no-build</c> is safe because the stage above just built them in the same configuration.
    ///
    /// <c>--sequenced</c> is not a preference. Every suite drives real pipelines, and a pipeline writes to one
    /// process-wide console and holds a thread in <c>Async.RunSynchronously</c> for the length of every stage;
    /// running the tests in parallel on a two-core runner produces a log whose lines belong to no test in
    /// particular, and enough blocked workers that the thread pool has to grow its way out one thread at a time.
    ///
    /// Under <c>--ci</c> the suites' output is held back and lifted into the error if one fails, so a green run
    /// says nothing and a red one says everything. Locally it stays live: watching a suite run is most of the
    /// reason for running it by hand.
    /// </remarks>
    let execute = input {
        let! skipTests = Options.skipTests
        and! config = Baked.Input.DotNet.configString
        and! ci = Baked.Input.CI.isCI
        let config = Option.defaultValue "Release" config
        let args = [ "-c"; config; "--no-build"; "--"; "--summary"; "--sequenced"; "--colours"; "256" ]

        return stage "test" {
            when' (not skipTests)
            quiet
            outputTo (if ci then StageOutput.Captured(OutputCapture()) else StageOutput.Console)
            run (Cmd.ofList "dotnet" (Repo.Project.``Partas.Build.Tests``.Run args))
            run (Cmd.ofList "dotnet" (Repo.Project.``Partas.Build.ExternalAnnotations.Tests``.Run args))
            run (Cmd.ofList "dotnet" (Repo.Project.``Partas.ExternalAnnotations.Tests``.Run args))
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

    let bump =
        command "bump" {
            description "Bumps the <Version> of the target project(s): dotnet run bump [major|minor|patch|alpha|beta|rc|preview|<SEMVER>] -p <project>"

            pipeline "bump" {
                workingDir root
                Versioning.bump
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
