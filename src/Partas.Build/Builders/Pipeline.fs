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

    /// <summary>Sets the description shown for the pipeline.</summary>
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
    /// <summary>Sets the default timeout applied to each step in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/timeoutUnits/*"/>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildPipeline, seconds: int<second>): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(float seconds)) }
    /// <summary>Sets the default timeout applied to each step in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/timeoutUnits/*"/>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildPipeline, seconds: float): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(seconds)) }
    /// <summary>Sets the default timeout applied to each step in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/timeoutUnits/*"/>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildPipeline, timeSpan: TimeSpan): BuildPipeline
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome timeSpan }

    /// <summary>Adds environment variables inherited by every stage in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/envVars/*"/>
    [<CustomOperation>] member inline _.
        envVars
        ([<InlineIfLambda>] build: BuildPipeline, kvs: seq<string * string>): BuildPipeline
        = build >> fun ctx -> { ctx with EnvVars = kvs |> Seq.fold (fun state (k, v) -> Map.add k v state) ctx.EnvVars }

    /// <summary>Sets which process exit codes count as success for the whole pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/acceptableExitCodes/*"/>
    [<CustomOperation>] member inline _.
        acceptExitCodes
        ([<InlineIfLambda>] build: BuildPipeline, codes: int seq): BuildPipeline
        = build >> fun ctx -> { ctx with AcceptableExitCodes = set codes }

    /// <summary>Sets the directory commands run in, for every stage that does not override it.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        workingDir
        ([<InlineIfLambda>] build: BuildPipeline, path: IO.DirectoryInfo): BuildPipeline
        = build >> fun ctx -> { ctx with WorkingDir = ValueSome path.FullName }

    /// <summary>Sets the directory commands run in, for every stage that does not override it.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        workingDir
        ([<InlineIfLambda>] build: BuildPipeline, path: string): BuildPipeline
        = build >> fun ctx -> { ctx with WorkingDir = ValueSome path }

    /// <summary>Stops each step prefixing its console output with the stage and step index.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        noPrefixForStep
        ([<InlineIfLambda>] build: BuildPipeline, ?flag: bool): BuildPipeline
        = build >> fun ctx -> { ctx with NoPrefixForStep = defaultArg flag true }

    /// <summary>Stops redirecting child process stdout/stderr, letting them write to the console directly.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        noStdRedirectForStep
        ([<InlineIfLambda>] build: BuildPipeline, ?flag: bool): BuildPipeline
        = build >> fun ctx -> { ctx with NoStdRedirectForStep = defaultArg flag true }

    /// <summary>Sends the output of every stage's steps somewhere other than the console.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        outputTo
        ([<InlineIfLambda>] build: BuildPipeline, output: StageOutput): BuildPipeline
        = build >> fun ctx -> { ctx with Output = ValueSome output }

    /// <summary>Drops the output of every stage's steps.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        silentOutput
        ([<InlineIfLambda>] build: BuildPipeline): BuildPipeline
        = build >> fun ctx -> { ctx with Output = ValueSome StageOutput.Silent }

    /// <summary>Holds every stage's step output back, lifting it into the error message when a step fails.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        captureOutput
        ([<InlineIfLambda>] build: BuildPipeline, ?capture: OutputCapture): BuildPipeline
        = build >> fun ctx -> { ctx with Output = ValueSome(StageOutput.Captured(defaultArg capture (OutputCapture()))) }

    /// <summary>Hands each line of step output to <paramref name="write"/> as it arrives.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        redirectOutput
        ([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] write: StdStream -> string -> unit): BuildPipeline
        = build >> fun ctx -> { ctx with Output = ValueSome(StageOutput.Redirect write) }

    /// <summary>Runs a function immediately before each stage of the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/hooks/*"/>
    [<CustomOperation>] member inline _.
        runBeforeEachStage
        ([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: StageContext -> unit): BuildPipeline
        = build >> fun ctx -> { ctx with RunBeforeEachStage = fn }

    /// <summary>Runs a function immediately after each stage of the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/hooks/*"/>
    [<CustomOperation>] member inline _.
        runAfterEachStage
        ([<InlineIfLambda>] build: BuildPipeline, [<InlineIfLambda>] fn: StageContext -> unit): BuildPipeline
        = build >> fun ctx -> { ctx with RunAfterEachStage = fn }

    /// <summary>Sets the stages that run after the main stages, whether or not the pipeline succeeded.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/postStages/*"/>
    [<CustomOperation>] member inline _.
        post
        ([<InlineIfLambda>] build: BuildPipeline, stages: StageContext list): BuildPipeline
        = build >> fun ctx -> { ctx with PostStages = stages }

    // Mirrors of every setting above, for a pipeline that has already picked up a stage declaring inputs.
    // Without these, placing a setting *after* such a stage is an overload error rather than a no-op.
    /// <summary>Sets the description shown for the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        description
        (spec: InputSpec<BuildPipeline>, desc: string): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.description(build, desc)) spec
    /// <summary>Sets a timeout for the pipeline as a whole.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeout
        (spec: InputSpec<BuildPipeline>, seconds: int<second>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeout(build, seconds)) spec
    /// <summary>Sets a timeout for the pipeline as a whole.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeout
        (spec: InputSpec<BuildPipeline>, seconds: float): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeout(build, seconds)) spec
    /// <summary>Sets a timeout for the pipeline as a whole.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeout
        (spec: InputSpec<BuildPipeline>, timeSpan: TimeSpan): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeout(build, timeSpan)) spec
    /// <summary>Sets the default timeout applied to each stage in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStage
        (spec: InputSpec<BuildPipeline>, seconds: int<second>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStage(build, seconds)) spec
    /// <summary>Sets the default timeout applied to each stage in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStage
        (spec: InputSpec<BuildPipeline>, seconds: float): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStage(build, seconds)) spec
    /// <summary>Sets the default timeout applied to each stage in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStage
        (spec: InputSpec<BuildPipeline>, timeSpan: TimeSpan): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStage(build, timeSpan)) spec
    /// <summary>Sets the default timeout applied to each step in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildPipeline>, seconds: int<second>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStep(build, seconds)) spec
    /// <summary>Sets the default timeout applied to each step in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildPipeline>, seconds: float): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStep(build, seconds)) spec
    /// <summary>Sets the default timeout applied to each step in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildPipeline>, timeSpan: TimeSpan): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.timeoutForStep(build, timeSpan)) spec
    /// <summary>Adds environment variables inherited by every stage in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        envVars
        (spec: InputSpec<BuildPipeline>, kvs: seq<string * string>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.envVars(build, kvs)) spec
    /// <summary>Sets which process exit codes count as success for the whole pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        acceptExitCodes
        (spec: InputSpec<BuildPipeline>, codes: int seq): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.acceptExitCodes(build, codes)) spec
    /// <summary>Sets the directory commands run in, for every stage that does not override it.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        workingDir
        (spec: InputSpec<BuildPipeline>, path: IO.DirectoryInfo): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.workingDir(build, path)) spec
    /// <summary>Sets the directory commands run in, for every stage that does not override it.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        workingDir
        (spec: InputSpec<BuildPipeline>, path: string): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.workingDir(build, path)) spec
    /// <summary>Stops each step prefixing its console output with the stage and step index.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        noPrefixForStep
        (spec: InputSpec<BuildPipeline>, ?flag: bool): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.noPrefixForStep(build, ?flag = flag)) spec
    /// <summary>Stops redirecting child process stdout/stderr, letting them write to the console directly.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        noStdRedirectForStep
        (spec: InputSpec<BuildPipeline>, ?flag: bool): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.noStdRedirectForStep(build, ?flag = flag)) spec
    /// <summary>Sends the output of every stage's steps somewhere other than the console.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        outputTo
        (spec: InputSpec<BuildPipeline>, output: StageOutput): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.outputTo(build, output)) spec
    /// <summary>Drops the output of every stage's steps.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        silentOutput
        (spec: InputSpec<BuildPipeline>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.silentOutput build) spec
    /// <summary>Holds every stage's step output back, lifting it into the error message when a step fails.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        captureOutput
        (spec: InputSpec<BuildPipeline>, ?capture: OutputCapture): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.captureOutput(build, ?capture = capture)) spec
    /// <summary>Hands each line of step output to <paramref name="write"/> as it arrives.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        redirectOutput
        (spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] write: StdStream -> string -> unit): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.redirectOutput(build, write)) spec
    /// <summary>Runs a function immediately before each stage of the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        runBeforeEachStage
        (spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: StageContext -> unit): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.runBeforeEachStage(build, fn)) spec
    /// <summary>Runs a function immediately after each stage of the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        runAfterEachStage
        (spec: InputSpec<BuildPipeline>, [<InlineIfLambda>] fn: StageContext -> unit): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.runAfterEachStage(build, fn)) spec
    /// <summary>Sets the stages that run after the main stages, whether or not the pipeline succeeded.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/mirror/*"/>
    [<CustomOperation>] member inline this.
        post
        (spec: InputSpec<BuildPipeline>, stages: StageContext list): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.post(build, stages)) spec
    [<CustomOperation>] member inline _.
        verbosity
        ([<InlineIfLambda>] build: BuildPipeline, verbosity: Verbosity): BuildPipeline
        = build >> fun ctx -> { ctx with Verbosity = ValueSome verbosity }
    [<CustomOperation>] member inline _.
        verbose
        ([<InlineIfLambda>] build: BuildPipeline): BuildPipeline
        = build >> fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Verbose }
    [<CustomOperation>] member inline _.
        quiet
        ([<InlineIfLambda>] build: BuildPipeline): BuildPipeline
        = build >> fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Quiet }
    [<CustomOperation>] member inline this.
        verbosity
        (build: InputSpec<BuildPipeline>, verbosity: Verbosity): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.verbosity(build, verbosity)) build
    [<CustomOperation>] member inline this.
        verbose
        (build: InputSpec<BuildPipeline>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.verbose(build)) build
    [<CustomOperation>] member inline this.
        quiet
        (build: InputSpec<BuildPipeline>): InputSpec<BuildPipeline>
        = InputSpec.map (fun (build: BuildPipeline) -> this.quiet(build)) build
let inline pipeline name = PipelineBuilder(name)
