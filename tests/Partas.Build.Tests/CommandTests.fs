module Partas.Build.Tests.CommandTests

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

            let compile = inputs {
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

            let declaring name = inputs {
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
            let config, _, verbose = options ()

            let declaring = inputs {
                let! cfg = config
                return pipeline "build" { stage "compile" { run (fun (_: StageContext) -> ignore cfg) } }
            }

            let built =
                command "build" {
                    addInput verbose
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

            let compile = inputs {
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
    ]
