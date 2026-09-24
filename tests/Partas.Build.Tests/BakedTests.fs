/// <summary>The ready-made stages of <c>Partas.Build.Baked</c>, run through a command the way a consumer runs them.</summary>
module Partas.Build.Tests.BakedTests

open System.IO
open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.Tests.Helpers

/// The stage a prefab answers for <paramref name="commandLine"/>, parsed against the options the prefab declares.
let private resolve (commandLine: string) (spec: InputSpec<StageContext>) = spec.Read (parse spec.Inputs commandLine)

let private subStages (stage: StageContext) = [
    for step in stage.Steps do
        match step with
        | Step.StepOfStage child -> child
        | _ -> ()
]

let private labels (stage: StageContext) = [
    for step in stage.Steps do
        match step with
        | Step.StepFn(ValueSome label, _)
        | Step.Operation(ValueSome label, _) -> label
        | _ -> ()
]

let private reasons (stage: StageContext) = stage.Conditions |> List.choose (_.Reason >> ValueOption.toOption)

let private isActive (stage: StageContext) = stage.IsActive stage

let private tempTree (files: string list) =
    let directory = Directory.CreateTempSubdirectory "partas-build-clean"
    for file in files do
        let path = Path.Combine(directory.FullName, file)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "")
    directory

[<Tests>]
let tests =
    testList "baked" [
        test "bump reports each project through the stage's output" {
            let directory = Directory.CreateTempSubdirectory "partas-build-bump"

            try
                let project = Path.Combine(directory.FullName, "Bumped.fsproj")
                File.WriteAllText(project, """<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>""")
                let lines = ResizeArray<string>()

                let built =
                    command "bump" {
                        pipeline "bump" {
                            quiet
                            stage "outer" {
                                outputTo (StageOutput.Redirect(fun _ line -> lines.Add line))
                                Baked.SemVer.Stages.bumpArgument (InputSpec.ret [ project ])
                            }
                        }
                    }

                Expect.equal (built.Parse("minor --ci false").Invoke()) 0 "the bump should succeed"
                Expect.sequenceEqual lines [ $"{project}: 1.2.3 -> 1.3.0" ] "the report should reach the stage's redirect"
                Expect.stringContains (File.ReadAllText project) "<Version>1.3.0</Version>" "the project file should carry the new version"
            finally
                directory.Delete true
        }

        test "prefabs compose inside a command, which registers their options" {
            let built =
                command "ci" {
                    pipeline "ci" {
                        quiet
                        Baked.Stages.restore "Repo.slnx"
                        Baked.Stages.clean [ "tmp-never-created" ] []
                        Baked.Stages.expecto "tests/T/T.fsproj" []
                    }
                }
            let exitCode = quietly (fun () -> built.Parse("--quick true --skip-tests true --ci false").Invoke())
            Expect.equal exitCode 0 "every prefab is skipped, and the options parse"
        }

        testList "options" [
            test "quick, skipTests and watch declare their options" {
                Expect.sequenceEqual
                    (inputNames [ Baked.Common.quick; Baked.Common.skipTests; Baked.Common.watch ])
                    [ "--quick"; "--skip-tests"; "--watch" ]
                    "each flag should declare its option"
            }
            test "the configuration defaults to Release" {
                let spec = Baked.Dotnet.configOrRelease
                Expect.equal (spec.Read (parse spec.Inputs "")) "Release" "an omitted configuration should read Release"
                Expect.equal (spec.Read (parse spec.Inputs "-c d")) "Debug" "a supplied configuration should be read"
            }
        ]

        testList "restore" [
            test "restores tools, then the solution" {
                let stage = Baked.Stages.restore "Repo.slnx" |> resolve ""
                Expect.equal stage.Name "restore" "the stage name"
                Expect.sequenceEqual (labels stage) [ "dotnet tool restore --verbosity q"; "dotnet restore Repo.slnx" ] "the steps"
                Expect.isTrue (isActive stage) "the stage runs without --quick"
            }
            test "is skipped by --quick, naming it" {
                let stage = Baked.Stages.restore "Repo.slnx" |> resolve "--quick true"
                Expect.isFalse (isActive stage) "the stage is skipped under --quick"
                Expect.sequenceEqual (reasons stage) [ "--quick is set" ] "--explain names the option"
            }
            test "the With variant takes the consumer's own flag" {
                let own = Input.option<bool> "--fast"
                let spec = Baked.Stages.restoreWith (InputSpec.ofInput own) "Repo.slnx"
                Expect.sequenceEqual (inputNames spec.Inputs) [ "--fast" ] "only the consumer's flag is declared"
                Expect.isFalse (resolve "--fast true" spec |> isActive) "the consumer's flag skips the stage"
            }
        ]

        testList "clean" [
            test "the step's label lists its patterns" {
                let stage = Baked.Stages.clean [ "**/bin"; "!bin" ] [ "bin/*.nupkg" ] |> resolve ""
                Expect.sequenceEqual (labels stage) [ "empty **/bin !bin; delete bin/*.nupkg" ] "the label"
                Expect.isFalse (Baked.Stages.clean [ "tmp" ] [] |> resolve "-q true" |> isActive) "--quick skips the clean"
            }
            test "empties the selected directories and deletes the selected files, under the working directory" {
                let directory =
                    tempTree [ "bin/keep.dll"; "bin/a.nupkg"; "bin/sub/b.nupkg"; "src/A/bin/Debug/A.dll"; "src/B/bin/B.dll"
                               "src/B/obj/B.dll"; "node_modules/pkg/bin/tool.js" ]
                try
                    let root = directory.FullName
                    let clean =
                        Baked.Stages.clean [ "**/bin"; "!bin"; "tmp" ] [ "bin/**/*.nupkg" ]
                        |> resolve ""
                        |> StageContext.setWorkingDir (ValueSome root)
                    Expect.isOk (quietly (fun () -> runStage clean)) "the clean should succeed"
                    let exists (path: string) = File.Exists(Path.Combine(root, path))
                    Expect.isTrue (exists "bin/keep.dll") "the excluded root bin keeps its other files"
                    Expect.isFalse (exists "bin/a.nupkg" || exists "bin/sub/b.nupkg") "the packages are deleted"
                    Expect.isFalse (exists "src/A/bin/Debug/A.dll" || exists "src/B/bin/B.dll") "nested bin directories are emptied"
                    Expect.isTrue (Directory.Exists(Path.Combine(root, "src/A/bin"))) "an emptied directory remains"
                    Expect.isTrue (exists "src/B/obj/B.dll") "an unselected directory is untouched"
                    Expect.isTrue (exists "node_modules/pkg/bin/tool.js") "node_modules is not walked"
                    Expect.isTrue (Directory.Exists(Path.Combine(root, "tmp"))) "a literal directory is created"
                finally
                    directory.Delete true
            }
            test "a selected directory nested inside another selected directory is omitted" {
                let directory = tempTree [ "src/A/bin/A.dll"; "a/b/c.txt" ]
                try
                    let root = directory.FullName
                    Expect.sequenceEqual (Baked.Clean.directories root [ "**/bin"; "src/A/bin/keep" ]) [ "src/A/bin" ] "literal under a match"
                    Expect.sequenceEqual (Baked.Clean.directories root [ "a"; "a/b" ]) [ "a" ] "literal under a literal"
                    Expect.sequenceEqual (Baked.Clean.directories root [ "src"; "**/bin" ]) [ "src" ] "match under a literal"
                    Expect.sequenceEqual (Baked.Clean.directories root [ "ab"; "a" ]) [ "ab"; "a" ] "a shared prefix is not nesting"
                finally
                    directory.Delete true
            }
            test "links are removed without following them" {
                let outside = tempTree [ "bin/important.txt"; "bin/a.nupkg"; "secret.nupkg" ]
                let directory = tempTree [ "bin/own.dll"; "src/A/A.fs" ]
                try
                    let root = directory.FullName
                    let link (path: string) (target: string) = Directory.CreateSymbolicLink(Path.Combine(root, path), target) |> ignore
                    link "link" outside.FullName
                    link "bin/inner" outside.FullName
                    link "src/loop" root
                    File.CreateSymbolicLink(Path.Combine(root, "src/A/linked.nupkg"), Path.Combine(outside.FullName, "secret.nupkg"))
                    |> ignore
                    let emptied, deleted = Baked.Clean.run root [ "**/bin"; "link/bin" ] [ "**/*.nupkg" ]
                    Expect.sequenceEqual emptied [ "bin" ] "a linked directory is neither walked nor selected"
                    Expect.sequenceEqual deleted [ "src/A/linked.nupkg" ] "a linked file is selected by its own path"
                    let intact (path: string) = File.Exists(Path.Combine(outside.FullName, path))
                    Expect.isTrue (intact "bin/important.txt" && intact "bin/a.nupkg" && intact "secret.nupkg") "the link targets are intact"
                    Expect.isFalse (Directory.Exists(Path.Combine(root, "bin/inner"))) "a link inside an emptied directory is removed"
                    Expect.isFalse (File.Exists(Path.Combine(root, "bin/own.dll"))) "the directory itself is emptied"
                    Expect.isTrue (File.Exists(Path.Combine(root, "src/A/A.fs"))) "an unselected file is untouched"
                finally
                    directory.Delete true
                    outside.Delete true
            }
            test "glob matching" {
                Expect.isTrue (Baked.Clean.isMatch "**/bin" "bin") "** matches no directories"
                Expect.isTrue (Baked.Clean.isMatch "**/bin" "a/b/bin") "** matches several directories"
                Expect.isFalse (Baked.Clean.isMatch "*/bin" "a/b/bin") "* stays within a segment"
                Expect.isTrue (Baked.Clean.isMatch "bin/*.nupkg" "bin/A.1.0.0.nupkg") "* matches within a segment"
                Expect.isFalse (Baked.Clean.isMatch "bin/*.nupkg" "bin/A.snupkg.txt") "the extension must match"
            }
        ]

        testList "build and pack" [
            test "build runs one sub-stage per project in parallel, in the configuration" {
                let stage = Baked.Stages.build [ "src/A/A.fsproj"; "src/B/B.fsproj" ] |> resolve ""
                Expect.equal stage.Name "build" "the stage name"
                Expect.equal (stage.IsParallel stage) (ValueSome -1) "the projects build in parallel"
                Expect.sequenceEqual (subStages stage |> List.map _.Name) [ "build A"; "build B" ] "a sub-stage per project"
                Expect.sequenceEqual
                    (subStages stage |> List.collect labels)
                    [ "dotnet build src/A/A.fsproj -c Release"; "dotnet build src/B/B.fsproj -c Release" ]
                    "Release by default"
                let debug = Baked.Stages.build [ "src/A/A.fsproj" ] |> resolve "--configuration debug"
                Expect.sequenceEqual (subStages debug |> List.collect labels) [ "dotnet build src/A/A.fsproj -c Debug" ] "the configuration"
            }
            test "pack packs the built output without rebuilding" {
                let stage = Baked.Stages.pack "bin" [ "src/A/A.fsproj" ] |> resolve ""
                Expect.equal (stage.IsParallel stage) (ValueSome -1) "the projects pack in parallel"
                Expect.sequenceEqual
                    (subStages stage |> List.collect labels)
                    [ "dotnet pack src/A/A.fsproj -c Release --no-build --no-restore -o bin" ]
                    "the pack step"
            }
        ]

        testList "expecto" [
            test "runs the suite through dotnet run, locally on the console" {
                let stage = Baked.Stages.expecto "tests/T/T.fsproj" [ "--sequenced" ] |> resolve "--ci false"
                Expect.equal stage.Name "test T" "the stage name"
                Expect.sequenceEqual
                    (labels stage)
                    [ "dotnet run --project tests/T/T.fsproj --no-build -c Release -- --sequenced" ]
                    "the MSBuild-resolved suite, without --summary"
                Expect.isTrue stage.Output.IsNone "local output is inherited"
                Expect.isTrue (isActive stage) "the suite runs"
            }
            test "summarises and captures under --ci" {
                let stage = Baked.Stages.expecto "tests/T/T.fsproj" [] |> resolve "--ci true"
                Expect.sequenceEqual (labels stage) [ "dotnet run --project tests/T/T.fsproj --no-build -c Release -- --summary" ] "--summary"
                Expect.isTrue (match stage.Output with ValueSome(StageOutput.Captured _) -> true | _ -> false) "the output is captured"
            }
            test "is skipped by --skip-tests" {
                let stage = Baked.Stages.expecto "tests/T/T.fsproj" [] |> resolve "--skip-tests true --ci false"
                Expect.isFalse (isActive stage) "the suite is skipped"
                Expect.sequenceEqual (reasons stage) [ "--skip-tests is set" ] "--explain names the option"
            }
        ]

        testList "nugetPush" [
            test "pushes to nuget.org with the key masked" {
                let spec = Baked.Stages.nugetPush "bin/*.nupkg"
                let stage = spec |> resolve "--nuget-key hunter2"
                Expect.equal stage.Name "push" "the stage name"
                Expect.sequenceEqual (subStages stage |> List.map _.Name) [ "push to source" ] "only the source push"
                let label = subStages stage |> List.collect labels |> List.exactlyOne
                Expect.equal
                    label
                    "dotnet nuget push bin/*.nupkg --source https://api.nuget.org/v3/index.json --api-key *** --skip-duplicate"
                    "the key is masked"
                Expect.isFalse (label.Contains "hunter2") "the key never reaches the label"
            }
            test "pushes to the local feed without a key" {
                let spec = Baked.Stages.nugetPushWith (InputSpec.ret None) "local" "https://example.test/v3/index.json" "bin/*.nupkg"
                let stage = spec |> resolve ""
                Expect.sequenceEqual (subStages stage |> List.map _.Name) [ "push to local feed" ] "only the local push"
                Expect.sequenceEqual
                    (subStages stage |> List.collect labels)
                    [ "dotnet nuget push bin/*.nupkg --source local --skip-duplicate" ]
                    "the local push"
            }
        ]

        testList "fantomas and npm" [
            test "fantomas formats the paths, skipped by --quick" {
                let stage = Baked.Stages.fantomas [ "src"; "tests" ] |> resolve ""
                Expect.sequenceEqual (labels stage) [ "dotnet fantomas src tests" ] "the format step"
                Expect.sequenceEqual
                    (Baked.Stages.fantomasWith (InputSpec.ret false) true [ "src" ] |> resolve "" |> labels)
                    [ "dotnet fantomas --check src" ]
                    "the check step"
                Expect.isFalse (Baked.Stages.fantomas [ "src" ] |> resolve "--quick true" |> isActive) "--quick skips formatting"
            }
            test "npmInstall runs npm ci under --ci and npm install otherwise" {
                Expect.sequenceEqual (Baked.Stages.npmInstall "docs" |> resolve "--ci true" |> labels) [ "npm ci --prefix docs" ] "CI"
                Expect.sequenceEqual (Baked.Stages.npmInstall "docs" |> resolve "--ci false" |> labels) [ "npm install --prefix docs" ] "local"
                Expect.isFalse (Baked.Stages.npmInstall "docs" |> resolve "--ci false -q true" |> isActive) "--quick skips the install"
            }
        ]
    ]
