module Partas.Build.Tests.PipelineTests

open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.Tests.Helpers

let private noop (_: StageContext) = ()
let private stageNames (ctx: PipelineContext) = [ for stage in ctx.Stages -> stage.Name ]

let private parentNames (ctx: PipelineContext) = [
    for stage in ctx.Stages do
        match stage.ParentContext with
        | ValueSome (StageParent.Pipeline parent) -> parent.Name
        | ValueSome (StageParent.Stage parent) -> "stage:" + parent.Name
        | ValueNone -> "none"
]

let private options () =
    Input.option<string> "--configuration" |> Input.def "Debug",
    Input.option<bool> "--quick" |> Input.def false,
    Input.option<bool> "--watch" |> Input.def false

[<Tests>]
let tests =
    testList "pipeline" [
        test "a pipeline whose stages declare nothing needs no ParseResult" {
            // The annotation is the assertion: this must not be an InputSpec.
            let built: PipelineContext =
                pipeline "pure" {
                    description "declares nothing"
                    stage "restore" { run noop }
                    stage "build" { run noop }
                }

            Expect.equal built.Name "pure" "the pipeline should keep its name"
            Expect.equal (stageNames built) [ "restore"; "build" ] "stage order should be preserved"
            Expect.equal built.Description (ValueSome "declares nothing") "the setting should be recorded"
        }

        test "one declaring stage makes the whole pipeline declare" {
            let config, quick, watch = options ()

            let compileStage =
                input {
                    let! cfg = config
                    and! q = quick
                    return stage "compile" { run (fun (_: StageContext) -> ignore (cfg, q)) }
                }

            let watchStage =
                input {
                    let! w = watch
                    return stage "watch" { run (fun (_: StageContext) -> ignore w) }
                }

            let spec: InputSpec<PipelineContext> =
                pipeline "mixed" {
                    description "mixes declaring and non-declaring stages"
                    stage "restore" { run noop }
                    compileStage
                    watchStage
                    stage "pack" { run noop }
                }

            // Still no ParseResult in existence.
            Expect.equal
                (inputNames spec.Inputs)
                [ "--configuration"; "--quick"; "--watch" ]
                "the pipeline should harvest every stage's inputs before parsing"

            let built = spec.Read (parse spec.Inputs "--configuration Release --quick true --watch true")

            Expect.equal
                (stageNames built)
                [ "restore"; "compile"; "watch"; "pack" ]
                "declaring and non-declaring stages should interleave in declaration order"

            Expect.equal built.Description (ValueSome "mixes declaring and non-declaring stages") "the setting should survive"
        }

        test "stages are re-parented onto the finished pipeline" {
            let config, _, _ = options ()

            let declaring = input {
                let! cfg = config
                return stage "compile" { run (fun (_: StageContext) -> ignore cfg) }
            }

            let spec =
                pipeline "parented" {
                    stage "restore" { run noop }
                    declaring
                }

            let built = spec.Read (parse spec.Inputs "")
            Expect.equal (parentNames built) [ "parented"; "parented" ] "every stage should point at its pipeline"
        }

        test "a declaring stage may come first" {
            let config, _, _ = options ()

            let declaring = input {
                let! cfg = config
                return stage "compile" { run (fun (_: StageContext) -> ignore cfg) }
            }

            let spec =
                pipeline "declFirst" {
                    declaring
                    stage "pack" { run noop }
                }

            Expect.equal spec.Inputs.Length 1 "the input should be harvested"
            let built = spec.Read (parse spec.Inputs "")
            Expect.equal (stageNames built) [ "compile"; "pack" ] "stage order should be preserved"
        }

        test "settings apply on either side of a declaring stage" {
            let config, _, _ = options ()

            let declaring = input {
                let! cfg = config
                return stage "compile" { run (fun (_: StageContext) -> ignore cfg) }
            }

            let spec =
                pipeline "settings" {
                    timeoutForStage 30.0
                    stage "restore" { run noop }
                    declaring
                    timeout 5.0
                    description "settings after a declaring stage"
                    stage "pack" { run noop }
                    workingDir "/tmp"
                }

            let built = spec.Read (parse spec.Inputs "")
            Expect.equal built.TimeoutForStage (ValueSome (System.TimeSpan.FromSeconds 30.0)) "a setting before the declaring stage should apply"
            Expect.equal built.Timeout (ValueSome (System.TimeSpan.FromSeconds 5.0)) "a setting after the declaring stage should apply"
            Expect.equal built.Description (ValueSome "settings after a declaring stage") "a setting after the declaring stage should apply"
            Expect.equal built.WorkingDir (ValueSome "/tmp") "a trailing setting should apply"
            Expect.equal (stageNames built) [ "restore"; "compile"; "pack" ] "settings should not disturb stage order"
        }

        test "a shared option is harvested once across stages" {
            let config, _, _ = options ()

            let first = input {
                let! cfg = config
                return stage "first" { run (fun (_: StageContext) -> ignore cfg) }
            }

            let second = input {
                let! cfg = config
                return stage "second" { run (fun (_: StageContext) -> ignore cfg) }
            }

            let spec =
                pipeline "shared" {
                    first
                    second
                }

            Expect.equal spec.Inputs.Length 1 "two stages declaring the same option should register it once"
            let built = spec.Read (parse spec.Inputs "--configuration Release")
            Expect.equal (stageNames built) [ "first"; "second" ] "both stages should be present"
        }
    ]
