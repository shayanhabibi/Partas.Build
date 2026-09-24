[<AutoOpen>]
module Partas.Build.PipelineBuilder

open Partas.Build
open Partas.Build.Internal

/// Appends a stage to the pipeline being built.
let inline private addStage (stage: StageContext): BuildPipeline = fun ctx -> { ctx with Stages = ctx.Stages @ [ stage ] }

/// Appends several stages to the pipeline being built, in order.
let inline private addStages (stages: StageContext seq): BuildPipeline = fun ctx -> { ctx with Stages = ctx.Stages @ List.ofSeq stages }

let private withStageInputs (stage: StageContext) (spec: InputSpec<'T>): InputSpec<'T> =
    { spec with Inputs = InputSpec.union [ StageContext.declaredInputs stage; spec.Inputs ] }

/// A plain BuildPipeline contains no parsed values. Traverse its declarations on an empty context
/// so a later InputSpec branch still sees producer inputs before the command parses anything.
let private buildInputs name (build: BuildPipeline) =
    let declared = PipelineContext.create name |> build
    InputSpec.union [ for stage in declared.Stages @ declared.PostStages -> StageContext.declaredInputs stage ]

let private withBuildInputs name (build: BuildPipeline) (spec: InputSpec<'T>): InputSpec<'T> =
    { spec with Inputs = InputSpec.union [ buildInputs name build; spec.Inputs ] }

/// Runs the accumulated builder and re-parents every stage onto the finished pipeline.
let private finish (name: string) (build: BuildPipeline) =
    let ctx = PipelineContext.create name |> build
    { ctx with
        Stages =
            ctx.Stages
            |> List.map (fun stage -> { stage with ParentContext = ValueSome(StageParent.Pipeline ctx) })
        PostStages =
            ctx.PostStages
            |> List.map (fun stage -> { stage with ParentContext = ValueSome(StageParent.Pipeline ctx) })
    }

/// <summary>
/// Builds a pipeline from stages.
/// </summary>
/// <remarks>
/// A pipeline declaring nothing stays a plain <c>PipelineContext</c> and runs without a <c>ParseResult</c>;
/// as soon as one stage is an <c>InputSpec&lt;StageContext></c> the pipeline becomes an
/// <c>InputSpec&lt;PipelineContext></c> and the inputs of every stage are unioned into it. The members that
/// change representation — <c>Yield</c>, <c>Delay</c>, <c>Combine</c>, <c>For</c>, <c>Run</c> — come in one
/// flavour per representation; the settings inherited from <c>PipelineSettingsBuilder</c> are generic in it.
/// </remarks>
type PipelineBuilder(name: string) =
    inherit PipelineSettingsBuilder()
    // =================================================================
    //                              Run
    // =================================================================
    member inline _.Run(_: unit) = ()
    member _.Run(build: BuildPipeline): PipelineContext = finish name build
    member _.Run(spec: InputSpec<BuildPipeline>): InputSpec<PipelineContext> = InputSpec.map (finish name) spec
    // =================================================================
    //                              Yield
    // =================================================================
    member inline _.Yield(_: unit): BuildPipeline = id
    member inline _.Yield(stage: StageContext): BuildPipeline = addStage stage
    member inline _.Yield(spec: InputSpec<StageContext>): InputSpec<BuildPipeline> = InputSpec.map addStage spec
    member inline _.Yield([<InlineIfLambda>] condition: BuildStageIsActive): BuildStageIsActive = condition
    member inline _.Yield(stages: StageContext seq): BuildPipeline = addStages stages
    member inline _.Yield(spec: InputSpec<StageContext seq>): InputSpec<BuildPipeline> = InputSpec.map addStages spec
    member inline _.Yield(spec: InputSpec<StageContext list>): InputSpec<BuildPipeline> = InputSpec.map addStages spec
    /// A list of ready-made blocks - `[ Blocks.restore; Blocks.build ]` - rather than one block yielding many stages.
    member inline _.Yield(specs: InputSpec<StageContext> seq): InputSpec<BuildPipeline> =
        InputSpec.map addStages (InputSpec.sequence specs)
    // =================================================================
    //                              Zero
    // =================================================================
    // Without this an `if ... then stage ...` with no `else` is `FS0708` rather than a stage that is simply absent.
    member inline _.Zero(): BuildPipeline = id
    // =================================================================
    //                            YieldFrom
    // =================================================================
    member inline _.YieldFrom(stages: StageContext seq): BuildPipeline = addStages stages
    member inline _.YieldFrom(specs: InputSpec<StageContext> seq): InputSpec<BuildPipeline> =
        InputSpec.map addStages (InputSpec.sequence specs)
    // =================================================================
    //                              Delay
    // =================================================================
    member inline _.Delay([<InlineIfLambda>] fn: unit -> unit) = fn()
    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildPipeline): BuildPipeline = fn()
    member inline _.Delay([<InlineIfLambda>] fn: unit -> StageContext): BuildPipeline = addStage (fn())
    member inline _.Delay([<InlineIfLambda>] fn: unit -> InputSpec<BuildPipeline>): InputSpec<BuildPipeline> = fn()
    member inline _.Delay([<InlineIfLambda>] fn: unit -> InputSpec<StageContext>): InputSpec<BuildPipeline> = InputSpec.map addStage (fn())
    // =================================================================
    //                              Combine
    // =================================================================
    member inline _.Combine([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] rest: BuildPipeline): BuildPipeline = build >> rest
    member inline _.Combine(stage: StageContext, [<InlineIfLambda>] build: BuildPipeline): BuildPipeline =
        addStage stage >> build
    member _.Combine(stage: StageContext, spec: InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map (fun build -> addStage stage >> build) spec |> withStageInputs stage
    member _.Combine(build: BuildPipeline, rest: InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map (fun rest -> build >> rest) rest |> withBuildInputs name build
    member _.Combine(spec: InputSpec<StageContext>, build: BuildPipeline): InputSpec<BuildPipeline> =
        InputSpec.map (fun stage -> addStage stage >> build) spec |> withBuildInputs name build
    member inline _.Combine(spec: InputSpec<StageContext>, rest: InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map2 (fun stage build -> addStage stage >> build) spec rest
    member inline _.Combine(build: InputSpec<BuildPipeline>, rest: InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map2 (fun build rest -> build >> rest) build rest
    member _.Combine(spec: InputSpec<BuildPipeline>, rest: BuildPipeline): InputSpec<BuildPipeline> =
        InputSpec.map (fun build -> build >> rest) spec |> withBuildInputs name rest
    member _.Combine(spec: InputSpec<BuildPipeline>, stage: StageContext): InputSpec<BuildPipeline> =
        InputSpec.map (fun build -> build >> addStage stage) spec |> withStageInputs stage
    // =================================================================
    //                              For
    // =================================================================
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> StageContext): BuildPipeline =
        addStages (Seq.map fn collection)
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> BuildPipeline): BuildPipeline =
        fun ctx -> collection |> Seq.fold (fun ctx item -> fn item ctx) ctx
    member inline _.For(spec: InputSpec<'Collection> when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> StageContext): InputSpec<BuildPipeline> =
        InputSpec.map (fun collection ctx -> { ctx with Stages = collection |> Seq.map fn |> Seq.toList |> List.append ctx.Stages }) spec
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> InputSpec<StageContext>)
        : InputSpec<BuildPipeline> =
        InputSpec.map addStages (InputSpec.traverse fn collection)
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> InputSpec<BuildPipeline>)
        : InputSpec<BuildPipeline> =
        InputSpec.map (fun builds ctx -> builds |> List.fold (fun ctx build -> build ctx) ctx) (InputSpec.traverse fn collection)
    member inline _.For([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: unit -> BuildPipeline): BuildPipeline = build >> fn()
    member inline _.For([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: unit -> StageContext): BuildPipeline = build >> addStage (fn())
    member _.For(build: BuildPipeline, fn: unit -> InputSpec<StageContext>): InputSpec<BuildPipeline> =
        InputSpec.map (fun stage -> build >> addStage stage) (fn()) |> withBuildInputs name build
    member _.For(spec: InputSpec<BuildPipeline>, fn: unit -> BuildPipeline): InputSpec<BuildPipeline> =
        let rest = fn()
        InputSpec.map (fun build -> build >> rest) spec |> withBuildInputs name rest
    member _.For(spec: InputSpec<BuildPipeline>, fn: unit -> StageContext): InputSpec<BuildPipeline> =
        let stage = fn()
        InputSpec.map (fun build -> build >> addStage stage) spec |> withStageInputs stage
    member inline _.For(spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: unit -> InputSpec<StageContext>): InputSpec<BuildPipeline> =
        InputSpec.map2 (fun build stage -> build >> addStage stage) spec (fn())
    member _.For(build: BuildPipeline, fn: unit -> InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map (fun rest -> build >> rest) (fn()) |> withBuildInputs name build
    member inline _.For(spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: unit -> InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map2 (>>) spec (fn())

/// <summary>Folds ready-made stages into one unnamed pipeline.</summary>
/// <remarks>
/// This is what a <c>command</c> does with stages yielded straight into it: they become the stages of a single
/// implicit pipeline, which then takes the command's own name and description.
/// </remarks>
let internal pipelineOfStages (stages: StageContext seq): PipelineContext = finish null (addStages stages)

/// <summary>Builds a named pipeline: stages run in order, under the settings the pipeline gives them.</summary>
/// <remarks>
/// Run it through a <c>command</c>, which registers the CLI inputs its stages declare, or directly with
/// <c>PipelineContext.run</c> when it declares none.
/// </remarks>
/// <example>
/// <code lang="fsharp">
/// pipeline "ci" {
///     workingDir __SOURCE_DIRECTORY__
///     timeout 600
///     stage "restore" { run "dotnet restore" }
///     stage "build" { run "dotnet build --no-restore" }
///     post [ stage "report" { echo "done" } ]
/// }
/// </code>
/// </example>
let inline pipeline name = PipelineBuilder(name)
