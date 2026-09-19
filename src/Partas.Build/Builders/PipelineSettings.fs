namespace Partas.Build.Internal

open System
open System.ComponentModel
open Partas.Build

/// <summary>Applies a state-preserving update to whichever representation a pipeline builder state currently holds.</summary>
/// <remarks>
/// The supported states are <c>BuildPipeline</c> and <c>InputSpec&lt;BuildPipeline></c>.
/// Any other state fails to resolve, and the compiler lists the two supported ones.
/// <para>Public: the inline members dispatching through it resolve in consuming assemblies.</para>
/// </remarks>
[<EditorBrowsable(EditorBrowsableState.Never)>]
type PipelineMap =
    /// <summary>Composes the update after the state's own build function.</summary>
    static member Map(build: BuildPipeline, update: PipelineContext -> PipelineContext): BuildPipeline =
        build >> update

    /// <summary>Composes the update after the build function the specification yields, deferring the read.</summary>
    static member Map(spec: InputSpec<BuildPipeline>, update: PipelineContext -> PipelineContext): InputSpec<BuildPipeline> =
        InputSpec.map (fun (build: BuildPipeline) -> build >> update) spec

    /// <summary>Dispatches <paramref name="update"/> to the <c>Map</c> overload of the state's representation.</summary>
    /// <param name="state" />
    /// <param name="update" />
    static member inline Apply(state: ^State, update: PipelineContext -> PipelineContext): ^State =
        ((^State or PipelineMap): (static member Map: ^State * (PipelineContext -> PipelineContext) -> ^State) (state, update))

/// <summary>Pipeline updates written once for every supported builder state.</summary>
[<EditorBrowsable(EditorBrowsableState.Never); CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module PipelineMap =
    /// <summary>Applies a state-preserving pipeline update to <paramref name="state"/>, keeping its representation.</summary>
    /// <param name="update" />
    /// <param name="state" />
    let inline mapPipeline ([<InlineIfLambda>] update: PipelineContext -> PipelineContext) (state: ^State): ^State =
        PipelineMap.Apply(state, update)

/// <summary>The pipeline settings whose implementation is shared by every builder state a pipeline CE can be in.</summary>
/// <remarks>
/// Each operation here replaces a pair of same-named members — one over <c>BuildPipeline</c>, one over its
/// <c>InputSpec</c> mirror — with a single member generic in the state. Representation-changing members
/// (<c>Yield</c>, <c>Delay</c>, <c>Combine</c>, <c>For</c>, <c>Run</c>) stay explicit in the builder that
/// inherits this one.
/// </remarks>
[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type PipelineSettingsBuilder() =

    /// <summary>Sets the description shown for the pipeline.</summary>
    [<CustomOperation>] member inline _.
        description
        (state: ^State, desc: string): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Description = ValueSome desc }) state

    /// <summary>Sets the total timeout for the entire pipeline execution.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/timeoutUnits/*"/>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        timeout
        (state: ^State, seconds: int<second>): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds(float seconds)) }) state

    /// <summary>Sets the total timeout for the entire pipeline execution (accepts seconds as float).</summary>
    [<CustomOperation>] member inline _.
        timeout
        (state: ^State, seconds: float): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds seconds) }) state

    /// <summary>Sets the total timeout for the entire pipeline execution (accepts TimeSpan).</summary>
    [<CustomOperation>] member inline _.
        timeout
        (state: ^State, timeSpan: TimeSpan): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Timeout = ValueSome timeSpan }) state

    /// <summary>Sets the default timeout for each individual stage in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/timeoutUnits/*"/>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStage
        (state: ^State, seconds: int<second>): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with TimeoutForStage = ValueSome(TimeSpan.FromSeconds(float seconds)) }) state

    /// <summary>Sets the default timeout for each individual stage in the pipeline (accepts seconds as float).</summary>
    [<CustomOperation>] member inline _.
        timeoutForStage
        (state: ^State, seconds: float): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with TimeoutForStage = ValueSome(TimeSpan.FromSeconds seconds) }) state

    /// <summary>Sets the default timeout for each individual stage in the pipeline (accepts TimeSpan).</summary>
    [<CustomOperation>] member inline _.
        timeoutForStage
        (state: ^State, timeSpan: TimeSpan): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with TimeoutForStage = ValueSome timeSpan }) state

    /// <summary>Sets the default timeout applied to each step in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/timeoutUnits/*"/>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStep
        (state: ^State, seconds: int<second>): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(float seconds)) }) state

    /// <summary>Sets the default timeout applied to each step in the pipeline (accepts seconds as float).</summary>
    [<CustomOperation>] member inline _.
        timeoutForStep
        (state: ^State, seconds: float): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds seconds) }) state

    /// <summary>Sets the default timeout applied to each step in the pipeline (accepts TimeSpan).</summary>
    [<CustomOperation>] member inline _.
        timeoutForStep
        (state: ^State, timeSpan: TimeSpan): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with TimeoutForStep = ValueSome timeSpan }) state

    /// <summary>Adds environment variables inherited by every stage in the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/envVars/*"/>
    [<CustomOperation>] member inline _.
        envVars
        (state: ^State, kvs: seq<string * string>): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with EnvVars = kvs |> Seq.fold (fun state (k, v) -> Map.add k v state) ctx.EnvVars }) state

    /// <summary>Sets which process exit codes count as success for the whole pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/acceptableExitCodes/*"/>
    [<CustomOperation>] member inline _.
        acceptExitCodes
        (state: ^State, codes: int seq): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with AcceptableExitCodes = set codes }) state

    /// <summary>Sets the directory commands run in, for every stage that does not override it.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        workingDir
        (state: ^State, path: IO.DirectoryInfo): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with WorkingDir = ValueSome path.FullName }) state

    /// <summary>Sets the directory commands run in, for every stage that does not override it.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        workingDir
        (state: ^State, path: string): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with WorkingDir = ValueSome path }) state

    /// <summary>Stops each step prefixing its console output with the stage and step index.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        noPrefixForStep
        (state: ^State, ?flag: bool): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with NoPrefixForStep = defaultArg flag true }) state

    /// <summary>Stops redirecting child process stdout/stderr, letting them write to the console directly.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        noStdRedirectForStep
        (state: ^State, ?flag: bool): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with NoStdRedirectForStep = defaultArg flag true }) state

    /// <summary>Sends the output of every stage's steps somewhere other than the console.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        outputTo
        (state: ^State, output: StageOutput): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Output = ValueSome output }) state

    /// <summary>Drops the output of every stage's steps.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        silentOutput
        (state: ^State): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Output = ValueSome StageOutput.Silent }) state

    /// <summary>Holds every stage's step output back, lifting it into the error message when a step fails.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        captureOutput
        (state: ^State, ?capture: OutputCapture): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Output = ValueSome(StageOutput.Captured(defaultArg capture (OutputCapture.create()))) }) state

    /// <summary>Hands each line of step output to <paramref name="write"/> as it arrives.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        redirectOutput
        (state: ^State, [<InlineIfLambda>] write: StdStream -> string -> unit): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Output = ValueSome(StageOutput.Redirect write) }) state

    /// <summary>Sets how much the pipeline reports about its own progress.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        verbosity
        (state: ^State, verbosity: Verbosity): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Verbosity = ValueSome verbosity }) state

    /// <summary>Reports the pipeline's progress at <c>Verbosity.Verbose</c>.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        verbose
        (state: ^State): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Verbose }) state

    /// <summary>Reports the pipeline's progress at <c>Verbosity.Quiet</c>.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/pipelineDefault/*"/>
    [<CustomOperation>] member inline _.
        quiet
        (state: ^State): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Quiet }) state

    /// <summary>Runs a function immediately before each stage of the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/hooks/*"/>
    [<CustomOperation>] member inline _.
        runBeforeEachStage
        (state: ^State, [<InlineIfLambda>] fn: StageContext -> unit): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with RunBeforeEachStage = fn }) state

    /// <summary>Runs a function immediately after each stage of the pipeline.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/hooks/*"/>
    [<CustomOperation>] member inline _.
        runAfterEachStage
        (state: ^State, [<InlineIfLambda>] fn: StageContext -> unit): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with RunAfterEachStage = fn }) state

    /// <summary>Sets the stages that run after the main stages, whether or not the pipeline succeeded.</summary>
    /// <include file="../xmldoc/pipeline.xml" path="/pipeline/postStages/*"/>
    [<CustomOperation>] member inline _.
        post
        (state: ^State, stages: StageContext list): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with PostStages = stages }) state

    /// <summary>Registers a handler to run when the pipeline fails.</summary>
    /// <remarks>
    /// It runs once per failed run, after the handlers of every stage of that run, and takes the failure the
    /// pipeline ends with as its primary cause. A run a cancellation ended — the pipeline's own timeout, or the
    /// console — runs none.
    /// </remarks>
    [<CustomOperation("onFailure")>] member inline _.
        onFailure
        (state: ^State, handler: FailureHandler): ^State
        = PipelineMap.mapPipeline (fun ctx -> { ctx with OnFailure = ctx.OnFailure @ [ handler ] }) state
