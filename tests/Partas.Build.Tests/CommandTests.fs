module Partas.Build.Tests.CommandTests

open System
open System.CommandLine
open Expecto
open Partas.Build
open Partas.Build.Internal

let private noop (_: StageContext) = ()
let private optionNames (command: Command) = [ for option in command.Options -> option.Name ]

let private options () =
    Input.option<string> "--configuration" |> Input.def "Debug",
    Input.option<bool> "--quick" |> Input.def false,
    Input.option<bool> "--verbose" |> Input.def false

/// The command's own options, minus the `--help` System.CommandLine adds to every command.
let private declared (command: Command) = optionNames command |> List.filter (fun name -> name <> "--help")

[<Tests>]
let tests =
    testList "command" [
        test "a command registers the options its stages declared" {
            let config, quick, _ = options ()

            let compile = input {
                let! cfg = config
                and! q = quick
                return stage "compile" { run (fun (_: StageContext) -> ignore (cfg, q)) }
            }

            let built =
                command "build" {
                    description "restore + build"
                    alias "b"
                    pipeline "build" {
                        stage "restore" { run noop }
                        compile
                    }
                }

            // Nothing here registered an option: both came from the stage that reads them.
            Expect.equal (declared built) [ "--configuration"; "--quick" ] "the stage's inputs should reach the command"
            Expect.equal built.Description "restore + build" "the description should be applied"
            Expect.equal (List.ofSeq built.Aliases) [ "b" ] "the alias should be applied"
        }

        test "a command whose pipelines declare nothing registers nothing" {
            let built =
                command "clean" {
                    pipeline "clean" { stage "clean" { run noop } }
                }

            Expect.isEmpty (declared built) "no option should be registered"
        }

        test "an option two pipelines share is registered once" {
            let config, _, _ = options ()

            let declaring name = input {
                let! cfg = config
                return pipeline name { stage name { run (fun (_: StageContext) -> ignore cfg) } }
            }

            let built =
                command "both" {
                    declaring "first"
                    declaring "second"
                }

            Expect.equal (declared built) [ "--configuration" ] "the shared option should be registered once"
        }

        test "addInput registers a flag no pipeline asked for" {
            let config, _, verboseInput = options ()

            let declaring = input {
                let! cfg = config
                return pipeline "build" { stage "compile" { run (fun (_: StageContext) -> ignore cfg) } }
            }

            let built =
                command "build" {
                    addInput verboseInput
                    declaring
                }

            Expect.equal (declared built) [ "--verbose"; "--configuration" ] "command inputs should come before harvested ones"
        }

        test "a command with no pipelines is a grouping node" {
            let child = command "child" { pipeline "child" { stage "only" { run noop } } }

            let built =
                command "parent" {
                    description "groups subcommands"
                    addCommand child
                }

            Expect.isTrue (isNull built.Action) "a command with no pipeline should have no action, so help is shown"
            Expect.equal [ for sub in built.Subcommands -> sub.Name ] [ "child" ] "the subcommand should be registered"
            Expect.isFalse (isNull child.Action) "a command with a pipeline should have an action"
        }

        test "hidden is applied" {
            let built = command "secret" { hidden }
            Expect.isTrue built.Hidden "the command should be hidden"
        }

        test "invoking a command runs its pipelines with the parsed values" {
            let config, quick, _ = options ()
            let seen = ResizeArray<string>()

            let compile = input {
                let! cfg = config
                and! q = quick
                return stage "compile" { run (fun (_: StageContext) -> seen.Add $"compile cfg={cfg} q={q}") }
            }

            let built =
                command "build" {
                    pipeline "build" {
                        stage "restore" { run (fun (_: StageContext) -> seen.Add "restore") }
                        compile
                    }
                }

            let exitCode = built.Parse("--configuration Release --quick true").Invoke()

            Expect.equal exitCode 0 "a successful pipeline should exit zero"
            Expect.sequenceEqual seen [ "restore"; "compile cfg=Release q=True" ] "stages should run in order with the parsed values"
        }

        test "invoking a command whose pipeline fails exits non-zero" {
            let built =
                command "fail" {
                    pipeline "fail" { stage "boom" { run (fun (_: StageContext) -> failwith "boom") } }
                }

            Expect.equal (built.Parse("").Invoke()) 1 "a failed pipeline should exit one"
        }

        test "the root command takes the name it was given" {
            let rootCommandBuilder = RootCommandBuilder [||]
            let build = rootCommandBuilder.name (id, "build.fsx")
            let spec = CommandSpec.create "" |> build
            Expect.equal spec.Name (ValueSome "build.fsx") "the name custom operation reaches the spec"
        }

        test "Args.script takes everything after the first separator" {
            let taken = Args.take [| "fsi.dll"; "build.fsx"; "--"; "test"; "--quick" |]
            Expect.equal taken [| "test"; "--quick" |] "the script's own arguments and nothing else"
        }

        test "Args.script is empty when there is no separator" {
            let taken = Args.take [| "fsi.dll"; "build.fsx" |]
            Expect.isEmpty taken "no separator means no arguments were passed to the script"
        }

        test "Args.scriptName finds the script file among the host's arguments" {
            let named = Args.nameOf [| "fsi.dll"; "tools/generate-wire.fsx"; "--"; "generate" |]
            Expect.equal named (ValueSome "generate-wire.fsx") "the filename, without its directory"
        }
    ]

/// The pipeline-level operations a command carries are defaults for every pipeline it runs, and a default
/// never wins against a pipeline that set the same thing for itself. Each test therefore reads the setting
/// back the way the runner does - through the `StageContext` lookups - rather than off the spec.
[<Tests>]
let defaultsTests =
    testList "command pipeline defaults" [
        test "a command default fills a setting the pipeline left alone" {
            let seen = ResizeArray<string voption>()

            let built =
                command "build" {
                    workingDir "/from-command"
                    pipeline "build" {
                        stage "compile" { run (fun ctx -> seen.Add (StageContext.getWorkingDir ctx)) }
                    }
                }

            Expect.equal (built.Parse("").Invoke()) 0 "the pipeline should succeed"
            Expect.sequenceEqual seen [ ValueSome "/from-command" ] "the stage should resolve the command's working directory"
        }

        test "a pipeline keeps its own setting against a command default" {
            let seen = ResizeArray<string voption>()

            let built =
                command "build" {
                    workingDir "/from-command"
                    pipeline "build" {
                        workingDir "/from-pipeline"
                        stage "compile" { run (fun ctx -> seen.Add (StageContext.getWorkingDir ctx)) }
                    }
                }

            Expect.equal (built.Parse("").Invoke()) 0 "the pipeline should succeed"
            Expect.sequenceEqual seen [ ValueSome "/from-pipeline" ] "the pipeline's own setting should survive the command default"
        }

        test "a default written below the pipeline reaches it too" {
            let seen = ResizeArray<string voption>()

            let built =
                command "build" {
                    pipeline "build" {
                        stage "compile" { run (fun ctx -> seen.Add (StageContext.getWorkingDir ctx)) }
                    }
                    workingDir "/from-command"
                }

            Expect.equal (built.Parse("").Invoke()) 0 "the pipeline should succeed"
            Expect.sequenceEqual seen [ ValueSome "/from-command" ] "defaults should apply once the command is built, not as each pipeline is yielded"
        }

        test "a default reaches the implicit pipeline of stages yielded into the command" {
            let seen = ResizeArray<int>()

            let built =
                command "build" {
                    timeoutForStage (TimeSpan.FromSeconds 12.)
                    stage "compile" { run (fun ctx -> seen.Add (StageContext.getTimeoutForStage ctx)) }
                }

            Expect.equal (built.Parse("").Invoke()) 0 "the pipeline should succeed"
            Expect.sequenceEqual seen [ 12_000 ] "a stage yielded straight into the command should see the command's default"
        }

        test "every pipeline the command runs takes the default" {
            let seen = ResizeArray<string voption>()
            let record name = stage name { run (fun ctx -> seen.Add (StageContext.getWorkingDir ctx)) }

            let built =
                command "build" {
                    workingDir "/from-command"
                    pipeline "first" { record "first" }
                    pipeline "second" {
                        workingDir "/from-second"
                        record "second"
                    }
                }

            Expect.equal (built.Parse("").Invoke()) 0 "both pipelines should succeed"
            Expect.sequenceEqual
                seen
                [ ValueSome "/from-command"; ValueSome "/from-second" ]
                "the default should fill the first pipeline and leave the second one alone"
        }

        test "env vars merge per key" {
            let seen = ResizeArray<string>()

            let built =
                command "build" {
                    envVars [ "SHARED", "from-command"; "ONLY_COMMAND", "from-command" ]
                    pipeline "build" {
                        envVars [ "SHARED", "from-pipeline" ]
                        stage "compile" {
                            run (fun ctx ->
                                seen.Add (StageContext.getEnvVar ctx "SHARED")
                                seen.Add (StageContext.getEnvVar ctx "ONLY_COMMAND"))
                        }
                    }
                }

            Expect.equal (built.Parse("").Invoke()) 0 "the pipeline should succeed"
            Expect.sequenceEqual seen [ "from-pipeline"; "from-command" ] "only the key the pipeline set should resist the default"
        }

        test "a command hook runs only when the pipeline declares none" {
            let seen = ResizeArray<string>()

            let built =
                command "build" {
                    runBeforeEachStage (fun ctx -> seen.Add $"command:{ctx.Name}")
                    pipeline "defaulted" { stage "one" { run noop } }
                    pipeline "own" {
                        runBeforeEachStage (fun ctx -> seen.Add $"pipeline:{ctx.Name}")
                        stage "two" { run noop }
                    }
                }

            Expect.equal (built.Parse("").Invoke()) 0 "both pipelines should succeed"
            Expect.sequenceEqual seen [ "command:one"; "pipeline:two" ] "the pipeline's own hook should replace the command's, not run alongside it"
        }

        test "command post stages run only for a pipeline without its own" {
            let seen = ResizeArray<string>()
            let record name = stage name { run (fun (_: StageContext) -> seen.Add name) }

            let built =
                command "build" {
                    post [ record "command-post" ]
                    pipeline "defaulted" { record "one" }
                    pipeline "own" {
                        record "two"
                        post [ record "pipeline-post" ]
                    }
                }

            Expect.equal (built.Parse("").Invoke()) 0 "both pipelines should succeed"
            Expect.sequenceEqual seen [ "one"; "command-post"; "two"; "pipeline-post" ] "each pipeline should run exactly one set of post stages"
        }

        test "acceptExitCodes defaults only where the pipeline kept the standard set" {
            let defaulted = PipelineContext.applyDefaults (fun ctx -> { ctx with AcceptableExitCodes = set [ 0; 2 ] })

            let untouched = pipeline "untouched" { stage "one" { run noop } } |> defaulted

            let own =
                pipeline "own" {
                    acceptExitCodes [ 0; 3 ]
                    stage "one" { run noop }
                }
                |> defaulted

            Expect.equal untouched.AcceptableExitCodes (set [ 0; 2 ]) "the default should fill a pipeline still on the standard set"
            Expect.equal own.AcceptableExitCodes (set [ 0; 3 ]) "a pipeline that chose its own exit codes should keep them"
        }

        test "a command default does not disturb the settings it says nothing about" {
            let before = pipeline "build" {
                workingDir "/from-pipeline"
                timeout (TimeSpan.FromSeconds 30.)
                stage "one" { run noop }
            }

            let after = before |> PipelineContext.applyDefaults (fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Quiet })

            Expect.equal after.WorkingDir before.WorkingDir "an unrelated setting should be untouched"
            Expect.equal after.Timeout before.Timeout "an unrelated setting should be untouched"
            Expect.equal after.Verbosity (ValueSome Verbosity.Quiet) "the defaulted setting should be filled in"
            Expect.equal (List.length after.Stages) 1 "defaults should not add or drop stages"
        }

        test "the description falls back to the command's only for an unnamed pipeline" {
            let described =
                command "build" {
                    description "restore + build"
                    pipeline "build" {
                        description "the pipeline says so"
                        stage "one" { run noop }
                    }
                }

            Expect.equal described.Description "restore + build" "the command keeps its own description"
        }
    ]
