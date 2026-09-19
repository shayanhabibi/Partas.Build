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

let private isSilent (output: StageOutput voption) =
    match output with
    | ValueSome StageOutput.Silent -> true
    | _ -> false

/// Asserts that each named operation survives on `PipelineBuilder` exactly once, inherited and generic in the state.
let private expectOneImplementation (names: string list) =
    let builder = typeof<Partas.Build.PipelineBuilder.PipelineBuilder>

    for name in names do
        let found = builder.GetMethods() |> Array.filter (fun method -> method.Name = name)

        Expect.equal found.Length 1 $"the mirrored {name} pair should collapse into one operation"
        Expect.isTrue found[0].IsGenericMethodDefinition $"the surviving {name} should be generic in the builder state"
        Expect.notEqual found[0].DeclaringType builder $"the surviving {name} should be inherited from the shared settings builder"
        Expect.isNull
            (System.Attribute.GetCustomAttribute(found[0], typeof<System.ComponentModel.EditorBrowsableAttribute>))
            $"the operation {name} should stay visible to completion"

/// Asserts that each named operation survives with one member per argument type, all inherited and generic in the state.
let private expectOverloadsOnly (expected: (string * int) list) =
    let builder = typeof<Partas.Build.PipelineBuilder.PipelineBuilder>

    for name, count in expected do
        let found = builder.GetMethods() |> Array.filter (fun method -> method.Name = name)

        Expect.equal found.Length count $"{name} should keep one member per argument type and no state mirror"
        Expect.allEqual (found |> Array.map _.IsGenericMethodDefinition) true $"every {name} should be generic in the builder state"
        Expect.isFalse
            (found |> Array.exists (fun method -> method.DeclaringType = builder))
            $"every {name} should be inherited from the shared settings builder"

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

        test "a pipeline surfaces the exception a stage raised" {
            let boom (_: StageContext) : Async<Result<unit, string>> = raise (System.InvalidOperationException "boom")
            let built = pipeline "raising" { stage "throws" { run boom } }

            let raised =
                try
                    PipelineContext.run built
                    None
                with :? PipelineFailedException as ex -> Some ex

            match raised with
            | None -> failtest "a raising stage should fail the pipeline"
            | Some ex ->
                Expect.isNotNull ex.InnerException "the pipeline should carry the stage's exception as its cause"
                Expect.equal ex.InnerException.Message "boom" "the cause should be the exception the step raised"
        }

        // ---------------------------------------------------------------- settings shared across builder states
        test "the output settings land on the pipeline in both representations" {
            let config, _, _ = options ()

            let declaring = input {
                let! cfg = config
                return stage "compile" { run (fun (_: StageContext) -> ignore cfg) }
            }

            let plain: PipelineContext =
                pipeline "plainOutput" {
                    silentOutput
                    verbosity Verbosity.Quiet
                    noPrefixForStep
                    stage "restore" { run noop }
                }

            let spec: InputSpec<PipelineContext> =
                pipeline "specOutput" {
                    noStdRedirectForStep
                    declaring
                    silentOutput
                    verbose
                }

            let built = spec.Read (parse spec.Inputs "")

            Expect.isTrue (isSilent plain.Output) "a plain pipeline should record the output sink"
            Expect.equal plain.Verbosity (ValueSome Verbosity.Quiet) "a plain pipeline should record the verbosity"
            Expect.isTrue plain.NoPrefixForStep "a plain pipeline should record the prefix flag"

            Expect.isTrue (isSilent built.Output) "an input-aware pipeline should record the output sink"
            Expect.equal built.Verbosity (ValueSome Verbosity.Verbose) "an input-aware pipeline should record the verbosity"
            Expect.isTrue built.NoStdRedirectForStep "a setting before the declaring stage should reach the pipeline"
        }

        test "one output setting implementation serves every pipeline builder state" {
            expectOneImplementation [
                "captureOutput"
                "noPrefixForStep"
                "noStdRedirectForStep"
                "outputTo"
                "quiet"
                "redirectOutput"
                "silentOutput"
                "verbose"
                "verbosity"
            ]
        }

        test "the budget settings land on the pipeline in both representations" {
            let config, _, _ = options ()

            let declaring = input {
                let! cfg = config
                return stage "compile" { run (fun (_: StageContext) -> ignore cfg) }
            }

            let plain: PipelineContext =
                pipeline "plainBudget" {
                    timeout 30<second>
                    timeoutForStage 20.0
                    timeoutForStep (System.TimeSpan.FromSeconds 10.0)
                    workingDir "/plain"
                    envVars [ "PARTAS_BUILD_T8B", "plain" ]
                    acceptExitCodes [ 0; 2 ]
                    stage "restore" { run noop }
                }

            let spec: InputSpec<PipelineContext> =
                pipeline "specBudget" {
                    timeout 45.0
                    declaring
                    timeoutForStage 15<second>
                    timeoutForStep 5.0
                    workingDir (System.IO.DirectoryInfo "/spec")
                    envVars [ "PARTAS_BUILD_T8B", "spec" ]
                    acceptExitCodes [ 0; 3 ]
                }

            let built = spec.Read (parse spec.Inputs "")

            Expect.equal plain.Timeout (ValueSome (System.TimeSpan.FromSeconds 30.0)) "a plain pipeline should record the pipeline budget"
            Expect.equal plain.TimeoutForStage (ValueSome (System.TimeSpan.FromSeconds 20.0)) "a plain pipeline should record the stage budget"
            Expect.equal plain.TimeoutForStep (ValueSome (System.TimeSpan.FromSeconds 10.0)) "a plain pipeline should record the step budget"
            Expect.equal plain.WorkingDir (ValueSome "/plain") "a plain pipeline should record the working directory"
            Expect.equal (Map.tryFind "PARTAS_BUILD_T8B" plain.EnvVars) (Some "plain") "a plain pipeline should record the variable"
            Expect.equal plain.AcceptableExitCodes (set [ 0; 2 ]) "a plain pipeline should record the exit codes"

            Expect.equal built.Timeout (ValueSome (System.TimeSpan.FromSeconds 45.0)) "a setting before the declaring stage should reach the pipeline"
            Expect.equal built.TimeoutForStage (ValueSome (System.TimeSpan.FromSeconds 15.0)) "an input-aware pipeline should record the stage budget"
            Expect.equal built.TimeoutForStep (ValueSome (System.TimeSpan.FromSeconds 5.0)) "an input-aware pipeline should record the step budget"
            Expect.equal built.WorkingDir (ValueSome (System.IO.DirectoryInfo("/spec").FullName)) "an input-aware pipeline should record the working directory"
            Expect.equal (Map.tryFind "PARTAS_BUILD_T8B" built.EnvVars) (Some "spec") "an input-aware pipeline should record the variable"
            Expect.equal built.AcceptableExitCodes (set [ 0; 3 ]) "an input-aware pipeline should record the exit codes"
        }

        test "one budget setting implementation serves every pipeline builder state" {
            expectOneImplementation [ "envVars"; "acceptExitCodes" ]
            expectOverloadsOnly [ "timeout", 3; "timeoutForStage", 3; "timeoutForStep", 3; "workingDir", 2 ]
        }
    ]
