module Partas.Build.Tests.ExplainTests

open System
open System.IO
open Expecto
open Partas.Build
open Partas.Build.Internal

/// Runs <paramref name="fn"/> with the console redirected, and answers its result alongside what it printed.
let private capturingOut (fn: unit -> 'T) =
    let original = Console.Out
    use writer = new StringWriter()
    Console.SetOut writer

    try
        let result = fn ()
        result, writer.ToString()
    finally
        Console.SetOut original

[<Tests>]
let tests =
    testList "explain" [
        test "explain renders the tree without running anything" {
            let ran = ResizeArray<string>()

            let built =
                pipeline "test" {
                    stage "restore" { when' false; run (fun (_: StageContext) -> ran.Add "restore") }
                    stage "build" { run "dotnet build Foo.slnx -c Release" }
                }

            let text = Explain.render [ built ]

            Expect.isEmpty ran "explain executes no step"
            Expect.stringContains text "restore" "every stage appears"
            Expect.stringContains text "skipped" "an inactive stage says so"
            Expect.stringContains text "dotnet build Foo.slnx -c Release" "a labelled step shows its command line"
        }

        test "explain masks a secret in a step's command line" {
            let key = "super-secret-key"
            let built = pipeline "publish" { stage "push" { runSensitive $"dotnet nuget push pkg -k {key}" } }

            let text = Explain.render [ built ]

            Expect.isFalse (text.Contains key) "explain is safe to run on a publish command"
            Expect.stringContains text "***" "the hole is masked, not omitted"
        }

        test "explain names an unlabelled step by its index rather than guessing" {
            let built = pipeline "compute" { stage "work" { run (fun (_: StageContext) -> ()) } }
            let text = Explain.render [ built ]
            Expect.stringContains text "step 1" "an opaque closure renders as its position"
        }

        test "explain attributes a skip to the structured condition that failed" {
            let built =
                pipeline "p" {
                    stage "guarded" { whenEnvVar "PARTAS_BUILD_EXPLAIN_UNSET"; run "dotnet --version" }
                }

            let text = Explain.render [ built ]

            Expect.stringContains text "(skipped: env var PARTAS_BUILD_EXPLAIN_UNSET is unset)" "the failing condition names itself"
        }

        test "explain reports a bare when' skip without inventing a reason" {
            let built = pipeline "p" { stage "off" { when' false; run "dotnet --version" } }

            let text = Explain.render [ built ]

            Expect.stringContains text "off  (skipped)" "the stage says it is skipped"
            Expect.isFalse (text.Contains "skipped:") "a bool argument leaves nothing to report"
        }

        test "a command that runs a pipeline registers --explain" {
            let built = command "build" { pipeline "p" { stage "s" { run (fun (_: StageContext) -> ()) } } }

            Expect.contains [ for option in built.Options -> option.Name ] "--explain" "the library reserves the name"
        }

        test "explain prints the tree and runs nothing when the command is invoked" {
            let ran = ResizeArray<string>()

            let built =
                command "build" {
                    pipeline "p" { stage "s" { run (fun (_: StageContext) -> ran.Add "s") } }
                }

            Expect.equal (built.Parse("--explain").Invoke()) 0 "explain exits zero"
            Expect.isEmpty ran "no step ran"
            Expect.equal (built.Parse("").Invoke()) 0 "the pipeline still runs without the flag"
            Expect.equal (List.ofSeq ran) [ "s" ] "and runs its step"
        }

        test "explain reports commands that have no description" {
            let built = command "orphan" { pipeline "p" { stage "s" { run (fun (_: StageContext) -> ()) } } }
            Expect.contains (Explain.undescribed built) "orphan" "an undescribed command is reported"
        }
    

        test "explain renders a stage whose output sink is not the console" {
            let capture = OutputCapture()

            let built =
                pipeline "p" {
                    stage "quiet" { silentOutput; run "dotnet --version" }
                    stage "held" { outputTo (StageOutput.Captured capture); run "dotnet --info" }
                }

            let text, printed = capturingOut (fun () -> Explain.render [ built ])

            Expect.stringContains text "quiet" "a silenced stage is still described"
            Expect.stringContains text "dotnet --version" "and so is its step"
            Expect.stringContains text "held" "a captured stage is still described"
            Expect.stringContains text "dotnet --info" "and so is its step"
            Expect.equal printed "" "render prints nothing of its own"
            Expect.isTrue capture.IsEmpty "and diverts nothing into a stage's sink"
        }

        test "a command explains a silenced stage in full" {
            let built = command "build" { pipeline "p" { stage "quiet" { silentOutput; run "dotnet --version" } } }

            let code, printed = capturingOut (fun () -> built.Parse("--explain").Invoke())

            Expect.equal code 0 "explain exits zero"
            Expect.stringContains printed "quiet" "the stage reaches the console"
            Expect.stringContains printed "dotnet --version" "together with its step, not a header on its own"
        }

        test "a command with no pipelines explains the subcommands it dispatches to" {
            let child =
                command "child" {
                    description "does the thing"
                    pipeline "p" { stage "s" { run (fun (_: StageContext) -> ()) } }
                }

            let built =
                command "group" {
                    description "groups subcommands"
                    addCommand child
                }

            Expect.contains [ for option in built.Options -> option.Name ] "--explain" "every command reserves the name"

            let code, printed = capturingOut (fun () -> built.Parse("--explain").Invoke())

            Expect.equal code 0 "explain exits zero"
            Expect.stringContains printed "child" "the subcommand is named"
            Expect.stringContains printed "does the thing" "with its description"
        }
    ]
