[<AutoOpen>]
module Partas.Build.StageBuilder

open System
open System.ComponentModel
open System.Threading
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Partas.Build.Internal
open Partas.Build
open FSharp.Data.UnitSystems.SI

[<Measure>] type second = UnitNames.second
[<Measure>] type s = UnitSymbols.s

type StepFnSignature = StageContext -> StepIndex -> Async<Result<unit, string>>

[<EditorBrowsable(EditorBrowsableState.Never)>]
type SRTPStageBuilderRunner =
    static member inline unifyResult(step: Async<unit>): StepFnSignature = fun _ _ -> Async.map Ok step
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

and [<EditorBrowsable(EditorBrowsableState.Advanced)>]
    StageBuilder(name: string) =
    [<EditorBrowsable(EditorBrowsableState.Never)>] // `Run` is deliberately not `inline`: `BuildStage` is a plain function type rather than a delegate,
    // and inlining an application of one defeats the optimiser (`FS1118`) in Release builds only.
    // It applies a closure once at construction time, so there is nothing to gain by inlining it.
    member _.Run(build: BuildStage): StageContext = build <| StageContext.create name
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Yield (_: unit): BuildStage = id
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Zero(): BuildStage = id
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Yield(stage: StageContext): StageContext = stage
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildStage): BuildStage = fn()
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Delay([<InlineIfLambda>] fn: unit -> StageContext): BuildStage = fun ctx ->
        { ctx with Steps = ctx.Steps @ [ Step.StepOfStage(fn ()) ] }
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Combine(stage: StageContext, [<InlineIfLambda>] build: BuildStage): BuildStage = fun ctx ->
        build { ctx with Steps = ctx.Steps @ [ Step.StepOfStage stage ]  }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.For([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] fn: unit -> BuildStage) : BuildStage = build >> fn ()

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.For([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] fn: unit -> StageContext): BuildStage = build >> fun ctx ->
        { ctx with Steps = ctx.Steps @ [ Step.StepOfStage <| fn() ] }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Yield([<InlineIfLambda>] condition: BuildStageIsActive): BuildStageIsActive = condition

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildStageIsActive): BuildStage = fun ctx ->
        { ctx with IsActive = fn () }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Yield([<InlineIfLambda>] builder: BuildStep): BuildStep = builder

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildStep): BuildStage = fun ctx ->
        { ctx with Steps = ctx.Steps @ [ Step.StepFn <| fn() ] }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Combine([<InlineIfLambda>] builder: BuildStep, [<InlineIfLambda>] build: BuildStage): BuildStage = fun ctx ->
        build { ctx with Steps = ctx.Steps @ [ Step.StepFn builder ] }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.For([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] fn: unit -> BuildStep): BuildStage = build >> fun ctx ->
        { ctx with Steps = ctx.Steps @ [ Step.StepFn <| fn() ] }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Combine([<InlineIfLambda>] build1: BuildStage, [<InlineIfLambda>] build2: BuildStage): BuildStage = build1 >> build2

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.For<'T>(items: 'T seq, [<InlineIfLambda>] fn: 'T -> StageContext): BuildStage = fun ctx ->
        { ctx with Steps = items |> Seq.map (fn >> Step.StepOfStage) |> Seq.append ctx.Steps |> Seq.toList }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.YieldFrom(stages: StageContext seq): BuildStage = fun ctx ->
        { ctx with Steps = stages |> Seq.map Step.StepOfStage |> Seq.append ctx.Steps |> Seq.toList }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Combine([<InlineIfLambda>] condition: BuildStageIsActive, [<InlineIfLambda>] build: BuildStage): BuildStage =
        StageContext.buildStageIsActive build condition

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.For([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] fn: unit -> BuildStageIsActive): BuildStage =
        StageContext.buildStageIsActive build (fn ())

    /// <summary>Adds environment variables to the stage.</summary>
    /// <remarks>Variables set here override inherited values from parent contexts. A stage-level variable shadows any pipeline-level variable with the same name.</remarks>
    [<CustomOperation>] member inline _.
        envVars
        ([<InlineIfLambda>] build: BuildStage, kvs: seq<string * string>): BuildStage
        = build >> fun ctx -> { ctx with EnvVars = kvs |> Seq.fold (fun state (k, v) -> Map.add k v state) ctx.EnvVars }
    /// <summary>Sets exit codes that are treated as successful.</summary>
    /// <remarks>By default, only exit code 0 is acceptable. Setting this replaces (rather than appends to) the default acceptable codes. A stage-level setting overrides the pipeline's.</remarks>
    [<CustomOperation>] member inline _.
        acceptExitCodes
        ([<InlineIfLambda>] build: BuildStage, codes: int seq): BuildStage
        = build >> fun ctx -> { ctx with AcceptableExitCodes = set codes }
    /// <summary>Fails the pipeline if this stage is inactive.</summary>
    /// <remarks>By default, inactive stages are skipped without failure. Enable this to treat an inactive stage as a pipeline error.</remarks>
    [<CustomOperation>] member inline _.
        failIfIgnored
        ([<InlineIfLambda>] build: BuildStage, ?flag: bool): BuildStage
        = build >> fun ctx -> { ctx with FailIfIgnored = defaultArg flag true }
    /// <summary>Fails the pipeline if no substages of this stage are active.</summary>
    /// <remarks>By default, stages with no active substages are skipped silently. Enable this to require at least one active substage.</remarks>
    [<CustomOperation>] member inline _.
        failIfNoActiveSubStage
        ([<InlineIfLambda>] build: BuildStage, ?flag: bool): BuildStage
        = build >> fun ctx -> { ctx with FailIfNoActiveSubStage = defaultArg flag true }
    /// <summary>Continues executing remaining steps even if a step fails.</summary>
    /// <remarks>By default, a step failure stops execution of subsequent steps. Enable this to run all steps regardless of earlier failures.</remarks>
    [<CustomOperation>] member inline _.
        continueStepsOnFailure
        ([<InlineIfLambda>] build: BuildStage, ?flag): BuildStage
        = build >> fun ctx -> { ctx with ContinueStepsOnFailure = defaultArg flag true }
    /// <summary>Continues pipeline execution even if this stage fails.</summary>
    /// <remarks>By default, a stage failure stops the entire pipeline. Enable this to allow post-stages and subsequent stages to run regardless of this stage's failure.</remarks>
    [<CustomOperation>] member inline _.
        continueStageOnFailure
        ([<InlineIfLambda>] build: BuildStage, ?flag): BuildStage
        = build >> fun ctx -> { ctx with ContinueStageOnFailure = defaultArg flag true }
    /// <summary>Continues execution after a step failure and continues the pipeline after a stage failure.</summary>
    /// <remarks>This is a convenience operation equivalent to enabling both <c>continueStepsOnFailure</c> and <c>continueStageOnFailure</c>.</remarks>
    [<CustomOperation>] member inline _.
        continueOnStepFailure
        ([<InlineIfLambda>] build: BuildStage, ?flag): BuildStage
        = build >> fun ctx ->
            let shouldCont = defaultArg flag true
            { ctx with ContinueStepsOnFailure = shouldCont; ContinueStageOnFailure = shouldCont }
    /// <summary>Sets the overall timeout for the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeout/*"/>
    [<CustomOperation>] member inline _.
        timeout
        ([<InlineIfLambda>] build: BuildStage, seconds: int<second>): BuildStage
        = build >> fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds(float seconds)) }
    /// <summary>Sets the overall timeout for the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeout/*"/>
    [<CustomOperation>] member inline _.
        timeout
        ([<InlineIfLambda>] build: BuildStage, seconds: float): BuildStage
        = build >> fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds(seconds)) }
    /// <summary>Sets the overall timeout for the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeout/*"/>
    [<CustomOperation>] member inline _.
        timeout
        ([<InlineIfLambda>] build: BuildStage, timespan: TimeSpan): BuildStage
        = build >> fun ctx -> { ctx with Timeout = ValueSome timespan }
    /// <summary>Sets the timeout for each step in the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeoutForStep/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildStage, seconds: int<second>): BuildStage
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(float seconds)) }
    /// <summary>Sets the timeout for each step in the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeoutForStep/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildStage, seconds: float): BuildStage
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(seconds)) }
    /// <summary>Sets the timeout for each step in the stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/timeoutForStep/*"/>
    [<CustomOperation>] member inline _.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildStage, timeSpan: TimeSpan): BuildStage
        = build >> fun ctx -> { ctx with TimeoutForStep = ValueSome timeSpan }
    /// <summary>Enables or disables parallel execution of steps in this stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>] member inline _.
        parallel'
        ([<InlineIfLambda>] build: BuildStage, ?flag: bool): BuildStage
        = build >> fun ctx -> { ctx with IsParallel = fun _ -> if defaultArg flag true then ValueSome -1 else ValueNone }
    /// <summary>Enables parallel execution of steps in this stage throttled to the given number of processes.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>] member inline _.
        parallel'
        ([<InlineIfLambda>] build: BuildStage, throttle: int): BuildStage
        = build >> fun ctx -> { ctx with IsParallel = fun _ -> ValueSome throttle }
    /// <summary>Sets a condition for parallel execution of steps in this stage. Can either return a boolean switch, or the throttle count.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>] member inline _.
        parallel'
        ([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] condition: StageContext -> int voption): BuildStage
        = build >> fun ctx -> { ctx with IsParallel = condition }
    /// <summary>Sets a condition for parallel execution of steps in this stage. Can either return a boolean switch, or the throttle count.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>] member inline _.
        parallel'
        ([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] condition: StageContext -> Choice<bool, int>): BuildStage
        = build >> fun ctx -> { ctx with IsParallel = condition >> function Choice1Of2 b -> (if b then ValueSome -1 else ValueNone) | Choice2Of2 i -> ValueSome i }
    /// <summary>Sets a condition for parallel execution of steps in this stage. Can either return a boolean switch, or the throttle count.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>] member inline _.
        parallel'
        ([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] condition: StageContext -> Choice<int, bool>): BuildStage
        = build >> fun ctx -> { ctx with IsParallel = condition >> function Choice1Of2 i -> ValueSome i | Choice2Of2 true -> ValueSome -1 | _ -> ValueNone }
    /// <summary>Sets a condition for parallel execution of steps in this stage. Can either return a boolean switch, or the throttle count.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/parallel/*"/>
    [<CustomOperation>] member inline _.
        parallel'
        ([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] condition: StageContext -> bool): BuildStage
        = build >> fun ctx -> { ctx with IsParallel = condition >> function true -> ValueSome -1 | false -> ValueNone }

    /// <summary>Sets the working directory for this stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/workingDir/*"/>
    [<CustomOperation>] member inline _.
        workingDir
        ([<InlineIfLambda>] build: BuildStage, path: string): BuildStage
        = build >> fun ctx -> { ctx with WorkingDir = ValueSome path }

    /// <summary>Sets the working directory for this stage.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/workingDir/*"/>
    [<CustomOperation>] member inline _.
        workingDir
        ([<InlineIfLambda>] build: BuildStage, path: IO.DirectoryInfo): BuildStage
        = build >> fun ctx -> { ctx with WorkingDir = ValueSome path.FullName }

    /// <summary>Suppresses the step number prefix in step output.</summary>
    /// <remarks>By default, each step's output is prefixed with its stage and step number. Enable this to output without the prefix.</remarks>
    [<CustomOperation>] member inline _.
        noPrefixForStep
        ([<InlineIfLambda>] build: BuildStage, ?flag: bool): BuildStage
        = build >> fun ctx -> { ctx with NoPrefixForStep = defaultArg flag true }

    /// <summary>Disables stdout and stderr redirection for steps in this stage.</summary>
    /// <remarks>By default, step output is captured and logged. Enable this to let steps write directly to the console without redirection.</remarks>
    [<CustomOperation>] member inline _.
        noStdRedirectForStep
        ([<InlineIfLambda>] build: BuildStage, ?flag: bool): BuildStage
        = build >> fun ctx -> { ctx with NoStdRedirectForStep = defaultArg flag true }

    /// <summary>Randomizes the execution order of steps in this stage.</summary>
    /// <remarks>By default, steps execute in the order they are declared. Enable this to shuffle the order randomly at each run.</remarks>
    [<CustomOperation>] member inline _.
        shuffleExecuteSequence
        ([<InlineIfLambda>] build: BuildStage, ?flag: bool): BuildStage
        = build >> fun ctx -> { ctx with ShuffleExecuteSequence = defaultArg flag true }

    /// <summary>Adds a step built from a context-dependent function.</summary>
    /// <remarks>The function receives the current stage context and returns a step function that operates on that context.</remarks>
    [<CustomOperation>] member _.
        run
        (build: BuildStage, buildStep: StageContext -> BuildStep): BuildStage
        = build >> fun ctx ->
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(fun ctx i -> async { return! buildStep ctx ctx i }) ] }

    /// <summary>Adds a step that runs <paramref name="exe"/> with <paramref name="args"/>.</summary>
    /// <remarks><paramref name="exe"/> is taken as given; <paramref name="args"/> is split on whitespace, honouring quotes.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, exe: string, args: string, ?cancellationToken: CancellationToken): BuildStage
        = this.run (build, (fun _ -> Cmd.create exe args), ?cancellationToken = cancellationToken)

    /// <summary>Adds a step that runs a whole command line.</summary>
    /// <remarks>
    /// The line is split on whitespace, honouring <c>"</c> and <c>'</c>: convenient, but lossy for anything with
    /// awkward quoting. Interpolate instead — <c>run $"dotnet build {project}"</c> — and each hole becomes exactly
    /// one argument, whatever it contains.
    /// </remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, command: string, ?cancellationToken: CancellationToken): BuildStage
        = this.run (build, (fun _ -> Cmd.ofString command), ?cancellationToken = cancellationToken)

    /// <summary>Adds a step that runs a prepared command.</summary>
    /// <remarks>Pair with <c>cmd</c> to keep interpolation holes intact: <c>run (cmd $"dotnet build {project}")</c>.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, command: Cmd, ?cancellationToken: CancellationToken): BuildStage
        = this.run (build, (fun _ -> command), ?cancellationToken = cancellationToken)

    /// <summary>Adds a step that runs a command line derived from the stage context.</summary>
    /// <remarks>The command line string is split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> string, ?cancellationToken: CancellationToken): BuildStage
        = this.run (build, (fun ctx -> Cmd.ofString (step ctx)), ?cancellationToken = cancellationToken)

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> Async<string>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        let buildCmd ctx = step ctx |> Async.map Cmd.ofString
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(CmdRunner.step buildCmd cancellationToken) ] }

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member _.
        run
        (build: BuildStage, buildCmd: StageContext -> Cmd, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(CmdRunner.step (buildCmd >> Async.singleton) cancellationToken) ] }

    /// <summary>Adds a step that runs an interpolated command line without printing what the holes contained.</summary>
    /// <remarks>
    /// Each hole is one argument and each hole is masked, so escaping and masking come from the same mechanism:
    /// <c>runSensitive $"docker login -u {user} -p {password}"</c> passes the password through untouched and logs
    /// it as <c>***</c>.
    /// </remarks>
    [<CustomOperation>] member this.
        runSensitive
        (build: BuildStage, command: FormattableString, ?cancellationToken: CancellationToken): BuildStage
        = this.run (build, (fun _ -> Cmd.ofFormattable true command), ?cancellationToken = cancellationToken)
    /// <summary>Adds a step with flexible signature support.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/run/*"/>
    [<CustomOperation>] member inline _.
        run
        ([<InlineIfLambda>] build: BuildStage, step): BuildStage
        = build >> fun ctx -> {
            ctx with Steps = ctx.Steps @ [ Step.StepFn((^T or SRTPStageBuilderRunner):(static member unifyResult: ^T -> StepFnSignature) step) ]
        }
    /// <summary>Adds a step that polls an HTTP endpoint for health.</summary>
    /// <remarks>The step repeatedly polls the given URL until it succeeds or the stage is cancelled. Useful for waiting for services to become available.</remarks>
    [<CustomOperation>] member _.
        runHttpHealthCheck
        (build: BuildStage, url: string, ?configRequest, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let configRequest = defaultArg configRequest ignore
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(fun ctx _ -> StageContext.runHttpHealthCheckCancelableWithConfigRequest ctx cancellationToken configRequest url) ] }
    /// <summary>Adds a step that prints a message derived from the stage context.</summary>
    /// <remarks>The message is prefixed with the step number unless <c>noPrefixForStep</c> is enabled.</remarks>
    [<CustomOperation>] member inline _.
        echo
        ([<InlineIfLambda>] build: BuildStage, msg: StageContext -> string): BuildStage
        = build >> fun ctx ->
        { ctx with
              Steps = ctx.Steps @ [ Step.StepFn(fun ctx i -> async {
                  if StageContext.getNoPrefixForStep ctx
                  then printfn $"%s{msg ctx}"
                  else printfn $"%s{StageContext.buildStepPrefix ctx i}: %s{msg ctx}"
                  return Ok()
              }) ] }

    [<CustomOperation>] member inline _.
        verbosity
        ([<InlineIfLambda>] build: BuildStage, verbosity: Verbosity): BuildStage
        = build >> fun ctx -> { ctx with Verbosity = ValueSome verbosity }
    [<CustomOperation>] member inline _.
        verbose
        ([<InlineIfLambda>] build: BuildStage): BuildStage
        = build >> fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Verbose }
    [<CustomOperation>] member inline _.
        quiet
        ([<InlineIfLambda>] build: BuildStage): BuildStage
        = build >> fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Quiet }


    /// <summary>Adds a step that prints a message.</summary>
    /// <remarks>The message is prefixed with the step number unless <c>noPrefixForStep</c> is enabled.</remarks>
    [<CustomOperation>] member inline
        this.echo
        ([<InlineIfLambda>] build: BuildStage, msg: string): BuildStage
        = this.echo(build, fun _ -> msg)

let inline stage name = StageBuilder(name)
let inline step x: BuildStep = x
