namespace Partas.Build.Internal

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
