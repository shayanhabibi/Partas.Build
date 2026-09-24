module Partas.Build.Tests.ExplainTests

open System
open System.IO
open System.Text.Json
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

/// Invokes <paramref name="command"/> on <paramref name="commandLine"/> with its output captured, and answers the
/// exit code alongside the output parsed as one JSON document.
let private invokeJson (command: System.CommandLine.Command) (commandLine: string) =
    use output = new StringWriter()
    let exitCode = command.Parse(commandLine).Invoke(System.CommandLine.InvocationConfiguration(Output = output))
    exitCode, JsonDocument.Parse(output.ToString()).RootElement.Clone()

let private property (name: string) (element: JsonElement) = element.GetProperty name

let private items (element: JsonElement) = [ for item in element.EnumerateArray() -> item ]

let private stageNamed (name: string) (pipeline: JsonElement) =
    pipeline |> property "stages" |> items |> List.find (fun stage -> (stage |> property "name").GetString() = name)

let private text (element: JsonElement) = element.GetString()

[<Tests>]
let tests =
    testList "explain" [
        test "explain names the producer a stage declares and the producers a stage requires" {
            let mutable prepared = 0
            let source =
                Producer.define "compile" (InputSpec.ret ()) DependencySpec.empty (fun _ _ ->
                    prepared <- prepared + 1
                    Operation.ret 42)

            let built =
                pipeline "release" {
                    Producer.stage source
                    Stage.consuming "pack" (DependencySpec.require source) (fun _ -> Operation.ret ())
                }

            let text = Explain.render [ built ]

            Expect.equal prepared 0 "rendering invokes no producer callback"
            Expect.stringContains text "produces compile" "the explicitly listed producer stage says what it declares"
            Expect.stringContains text "needs compile" "the consumer stage says what it requires"
        }

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
            let capture = OutputCapture.create()

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
            Expect.isTrue (OutputCapture.isEmpty capture) "and diverts nothing into a stage's sink"
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
        test "explain --json describes stages, steps, skips and dependencies as one document" {
            let source = Producer.define "compile" (InputSpec.ret ()) DependencySpec.empty (fun _ _ -> Operation.ret 42)

            let built =
                command "build" {
                    description "builds"
                    pipeline "p" {
                        stage "restore" { run "dotnet restore Foo.slnx" }
                        stage "guarded" { whenEnvVar "PARTAS_BUILD_EXPLAIN_UNSET"; run "dotnet --version" }
                        Producer.stage source
                        Stage.consuming "pack" (DependencySpec.require source) (fun (_: int) -> Operation.ret ())
                    }
                }

            let code, document = invokeJson built "--explain --json"

            Expect.equal code 0 "explain exits zero"
            Expect.equal (document |> property "command" |> text) "build" "the command is named"
            Expect.equal (document |> property "mode" |> text) "static" "JSON explain is static by default"
            let pipeline = document |> property "pipelines" |> items |> List.exactlyOne
            Expect.equal (pipeline |> property "name" |> text) "p" "the pipeline is named"

            let restore = stageNamed "restore" pipeline
            Expect.equal (restore |> property "status" |> text) "active" "an unconditional stage is active"
            let step = restore |> property "steps" |> items |> List.exactlyOne
            Expect.equal (step |> property "kind" |> text) "step" "a command step is a step"
            Expect.equal (step |> property "label" |> text) "dotnet restore Foo.slnx" "with its command line as its label"
            Expect.equal ((step |> property "index").GetInt32()) 0 "indexed from zero, as a run result's step is"

            let guarded = stageNamed "guarded" pipeline
            Expect.equal (guarded |> property "status" |> text) "skipped" "a failed pure condition is evaluated even statically"
            Expect.equal (guarded |> property "reason" |> text) "env var PARTAS_BUILD_EXPLAIN_UNSET is unset" "and names itself"
            let condition = guarded |> property "conditions" |> items |> List.exactlyOne
            Expect.equal (condition |> property "state" |> text) "failed" "the condition records its answer"

            Expect.equal (stageNamed "compile" pipeline |> property "produces" |> text) "compile" "the producer stage says what it declares"
            Expect.equal [ for need in stageNamed "pack" pipeline |> property "needs" |> items -> text need ] [ "compile" ] "the consumer names its needs"
        }

        test "explain --json masks a secret in a step's label" {
            let key = "super-secret-key"
            let built = command "publish" { pipeline "p" { stage "push" { runSensitive $"dotnet nuget push pkg -k {key}" } } }

            use output = new StringWriter()
            built.Parse("--explain --json").Invoke(System.CommandLine.InvocationConfiguration(Output = output)) |> ignore
            let json = output.ToString()

            Expect.isFalse (json.Contains key) "the JSON form is as safe as the text form"
            Expect.stringContains json "***" "the hole is masked, not omitted"
            JsonDocument.Parse json |> ignore
        }

        test "static explain runs no condition stage and reports it unevaluated" {
            let mutable ran = 0
            let probe = stage "probe" { run (fun (_: StageContext) -> ran <- ran + 1) }

            let built =
                command "build" {
                    pipeline "p" {
                        stage "viaOperation" { when' probe; run "dotnet --version" }
                        stage "viaBuilder" { whenStage "inline-probe" { run (fun (_: StageContext) -> ran <- ran + 1) }; run "dotnet --version" }
                        stage "viaWhenAll" { whenAll { when' probe; envVar "PATH" }; run "dotnet --version" }
                        stage "onBranch" { whenBranch "main"; run "dotnet --version" }
                    }
                }

            let code, document = invokeJson built "--explain --json"
            let rendered = Explain.renderWith ExplainMode.Static [ pipeline "p" { stage "s" { when' probe; run "dotnet --version" } } ]

            Expect.equal code 0 "explain exits zero"
            Expect.equal ran 0 "no condition stage ran"
            Expect.stringContains rendered "(unevaluated: whenStage probe)" "the text form names the condition left uncalled"

            let pipeline = document |> property "pipelines" |> items |> List.exactlyOne

            for name, effect in
                [ "viaOperation", "whenStage probe"
                  "viaBuilder", "whenStage inline-probe"
                  "viaWhenAll", "whenAll { whenStage probe; 1 more }"
                  "onBranch", "whenBranch main" ] do
                let explained = stageNamed name pipeline
                Expect.equal (explained |> property "status" |> text) "unevaluated" $"%s{name} is unevaluated"
                let condition = explained |> property "conditions" |> items |> List.exactlyOne
                Expect.equal (condition |> property "state" |> text) "unevaluated" $"%s{name}'s condition is unevaluated"
                Expect.equal (condition |> property "effect" |> text) effect $"%s{name}'s condition is described"
        }

        test "evaluated explain runs a failing condition stage once" {
            let mutable ran = 0
            let probe = stage "probe" { run (fun (_: StageContext) -> ran <- ran + 1; failwith "not today") }
            let built = pipeline "p" { stage "guarded" { when' probe; run "dotnet --version" } }

            let rendered = Helpers.quietly (fun () -> Explain.render [ built ])

            Expect.equal ran 1 "the skip is attributed without evaluating the condition again"
            Expect.stringContains rendered "(skipped: condition stage probe failed)" "the skip still names its condition"
        }

        test "explain renders a condition that throws as its message" {
            let throwing: BuildStageIsActive = fun _ -> failwith "no answer"
            let built = pipeline "p" { stage "fragile" { throwing; run "dotnet --version" }; stage "after" { run "dotnet --info" } }

            let rendered = Explain.render [ built ]
            let json = Explain.toJson ExplainMode.Evaluated (System.CommandLine.Command "c") [ built ]
            let fragile = JsonDocument.Parse(json).RootElement |> property "pipelines" |> items |> List.exactlyOne |> stageNamed "fragile"

            Expect.stringContains rendered "fragile  (condition failed: no answer)" "the failure is rendered against its stage"
            Expect.stringContains rendered "after" "and the stages after it are still described"
            Expect.equal (fragile |> property "status" |> text) "error" "the JSON form reports the error"
            Expect.equal (fragile |> property "reason" |> text) "no answer" "with its message"
        }

        test "explain stops at the first condition that fails, as IsActive does" {
            let mutable reached = false
            let later: BuildStageIsActive = fun _ -> reached <- true; true
            let built = pipeline "p" { stage "s" { when' false; later; run "dotnet --version" } }

            Explain.render [ built ] |> ignore

            Expect.isFalse reached "a condition after a failed one is not called"
        }

        test "explain --json on a command with no pipelines lists its subcommands" {
            let child = command "child" { description "does the thing"; pipeline "p" { stage "s" { run (fun (_: StageContext) -> ()) } } }
            let built = command "group" { description "groups"; addCommand child }

            let code, document = invokeJson built "--explain --json"

            Expect.equal code 0 "explain exits zero"
            let subcommand = document |> property "subcommands" |> items |> List.exactlyOne
            Expect.equal (subcommand |> property "name" |> text) "child" "the subcommand is named"
            Expect.equal (subcommand |> property "description" |> text) "does the thing" "with its description"
        }
    ]
