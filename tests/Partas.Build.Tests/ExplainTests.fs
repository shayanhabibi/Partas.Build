module Partas.Build.Tests.ExplainTests

open Expecto
open Partas.Build
open Partas.Build.Internal

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
    ]
