[<AutoOpen>]
module Partas.Build.PipelineBuilder

open System
open Spectre.Console
open Partas.Build
open Partas.Build.Internal

/// Appends a stage to the pipeline being built.
let inline private addStage (stage: StageContext): BuildPipeline = fun ctx -> { ctx with Stages = ctx.Stages @ [ stage ] }

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
/// Members come in two flavours: one over <c>BuildPipeline</c>, and one over
/// <c>InputSpec&lt;BuildPipeline></c> for pipelines containing at least one stage that declares CLI inputs.
/// A pipeline declaring nothing stays a plain <c>PipelineContext</c> and runs without a <c>ParseResult</c>;
/// as soon as one stage is an <c>InputSpec&lt;StageContext></c> the pipeline becomes an
/// <c>InputSpec&lt;PipelineContext></c> and the inputs of every stage are unioned into it.
/// </remarks>
type PipelineBuilder(name: string) =
    member inline _.Run(_: unit) = ()
    member _.Run(build: BuildPipeline): PipelineContext = finish name build
    member _.Run(spec: InputSpec<BuildPipeline>): InputSpec<PipelineContext> = InputSpec.map (finish name) spec
    member inline _.Yield(_: unit): BuildPipeline = id
    member inline _.Yield(stage: StageContext) = stage
    member inline _.Yield(spec: InputSpec<StageContext>) = spec
    member inline _.Delay([<InlineIfLambda>] fn: unit -> unit) = fn()
    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildPipeline): BuildPipeline = fn()
    member inline _.Delay([<InlineIfLambda>] fn: unit -> InputSpec<BuildPipeline>): InputSpec<BuildPipeline> = fn()
    member inline _.Delay([<InlineIfLambda>] fn: unit -> InputSpec<StageContext>): InputSpec<BuildPipeline> =
        InputSpec.map addStage (fn())
    member inline _.Delay([<InlineIfLambda>] fn): BuildPipeline = fun ctx -> { ctx with Stages = ctx.Stages @ [ fn() ] }
    member inline _.Combine(stage: StageContext, [<InlineIfLambda>] build: BuildPipeline): BuildPipeline =
        addStage stage >> build
    member inline _.Combine(stage: StageContext, spec: InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map (fun build -> addStage stage >> build) spec
    member inline _.Combine(spec: InputSpec<StageContext>, [<InlineIfLambda>] build: BuildPipeline): InputSpec<BuildPipeline> =
        InputSpec.map (fun stage -> addStage stage >> build) spec
    member inline _.Combine(spec: InputSpec<StageContext>, rest: InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map2 (fun stage build -> addStage stage >> build) spec rest
    member inline _.Combine([<InlineIfLambda>] build: BuildPipeline, rest: InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map (fun rest -> build >> rest) rest
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> StageContext): BuildPipeline = fun ctx ->
        { ctx with Stages = collection |> Seq.map fn |> Seq.toList |> List.append ctx.Stages }
    member inline _.For([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: unit -> BuildPipeline): BuildPipeline = build >> fn()
    member inline _.For([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: unit -> StageContext): BuildPipeline = build >> addStage (fn())
    member inline _.For([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: unit -> InputSpec<StageContext>): InputSpec<BuildPipeline> =
        InputSpec.map (fun stage -> build >> addStage stage) (fn())
    member inline _.For(spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: unit -> BuildPipeline): InputSpec<BuildPipeline> =
        InputSpec.map (fun build -> build >> fn()) spec
    member inline _.For(spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: unit -> StageContext): InputSpec<BuildPipeline> =
        InputSpec.map (fun build -> build >> addStage (fn())) spec
    member inline _.For(spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: unit -> InputSpec<StageContext>): InputSpec<BuildPipeline> =
        InputSpec.map2 (fun build stage -> build >> addStage stage) spec (fn())
    member inline _.For([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: unit -> InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map (fun rest -> build >> rest) (fn())
    member inline _.For(spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: unit -> InputSpec<BuildPipeline>): InputSpec<BuildPipeline> =
        InputSpec.map2 (>>) spec (fn())
    member inline _.Yield([<InlineIfLambda>] condition: BuildStageIsActive): BuildStageIsActive = condition
    // member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildStageIsActive): BuildPipeline = fun ctx ->
    // member inline _.Combine([<InlineIfLambda>] condition: BuildStageIsActive) = buildPipelineV

    [<CustomOperation>] member inline _.
        description
        ([<InlineIfLambda>] build: BuildPipeline, desc): BuildPipeline
        = build >> fun ctx -> { ctx with Description = ValueSome desc }
    /// <summary>Sets the total timeout for the entire pipeline execution.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/timeoutUnits/*"/>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        timeout
        ([<InlineIfLambda>] build: BuildPipeline, seconds: int<second>): BuildPipeline
        = build >> fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds(float seconds)) }
    /// <summary>Sets the total timeout for the entire pipeline execution (accepts seconds as float).</summary>
    [<CustomOperation>] member inline _.
        timeout
        ([<InlineIfLambda>] build: BuildPipeline, seconds: float): BuildPipeline
        = build >> fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds(seconds)) }
    /// <summary>Sets the total timeout for the entire pipeline execution (accepts TimeSpan).</summary>
    [<CustomOperation>] member inline _.
        timeout
        ([<InlineIfLambda>] build: BuildPipeline, timeSpan: TimeSpan): BuildPipeline
        = build >> fun ctx -> { ctx with Timeout = ValueSome timeSpan }
    /// <summary>Sets the default timeout for each individual stage in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/timeoutUnits/*"/>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStage
        ([<InlineIfLambda>] build: BuildPipeline, seconds: int<second>): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStage = ValueSome(TimeSpan.FromSeconds(float seconds)) }
    /// <summary>Sets the default timeout for each individual stage in the pipeline (accepts seconds as float).</summary>
    [<CustomOperation>] member inline _.
        timeoutForStage
        ([<InlineIfLambda>] build: BuildPipeline, seconds: float): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStage = ValueSome(TimeSpan.FromSeconds(seconds)) }
    /// <summary>Sets the default timeout for each individual stage in the pipeline (accepts TimeSpan).</summary>
    [<CustomOperation>] member inline _.
        timeoutForStage
        ([<InlineIfLambda>] build: BuildPipeline, timeSpan: TimeSpan): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStage = ValueSome timeSpan }
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildPipeline, seconds: int<second>): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(float seconds)) }
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildPipeline, seconds: float): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(seconds)) }
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildPipeline, timeSpan: TimeSpan): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome timeSpan }

    [<CustomOperation>] member inline _.
        envVars
        ([<InlineIfLambda>] build: BuildPipeline, kvs: seq<string * string>): BuildPipeline
        = build >> fun ctx -> { ctx with EnvVars = kvs |> Seq.fold (fun state (k, v) -> Map.add k v state) ctx.EnvVars }

    [<CustomOperation>] member inline _.
        acceptExitCodes
        ([<InlineIfLambda>] build: BuildPipeline, codes: int seq): BuildPipeline
        = build >> fun ctx -> { ctx with AcceptableExitCodes = set codes }

    [<CustomOperation>] member inline _.
        workingDir
        ([<InlineIfLambda>] build: BuildPipeline, path: IO.DirectoryInfo): BuildPipeline
        = build >> fun ctx -> { ctx with WorkingDir = ValueSome path.FullName }

    [<CustomOperation>] member inline _.
        workingDir
        ([<InlineIfLambda>] build: BuildPipeline, path: string): BuildPipeline
        = build >> fun ctx -> { ctx with WorkingDir = ValueSome path }

    [<CustomOperation>] member inline _.
        noPrefixForStep
        ([<InlineIfLambda>] build: BuildPipeline, ?flag: bool): BuildPipeline
        = build >> fun ctx -> { ctx with NoPrefixForStep = defaultArg flag true }

    [<CustomOperation>] member inline _.
        noStdRedirectForStep
        ([<InlineIfLambda>] build: BuildPipeline, ?flag: bool): BuildPipeline
        = build >> fun ctx -> { ctx with NoStdRedirectForStep = defaultArg flag true }

    [<CustomOperation>] member inline _.
        runBeforeEachStage
        ([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: StageContext -> unit): BuildPipeline
        = build >> fun ctx -> { ctx with RunBeforeEachStage = fn }

    [<CustomOperation>] member inline _.
        runAfterEachStage
        ([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: StageContext -> unit): BuildPipeline
        = build >> fun ctx -> { ctx with RunAfterEachStage = fn }

    [<CustomOperation>] member inline _.
        post
        ([<InlineIfLambda>] build: BuildPipeline, stages: StageContext list): BuildPipeline
        = build >> fun ctx -> { ctx with PostStages = stages }

    // Mirrors of every setting above, for a pipeline that has already picked up a stage declaring inputs.
    // Without these, placing a setting *after* such a stage is an overload error rather than a no-op.
    [<CustomOperation>] member inline this.
        description
        (spec: InputSpec<BuildPipeline>, desc: string): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.description(build, desc)) spec
    [<CustomOperation>] member inline this.
        timeout
        (spec: InputSpec<BuildPipeline>, seconds: int<second>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeout(build, seconds)) spec
    [<CustomOperation>] member inline this.
        timeout
        (spec: InputSpec<BuildPipeline>, seconds: float): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeout(build, seconds)) spec
    [<CustomOperation>] member inline this.
        timeout
        (spec: InputSpec<BuildPipeline>, timeSpan: TimeSpan): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeout(build, timeSpan)) spec
    [<CustomOperation>] member inline this.
        timeoutForStage
        (spec: InputSpec<BuildPipeline>, seconds: int<second>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStage(build, seconds)) spec
    [<CustomOperation>] member inline this.
        timeoutForStage
        (spec: InputSpec<BuildPipeline>, seconds: float): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStage(build, seconds)) spec
    [<CustomOperation>] member inline this.
        timeoutForStage
        (spec: InputSpec<BuildPipeline>, timeSpan: TimeSpan): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStage(build, timeSpan)) spec
    [<CustomOperation>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildPipeline>, seconds: int<second>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStep(build, seconds)) spec
    [<CustomOperation>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildPipeline>, seconds: float): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStep(build, seconds)) spec
    [<CustomOperation>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildPipeline>, timeSpan: TimeSpan): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStep(build, timeSpan)) spec
    [<CustomOperation>] member inline this.
        envVars
        (spec: InputSpec<BuildPipeline>, kvs: seq<string * string>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.envVars(build, kvs)) spec
    [<CustomOperation>] member inline this.
        acceptExitCodes
        (spec: InputSpec<BuildPipeline>, codes: int seq): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.acceptExitCodes(build, codes)) spec
    [<CustomOperation>] member inline this.
        workingDir
        (spec: InputSpec<BuildPipeline>, path: IO.DirectoryInfo): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.workingDir(build, path)) spec
    [<CustomOperation>] member inline this.
        workingDir
        (spec: InputSpec<BuildPipeline>, path: string): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.workingDir(build, path)) spec
    [<CustomOperation>] member inline this.
        noPrefixForStep
        (spec: InputSpec<BuildPipeline>, ?flag: bool): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.noPrefixForStep(build, ?flag = flag)) spec
    [<CustomOperation>] member inline this.
        noStdRedirectForStep
        (spec: InputSpec<BuildPipeline>, ?flag: bool): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.noStdRedirectForStep(build, ?flag = flag)) spec
    [<CustomOperation>] member inline this.
        runBeforeEachStage
        (spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: StageContext -> unit): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.runBeforeEachStage(build, fn)) spec
    [<CustomOperation>] member inline this.
        runAfterEachStage
        (spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: StageContext -> unit): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.runAfterEachStage(build, fn)) spec
    [<CustomOperation>] member inline this.
        post
        (spec: InputSpec<BuildPipeline>, stages: StageContext list): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.post(build, stages)) spec

let inline pipeline name = PipelineBuilder(name)
