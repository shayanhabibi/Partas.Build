namespace Partas.Build.Internal

open System
open System.ComponentModel
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
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

/// <summary>The signature every step a stage runs is reduced to.</summary>
type StepFnSignature = StageContext -> StepIndex -> Async<Result<unit, string>>

/// <summary>
/// SRTP management for accepted <c>run</c> signatures which are transformed into
/// StepFnSignatures.
/// </summary>
[<EditorBrowsable(EditorBrowsableState.Never)>]
type SRTPStageBuilderRunner =
    static member inline unifyResult(step: Async<unit>): StepFnSignature = fun _ _ -> step |> Async.map Ok
    static member inline unifyResult(step: Async<int>): StepFnSignature = fun ctx _ -> step |> Async.map (StageContext.mapExitCodeToResult ctx)
    static member inline unifyResult(step: StageContext -> unit): StepFnSignature = fun ctx _ -> step ctx |> Ok |> Async.singleton
    static member inline unifyResult(step: StageContext -> int): StepFnSignature = fun ctx _ -> step ctx |> StageContext.mapExitCodeToResult ctx |> Async.singleton
    static member inline unifyResult(step: StageContext -> Async<unit>): StepFnSignature = fun ctx _ -> step ctx |> Async.map Ok
    static member inline unifyResult(step: StageContext -> Async<int>): StepFnSignature = fun ctx _ -> step ctx |> Async.map (StageContext.mapExitCodeToResult ctx)
    static member inline unifyResult(step: StageContext -> Async<Result<unit, string>>): StepFnSignature = fun ctx _ -> step ctx
    static member inline unifyResult(step: StageContext -> Task<Result<unit, string>>): StepFnSignature = fun ctx _ -> step ctx |> Async.AwaitTask
    static member inline unifyResult(step: StageContext -> Result<unit, string>): StepFnSignature = fun ctx _ -> step ctx |> Async.singleton
    static member inline unifyResult(step: StageContext -> Task): StepFnSignature = fun ctx _ -> step ctx |> Task.ofUnit |> Task.map Ok |> Async.AwaitTask
    static member inline unifyResult(step: StageContext -> Task<unit>): StepFnSignature = fun ctx _ -> step ctx |> Task.map Ok |> Async.AwaitTask
    static member inline unifyResult(step: StageContext -> Task<int>): StepFnSignature = fun ctx _ -> step ctx |> Task.map (StageContext.mapExitCodeToResult ctx) |> Async.AwaitTask

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


    /// <summary>Adds a step that runs <paramref name="exe"/> with <paramref name="args"/>.</summary>
    /// <remarks><paramref name="exe"/> is taken as given; <paramref name="args"/> is split on whitespace, honouring quotes.</remarks>
    /// <param name="state">The stage to add the step to.</param>
    /// <param name="exe">The executable to run.</param>
    /// <param name="args">The arguments to pass to the executable.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the step.</param>
    [<CustomOperation>]
    member inline _.run(state: ^State, exe: string, args: string, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let command = Cmd.create exe args
            let step = CmdRunner.step (fun _ -> Async.singleton command) cancellationToken
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueSome(Cmd.toLogString command), step) ] }) state

    /// <summary>Adds a step that runs a whole command line.</summary>
    /// <remarks>
    /// The line is split on whitespace, honouring <c>"</c> and <c>'</c>: convenient, but lossy for anything with
    /// awkward quoting. An interpolated string binds to this overload too, so its holes are flattened into the line
    /// and split with it. Wrap it in <c>cmd</c> — <c>run (cmd $"dotnet build {project}")</c> — and each hole becomes
    /// exactly one argument, whatever it contains.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let project = "src/My Lib/MyLib.fsproj"
    ///
    /// stage "build" {
    ///     run "dotnet --info"                    // dotnet, --info
    ///     run $"dotnet build {project}"          // dotnet, build, src/My, Lib/MyLib.fsproj
    ///     run (cmd $"dotnet build {project}")    // dotnet, build, src/My Lib/MyLib.fsproj
    /// }
    /// </code>
    /// </example>
    [<CustomOperation>]
    member inline _.run(state: ^State, command: string, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let command = Cmd.ofString command
            let step = CmdRunner.step (fun _ -> Async.singleton command) cancellationToken
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueSome(Cmd.toLogString command), step) ] }) state

    /// <summary>Adds a step that runs a prepared command.</summary>
    /// <remarks>Pair with <c>cmd</c> to keep interpolation holes intact: <c>run (cmd $"dotnet build {project}")</c>.</remarks>
    /// <example>
    /// A flag added only on CI, without duplicating the command line:
    /// <code lang="fsharp">
    /// stage "test" {
    ///     run (
    ///         cmd $"dotnet test -c {configuration}"
    ///         |> Cmd.argIf ci [ "--logger"; "trx" ]
    ///     )
    /// }
    /// </code>
    /// </example>
    [<CustomOperation>]
    member inline _.run(state: ^State, command: Cmd, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let step = CmdRunner.step (fun _ -> Async.singleton command) cancellationToken
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueSome(Cmd.toLogString command), step) ] }) state

    /// <summary>Adds a step that runs an interpolated command line without printing what the holes contained.</summary>
    /// <remarks>
    /// Each hole is one argument and each hole is masked, so escaping and masking come from the same mechanism:
    /// <c>runSensitive $"docker login -u {user} -p {password}"</c> passes the password through untouched and logs
    /// it as <c>***</c>.
    /// <para>
    /// The single generic member is what keeps the <c>string</c> -> <c>FormattableString</c> conversion available:
    /// F# applies it only while one overload is in play, so a mirrored pair would reject <c>runSensitive $"..."</c>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// stage "push" {
    ///     // Runs with the key; the log, --explain and the JSON run result show "--api-key ***".
    ///     runSensitive $"dotnet nuget push bin/*.nupkg --api-key {apiKey}"
    /// }
    /// </code>
    /// </example>
    [<CustomOperation>]
    member inline _.runSensitive(state: ^State, command: FormattableString, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let command = Cmd.ofFormattable true command
            let step = CmdRunner.step (fun _ -> Async.singleton command) cancellationToken
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueSome(Cmd.toLogString command), step) ] }) state

    /// <summary>Adds a step that runs a command line derived from the stage context.</summary>
    /// <remarks>The command line string is split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<Obsolete("A string returned from the function runs as a command line. Use `runLine` to run a derived command line, `run (fun ctx -> cmd $\"...\")` to run a prepared command, or `echo` to print a message.")>]
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> string, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = Cmd.ofString (step ctx) |> Async.singleton
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<Obsolete("A string returned from the function runs as a command line. Use `runLine` to run a derived command line, `run (fun ctx -> cmd $\"...\")` to run a prepared command, or `echo` to print a message.")>]
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> Async<string>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Async.map Cmd.ofString
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs a command line derived from the stage context by a task.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<Obsolete("A string returned from the function runs as a command line. Use `runLine` to run a derived command line, `run (fun ctx -> cmd $\"...\")` to run a prepared command, or `echo` to print a message.")>]
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> Task<string>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Task.map Cmd.ofString |> Async.AwaitTask
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    /// <example>
    /// <code lang="fsharp">
    /// stage "pack" {
    ///     run (fun ctx -> cmd $"dotnet pack -o {ctx.Name}-out")
    /// }
    /// </code>
    /// </example>
    [<CustomOperation>]
    member inline _.run(state: ^State, buildCmd: StageContext -> Cmd, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step (buildCmd >> Async.singleton) cancellationToken) ] }) state

    /// <summary>Adds a step that runs a command line derived from the stage context, or no step at all.</summary>
    /// <remarks>The command line string is split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<Obsolete("A string returned from the function runs as a command line. Use `runLine` to run a derived command line, `run (fun ctx -> cmd $\"...\")` to run a prepared command, or `echo` to print a message.")>]
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> string option, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Option.map Cmd.ofString |> Async.singleton
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context, or no step at all.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<Obsolete("A string returned from the function runs as a command line. Use `runLine` to run a derived command line, `run (fun ctx -> cmd $\"...\")` to run a prepared command, or `echo` to print a message.")>]
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> Async<string option>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Async.map (Option.map Cmd.ofString)
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs a command line derived from the stage context by a task, or no step at all.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<Obsolete("A string returned from the function runs as a command line. Use `runLine` to run a derived command line, `run (fun ctx -> cmd $\"...\")` to run a prepared command, or `echo` to print a message.")>]
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> Task<string option>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Task.map (Option.map Cmd.ofString) |> Async.AwaitTask
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs a prepared command asynchronously derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> Async<Cmd>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step step cancellationToken) ] }) state

    /// <summary>Adds a step that runs a prepared command asynchronously derived from the stage context, or no step at all.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> Async<Cmd option>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption step cancellationToken) ] }) state

    /// <summary>Adds a step that runs a prepared command asynchronously derived from the stage context, and fails with what the derivation reports.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> Async<Result<Cmd, string>>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepResult step cancellationToken) ] }) state

    /// <summary>Adds a step that runs a prepared command asynchronously derived from the stage context, or no step at all, and fails with what the derivation reports.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.run(state: ^State, step: StageContext -> Async<Result<Cmd option, string>>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepResultOption step cancellationToken) ] }) state

    /// <summary>Adds a step that runs a prepared command derived from the stage context, or no step at all.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.run(state: ^State, buildCmd: StageContext -> Cmd option, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption (buildCmd >> Async.singleton) cancellationToken) ] }) state

    /// <summary>Adds a step that runs a prepared command derived from the stage context, and fails with what the derivation reports.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.run(state: ^State, buildCmd: StageContext -> Result<Cmd, string>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepResult (buildCmd >> Async.singleton) cancellationToken) ] }) state

    /// <summary>Adds a step that runs a prepared command derived from the stage context, or no step at all, and fails with what the derivation reports.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.run(state: ^State, buildCmd: StageContext -> Result<Cmd option, string>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepResultOption (buildCmd >> Async.singleton) cancellationToken) ] }) state

    // =================================================================
    //   runLine: a command line derived from the stage context
    // =================================================================

    /// <summary>Adds a step that runs the command line derived from the stage context.</summary>
    /// <remarks>The command line string is split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    /// <example>
    /// The string runs as a command; to print a message, use <c>echo</c>.
    /// <code lang="fsharp">
    /// stage "restore" {
    ///     runLine (fun ctx -> $"dotnet restore {ctx.Name}.slnx")
    /// }
    /// </code>
    /// </example>
    [<CustomOperation>]
    member inline _.runLine(state: ^State, step: StageContext -> string, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = Cmd.ofString (step ctx) |> Async.singleton
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs the command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.runLine(state: ^State, step: StageContext -> Async<string>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Async.map Cmd.ofString
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs the command line derived from the stage context by a task.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.runLine(state: ^State, step: StageContext -> Task<string>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Task.map Cmd.ofString |> Async.AwaitTask
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs the command line derived from the stage context, or no step at all.</summary>
    /// <remarks>The command line string is split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.runLine(state: ^State, step: StageContext -> string option, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Option.map Cmd.ofString |> Async.singleton
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs the command line asynchronously derived from the stage context, or no step at all.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.runLine(state: ^State, step: StageContext -> Async<string option>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Async.map (Option.map Cmd.ofString)
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption buildCmd cancellationToken) ] }) state

    /// <summary>Adds a step that runs the command line derived from the stage context by a task, or no step at all.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>]
    member inline _.runLine(state: ^State, step: StageContext -> Task<string option>, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let buildCmd ctx = step ctx |> Task.map (Option.map Cmd.ofString) |> Async.AwaitTask
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption buildCmd cancellationToken) ] }) state
    /// <summary>Adds a step with flexible signature support.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/run/*"/>
    /// <example>
    /// <code lang="fsharp">
    /// stage "check" {
    ///     run (fun ctx -> if System.IO.File.Exists "global.json" then 0 else 1)
    ///     run (fun ctx -> async { do! Async.Sleep 100 })
    /// }
    /// </code>
    /// </example>
    [<CustomOperation>]
    member inline _.run(state: ^State, step): ^State =
        StageMap.mapStage (fun ctx ->
            let stepFn = ((^T or SRTPStageBuilderRunner):(static member unifyResult: ^T -> StepFnSignature) step)
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, stepFn) ] }) state

    /// <summary>Adds a step running deferred work.</summary>
    /// <remarks>
    /// The operation runs under the stage's working directory, environment, acceptable exit codes and output
    /// routing, and reports a structured failure the runner renders at the print site.
    /// <c>label</c> is what <c>--explain</c> shows for the step; without one it shows the step's index.
    /// </remarks>
    [<CustomOperation>]
    member inline _.runOperation(state: ^State, operation: Operation<unit>, ?label: string): ^State =
        StageMap.mapStage (fun ctx ->
            { ctx with Steps = ctx.Steps @ [ Step.Operation(ValueOption.ofOption label, Operation.toStepOutcome operation) ] }) state

    /// <summary>Adds a step that polls an HTTP endpoint for health.</summary>
    /// <remarks>The step repeatedly polls the given URL until it succeeds or the stage is cancelled. Useful for waiting for services to become available.</remarks>
    [<CustomOperation>]
    member inline _.runHttpHealthCheck(state: ^State, url: string, ?configRequest, ?cancellationToken: CancellationToken): ^State =
        StageMap.mapStage (fun ctx ->
            let configRequest = defaultArg configRequest ignore
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let poll ctx _ = StageContext.runHttpHealthCheckCancelableWithConfigRequest ctx cancellationToken configRequest url
            { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, poll) ] }) state
