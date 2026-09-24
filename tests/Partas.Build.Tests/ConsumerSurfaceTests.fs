/// <summary>
/// The surface a consumer writes against, compiled without <c>open Partas.Build.Internal</c>.
/// </summary>
/// <remarks>
/// The file compiling is the first assertion: every annotation below names its type through <c>Partas.Build</c>.
/// Keep <c>Partas.Build.Internal</c> out of this file's opens and out of its qualified names.
/// </remarks>
module Partas.Build.Tests.ConsumerSurfaceTests

open System.CommandLine
open Expecto
open Partas.Build

let private quick = Input.option<bool> "--quick"

let private writing (name: string) : InputSpec<StageContext> = input {
    let! quick = quick
    return stage name {
        when' (not quick)
        run (fun (ctx: StageContext) -> StageContext.writeLine ctx StdStream.Out $"ran {ctx.Name}")
    }
}

let private parentName (ctx: StageContext) =
    match ctx.ParentContext with
    | ValueSome(StageParent.Pipeline pipeline) -> pipeline.Name
    | ValueSome(StageParent.Stage parent) -> parent.Name
    | ValueNone -> ""

let private recording (seen: ResizeArray<string>) : Operation<unit> = {
    Execute = fun (context: RuntimeContext) -> async { seen.Add $"{context.Stage.Name}#{int context.StepIndex}" }
}

let private renamed: BuildStage = fun (ctx: StageContext) -> { ctx with Name = ctx.Name + "!" }

let private withDescription: BuildCommand = fun (spec: CommandSpec) -> { spec with Description = ValueSome "described" }

let private invoke (built: Command) (commandLine: string) = built.Parse(commandLine).Invoke()

[<Tests>]
let tests =
    testList "consumer surface" [
        test "a stage factory typed through Partas.Build runs and routes its output" {
            let lines = ResizeArray<string>()

            let built =
                command "build" {
                    pipeline "build" {
                        quiet
                        stage "outer" {
                            outputTo (StageOutput.Redirect(fun _ line -> lines.Add line))
                            writing "inner"
                        }
                    }
                }

            Expect.equal (invoke built "") 0 "the pipeline should succeed"
            Expect.sequenceEqual lines [ "ran inner" ] "writeLine should reach the stage's redirect"
        }

        test "a stage's parent is matched through StageParent" {
            let seen = ResizeArray<string>()

            let built =
                command "build" {
                    pipeline "the-pipeline" {
                        quiet
                        stage "outer" {
                            run (fun (ctx: StageContext) -> seen.Add(parentName ctx))
                            stage "inner" { run (fun (ctx: StageContext) -> seen.Add(parentName ctx)) }
                        }
                    }
                }

            Expect.equal (invoke built "") 0 "the pipeline should succeed"
            Expect.sequenceEqual seen [ "the-pipeline"; "outer" ] "each stage should see its own parent"
        }

        test "an operation reads its RuntimeContext" {
            let seen = ResizeArray<string>()

            let built =
                command "build" {
                    pipeline "build" {
                        stage "op" {
                            echo "first"
                            runOperation (recording seen)
                        }
                    }
                }

            Expect.equal (invoke built "") 0 "the pipeline should succeed"
            Expect.sequenceEqual seen [ "op#1" ] "the operation should see its stage and step index"
        }

        test "the Build aliases and CommandSpec are nameable" {
            let stage = renamed (stage "compile" { echo "compiling" })
            Expect.equal stage.Name "compile!" "a BuildStage should apply to a StageContext"

            let spec: CommandSpec = withDescription { Name = "c"; DisplayName = ValueNone; Description = ValueNone
                                                      PipelineDefaults = id; Aliases = []; Hidden = false; ExtraInputs = []
                                                      Pipelines = []; SubCommands = []; ParserConfiguration = ValueNone
                                                      InvocationConfiguration = ValueNone }
            Expect.equal spec.Description (ValueSome "described") "a BuildCommand should apply to a CommandSpec"

            let pipelineSpec: InputSpec<PipelineContext> = pipeline "p" { writing "compile" }
            Expect.isNonEmpty pipelineSpec.Inputs "the pipeline should declare the stage's --quick"
        }
    ]
