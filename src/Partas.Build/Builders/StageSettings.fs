namespace Partas.Build.Internal

open System
open System.ComponentModel
open Partas.Build

/// <summary>Applies a state-preserving update to whichever representation a stage builder state currently holds.</summary>
/// <remarks>
/// The supported states are <c>BuildStage</c>, <c>InputSpec&lt;BuildStage></c> and <c>InputSpec&lt;StageContext></c>.
/// Any other state fails to resolve, and the compiler lists the three supported ones.
/// <para>Public: the inline members dispatching through it resolve in consuming assemblies.</para>
/// </remarks>
[<EditorBrowsable(EditorBrowsableState.Never)>]
type StageMap =
    /// <summary>Composes the update after the state's own build function.</summary>
    static member Map(build: BuildStage, update: StageContext -> StageContext): BuildStage =
        build >> update

    /// <summary>Composes the update after the build function the specification yields, deferring the read.</summary>
    static member Map(spec: InputSpec<BuildStage>, update: StageContext -> StageContext): InputSpec<BuildStage> =
        InputSpec.map (fun (build: BuildStage) -> build >> update) spec

    /// <summary>Applies the update to the stage the specification yields, deferring the read.</summary>
    static member Map(spec: InputSpec<StageContext>, update: StageContext -> StageContext): InputSpec<StageContext> =
        InputSpec.map update spec

    /// <summary>Dispatches <paramref name="update"/> to the <c>Map</c> overload of the state's representation.</summary>
    /// <param name="state" />
    /// <param name="update" />
    static member inline Apply(state: ^State, update: StageContext -> StageContext): ^State =
        ((^State or StageMap): (static member Map: ^State * (StageContext -> StageContext) -> ^State) (state, update))

/// <summary>Stage updates written once for every supported builder state.</summary>
[<EditorBrowsable(EditorBrowsableState.Never); CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module StageMap =
    /// <summary>Applies a state-preserving stage update to <paramref name="state"/>, keeping its representation.</summary>
    /// <param name="update" />
    /// <param name="state" />
    let inline mapStage ([<InlineIfLambda>] update: StageContext -> StageContext) (state: ^State): ^State =
        StageMap.Apply(state, update)

/// <summary>The stage settings whose implementation is shared by every builder state a stage CE can be in.</summary>
/// <remarks>
/// Each operation here replaces a pair of same-named members — one over <c>BuildStage</c>, one over its
/// <c>InputSpec</c> mirror — with a single member generic in the state. Representation-changing members
/// (<c>Yield</c>, <c>Delay</c>, <c>Combine</c>, <c>For</c>, <c>Run</c>) stay explicit in the builder that
/// inherits this one.
/// </remarks>
[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type StageSettingsBuilder() =
    /// <summary>Sets how many further attempts the stage's steps get after a failing one.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/retry/*"/>
    [<CustomOperation("retry")>]
    member inline _.retry(state: ^State, count: int): ^State =
        StageMap.mapStage (fun ctx -> { ctx with Retry = max 0 count }) state

    /// <summary>Adds a step that runs <paramref name="execute"/> over the results of
    /// <paramref name="dependencies"/>, and declares those producers as the stage's prerequisites.</summary>
    /// <remarks>
    /// The stage is the consumer, so the settings written beside this operation — <c>retry</c>, conditions,
    /// timeouts — govern the consuming work itself rather than a wrapper around it.
    /// <para>The producers' CLI inputs join the stage's declared inputs, and reach the command before it
    /// parses.</para>
    /// </remarks>
    /// <param name="state" />
    /// <param name="dependencies" />
    /// <param name="execute" />
    [<CustomOperation("consumes")>]
    member inline _.consumes(state: ^State, dependencies: DependencySpec<'D>, execute: 'D -> Operation<unit>): ^State =
        StageMap.mapStage (Stage.consumes dependencies execute) state

    /// <summary>Registers a handler to run when this stage fails.</summary>
    /// <remarks>
    /// The handler runs once per failed execution of the stage, after that execution exhausts its <c>retry</c>
    /// attempts and before the handlers of the scopes enclosing it. A stage a retry recovers keeps its handlers
    /// back, and so does a cancelled one: the stage's own <c>timeout</c> and <c>timeoutForStep</c> are failures
    /// of the stage, while an ancestor's token, the pipeline's and the invocation's are cancellations.
    /// <para>The handler receives the causes the execution recorded and the producer values the invocation
    /// holds. An exception out of it is one more cause of the same scope, and the primary stays where it
    /// was.</para>
    /// <para>A stage run as a condition — the body of a <c>whenStage</c> — answers its condition by failing and
    /// belongs to that condition rather than to the run, so it keeps its handlers back throughout, <c>--explain</c>
    /// included.</para>
    /// </remarks>
    /// <param name="state" />
    /// <param name="handler" />
    [<CustomOperation("onFailure")>]
    member inline _.onFailure(state: ^State, handler: FailureHandler): ^State =
        StageMap.mapStage (StageContext.addFailureHandler handler) state

    /// <summary>Adds environment variables to the stage.</summary>
    /// <remarks>Variables set here override inherited values from parent contexts. A stage-level variable shadows any pipeline-level variable with the same name.</remarks>
    [<CustomOperation>]
    member inline _.envVars (state: ^State, kvs: seq<string * string>): ^State =
        StageMap.mapStage (fun ctx -> { ctx with EnvVars = kvs |> Seq.fold (fun acc (k, v) -> Map.add k v acc) ctx.EnvVars }) state
    /// <summary>Sets exit codes that are treated as successful.</summary>
    /// <remarks>By default, only exit code 0 is acceptable. Setting this replaces (rather than appends to) the default acceptable codes. A stage-level setting overrides the pipeline's.</remarks>
    [<CustomOperation>]
    member inline _.acceptExitCodes (state: ^State, codes: int seq): ^State =
        StageMap.mapStage (fun ctx -> { ctx with AcceptableExitCodes = set codes }) state
    /// <summary>Fails the pipeline if this stage is inactive.</summary>
    /// <remarks>By default, inactive stages are skipped without failure. Enable this to treat an inactive stage as a pipeline error.</remarks>
    [<CustomOperation>]
    member inline _. failIfIgnored (state: ^State, ?flag: bool): ^State =
        StageMap.mapStage (fun ctx -> { ctx with FailIfIgnored = defaultArg flag true }) state
    /// <summary>Fails the pipeline if no substages of this stage are active.</summary>
    /// <remarks>By default, stages with no active substages are skipped silently. Enable this to require at least one active substage.</remarks>
    [<CustomOperation>]
    member inline _. failIfNoActiveSubStage (state: ^State, ?flag: bool): ^State =
        StageMap.mapStage(fun ctx -> { ctx with FailIfNoActiveSubStage = defaultArg flag true }) state

    /// <summary>Continues executing remaining steps even if a step fails.</summary>
    /// <remarks>By default, a step failure stops execution of subsequent steps. Enable this to run all steps regardless of earlier failures.</remarks>
    [<CustomOperation>]
    member inline _. continueStepsOnFailure (state: ^State, ?flag): ^State =
        StageMap.mapStage(fun ctx -> { ctx with ContinueStepsOnFailure = defaultArg flag true }) state
    /// <summary>Continues pipeline execution even if this stage fails.</summary>
    /// <remarks>By default, a stage failure stops the entire pipeline. Enable this to allow post-stages and subsequent stages to run regardless of this stage's failure.</remarks>
    [<CustomOperation>]
    member inline _. continueStageOnFailure (state: ^State, ?flag): ^State =
        StageMap.mapStage (fun ctx -> { ctx with ContinueStageOnFailure = defaultArg flag true }) state
    /// <summary>Continues execution after a step failure and continues the pipeline after a stage failure.</summary>
    /// <remarks>This is a convenience operation equivalent to enabling both <c>continueStepsOnFailure</c> and <c>continueStageOnFailure</c>.</remarks>
    [<CustomOperation>]
    member inline _.continueOnStepFailure (state: ^State, ?flag): ^State =
            StageMap.mapStage (fun ctx ->
                let shouldCont = defaultArg flag true
                { ctx with ContinueStepsOnFailure = shouldCont; ContinueStageOnFailure = shouldCont })
                state
    /// <summary>Sets the overall timeout for the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeout/*"/>
    [<CustomOperation>]
    member inline _.timeout (state: ^State, seconds: int): ^State =
        StageMap.mapStage(fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds(float seconds)) }) state
    /// <summary>Sets the overall timeout for the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeout/*"/>
    [<CustomOperation>]
    member inline _.timeout (state: ^State, seconds: float): ^State =
        StageMap.mapStage (fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds(seconds)) }) state
    /// <summary>Sets the overall timeout for the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeout/*"/>
    [<CustomOperation>]
    member inline _.timeout (state: ^State, timespan: TimeSpan): ^State =
        StageMap.mapStage (fun ctx -> { ctx with Timeout = ValueSome timespan }) state
    /// <summary>Sets the timeout for each step in the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeoutForStep/*"/>
    [<CustomOperation>]
    member inline _. timeoutForStep(state: ^State, seconds: int): ^State =
        StageMap.mapStage (fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(float seconds)) }) state
    /// <summary>Sets the timeout for each step in the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeoutForStep/*"/>
    [<CustomOperation>]
    member inline _.timeoutForStep(state: ^State, seconds: float): ^State =
        StageMap.mapStage (fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(seconds)) }) state
    /// <summary>Sets the timeout for each step in the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeoutForStep/*"/>
    [<CustomOperation>]
    member inline _.timeoutForStep (state: ^State, timeSpan: TimeSpan): ^State =
        StageMap.mapStage (fun ctx -> { ctx with TimeoutForStep = ValueSome timeSpan }) state
    /// <summary>Enables or disables parallel execution of steps in this stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>]
    member inline _.parallel' (state: ^State, ?flag: bool): ^State =
        StageMap.mapStage (fun ctx -> { ctx with IsParallel = fun _ -> if defaultArg flag true then ValueSome -1 else ValueNone }) state
    /// <summary>Enables parallel execution of steps in this stage throttled to the given number of processes.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>]
    member inline _.parallel'(state: ^State, throttle: int): ^State =
        StageMap.mapStage (fun ctx -> { ctx with IsParallel = fun _ -> ValueSome throttle }) state
    /// <summary>Sets a condition for parallel execution of steps in this stage. Can either return a boolean switch, or the throttle count.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>]
    member inline _.parallel' (state: ^State, [<InlineIfLambda>] condition: StageContext -> int voption): ^State =
        StageMap.mapStage (fun ctx -> { ctx with IsParallel = condition }) state
    /// <summary>Sets a condition for parallel execution of steps in this stage. Can either return a boolean switch, or the throttle count.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>]
    member inline _.parallel' (state: ^State, [<InlineIfLambda>] condition: StageContext -> Choice<bool, int>): ^State =
       StageMap.mapStage (fun ctx -> { ctx with IsParallel = condition >> function Choice1Of2 b -> (if b then ValueSome -1 else ValueNone) | Choice2Of2 i -> ValueSome i }) state
    /// <summary>Sets a condition for parallel execution of steps in this stage. Can either return a boolean switch, or the throttle count.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>]
    member inline _.parallel' (state: ^State, [<InlineIfLambda>] condition: StageContext -> Choice<int, bool>): ^State =
        StageMap.mapStage (fun ctx -> { ctx with IsParallel = condition >> function Choice1Of2 i -> ValueSome i | Choice2Of2 true -> ValueSome -1 | _ -> ValueNone }) state
    /// <summary>Sets a condition for parallel execution of steps in this stage. Can either return a boolean switch, or the throttle count.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>]
    member inline _.parallel' (state: ^State, [<InlineIfLambda>] condition: StageContext -> bool): ^State =
        StageMap.mapStage (fun ctx -> { ctx with IsParallel = condition >> function true -> ValueSome -1 | false -> ValueNone }) state
    /// <summary>Sets the working directory for this stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/workingDir/*"/>
    [<CustomOperation>]
    member inline _.workingDir (state: ^State, path: string): ^State =
        StageMap.mapStage (fun ctx -> { ctx with WorkingDir = ValueSome path }) state
    /// <summary>Sets the working directory for this stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/workingDir/*"/>
    [<CustomOperation>]
    member inline _.workingDir (state: ^State, path: IO.DirectoryInfo): ^State =
        StageMap.mapStage (fun ctx -> { ctx with WorkingDir = ValueSome path.FullName }) state
    /// <summary>Suppresses the step number prefix in step output.</summary>
    /// <remarks>By default, each step's output is prefixed with its stage and step number. Enable this to output without the prefix.</remarks>
    [<CustomOperation>]
    member inline _.noPrefixForStep (state: ^State, ?flag: bool): ^State =
        StageMap.mapStage (fun ctx -> { ctx with NoPrefixForStep = defaultArg flag true }) state
    /// <summary>Disables stdout and stderr redirection for steps in this stage.</summary>
    /// <remarks>By default, step output is captured and logged. Enable this to let steps write directly to the console without redirection.</remarks>
    [<CustomOperation>]
    member inline _.noStdRedirectForStep(state: ^State, ?flag: bool): ^State =
        StageMap.mapStage (fun ctx -> { ctx with NoStdRedirectForStep = defaultArg flag true }) state
    /// <summary>Sends the output of this stage's steps somewhere other than the console.</summary>
    /// <remarks>
    /// Inherited by sub-stages that declare nothing of their own. Only the steps' output moves: the pipeline's
    /// log — the stage rules, the command lines, the timings — stays on the console, and <c>verbosity</c> is
    /// what quietens that. <c>noStdRedirectForStep</c> overrides this, since without redirection there is
    /// nothing to route.
    ///
    /// Named <c>outputTo</c> rather than <c>output</c> because a custom operation's name shadows every
    /// identifier of that name inside the CE, and <c>output</c> is a value a stage very often has in scope.
    /// </remarks>
    [<CustomOperation>]
    member inline _.outputTo (state, output: StageOutput): ^State =
        StageMap.mapStage (fun ctx -> { ctx with Output = ValueSome output }) state
    /// <summary>Drops the output of this stage's steps.</summary>
    /// <remarks>For a step whose noise is never worth reading. A failure still reports its exit code.</remarks>
    [<CustomOperation>]
    member inline _.silentOutput (state: ^State): ^State =
        StageMap.mapStage (fun ctx -> { ctx with Output = ValueSome StageOutput.Silent }) state
    /// <summary>Holds the output of this stage's steps back, and lifts it into the error message if one fails.</summary>
    /// <remarks>
    /// A quiet run that still says why it failed: stderr if the process used it, and everything it wrote
    /// otherwise. Pass an <c>OutputCapture</c> to keep a handle on the lines regardless of the outcome.
    /// </remarks>
    [<CustomOperation>] member inline _.
        captureOutput
        (state: ^State, ?capture: OutputCapture): ^State
        = StageMap.mapStage (fun ctx -> { ctx with Output = ValueSome(StageOutput.Captured(defaultArg capture (OutputCapture.create()))) }) state
    /// <summary>Hands each line of this stage's step output to write as it arrives.</summary>
    /// <remarks>Called from the reader threads of both streams, so write must tolerate that.</remarks>
    [<CustomOperation>]
    member inline _.redirectOutput (state: ^State, [<InlineIfLambda>] write: StdStream -> string -> unit): ^State =
        StageMap.mapStage (fun ctx -> { ctx with Output = ValueSome(StageOutput.Redirect write) }) state
    /// <summary>Randomizes the execution order of steps in this stage.</summary>
    /// <remarks>By default, steps execute in the order they are declared. Enable this to shuffle the order randomly at each run.</remarks>
    [<CustomOperation>]
    member inline _.shuffleExecuteSequence (state: ^State, ?flag: bool): ^State =
        StageMap.mapStage (fun ctx -> { ctx with ShuffleExecuteSequence = defaultArg flag true }) state
    /// <summary>Adds a step that prints a message derived from the stage context.</summary>
    /// <remarks>The message is prefixed with the step number unless <c>noPrefixForStep</c> is enabled.</remarks>
    [<CustomOperation>]
    member inline _.echo(state: ^State, msg: StageContext -> string): ^State =
        StageMap.mapStage (fun ctx ->
        { ctx with
              Steps = ctx.Steps @ [ Step.StepFn(ValueNone, fun ctx i -> async {
                  if StageContext.getNoPrefixForStep ctx
                  then StageContext.writeLine ctx StdStream.Out $"%s{msg ctx}"
                  else StageContext.writeLine ctx StdStream.Out $"%s{StageContext.buildStepPrefix ctx i}: %s{msg ctx}"
                  return Ok()
              }) ] }) state
    [<CustomOperation>] member inline _.
        verbosity
        (state: ^State, verbosity: Verbosity): ^State
        = StageMap.mapStage (fun ctx -> { ctx with Verbosity = ValueSome verbosity }) state
    [<CustomOperation>] member inline _.
        verbose
        (state: ^State): ^State
        = StageMap.mapStage (fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Verbose }) state
    [<CustomOperation>] member inline _.
        quiet
        (state: ^State): ^State
        = StageMap.mapStage (fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Quiet }) state

    /// <summary>Adds a step that prints a message.</summary>
    /// <remarks>The message is prefixed with the step number unless <c>noPrefixForStep</c> is enabled.</remarks>
    [<CustomOperation>] member inline
        this.echo
        (state: ^State, msg: string): ^State
        = this.echo(state, fun _ -> msg)

