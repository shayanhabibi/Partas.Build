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

type private IILAttribute = InlineIfLambdaAttribute
type private EBAttribute = EditorBrowsableAttribute
[<Literal>]
let private never = EditorBrowsableState.Never
[<Literal>]
let private advanced = EditorBrowsableState.Advanced


[<EB(never)>]
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

and [<EB(advanced)>]
    StageBuilder(name: string) =
    [<EB(never)>] // `Run` is deliberately not `inline`: `BuildStage` is a plain function type rather than a delegate,
    // and inlining an application of one defeats the optimiser (`FS1118`) in Release builds only.
    // It applies a closure once at construction time, so there is nothing to gain by inlining it.
    member _.Run(build: BuildStage): StageContext = build <| StageContext.create name
    // The `InputSpec` mirror of the above: a nested stage that declares a CLI input turns the whole stage into
    // an `InputSpec<StageContext>`, which is what `pipeline` and `command` already know how to yield.
    [<EB(never)>]
    member _.Run(spec: InputSpec<BuildStage>): InputSpec<StageContext> =
        InputSpec.map (fun (build: BuildStage) -> build <| StageContext.create name) spec
    // =================================================================
    //                              Yield
    // =================================================================

    [<EB(never)>]
    member inline _.Yield (_: unit): BuildStage = id
    [<EB(never)>]
    member inline _.Yield stage = StageContext.addSubStage stage
    [<EB(never)>]
    member inline _.Yield ([<IIL>] builder): BuildStep = builder
    [<EB(never)>]
    member inline _.Yield ([<IIL>] condition): BuildStageIsActive = condition
    [<EB(never)>]
    member inline _.Yield stages = StageContext.addSubStages stages
    [<EB(never)>]
    member inline _.Yield spec = InputSpec.map StageContext.addSubStage spec
    [<EB(never)>]
    member inline _.Yield spec = InputSpec.map StageContext.addSubStages spec
    /// A list of ready-made blocks - `[ Blocks.restore; Blocks.build ]` - rather than one block yielding many stages.
    [<EB(never)>]
    member inline _.Yield specs = InputSpec.map StageContext.addSubStages (InputSpec.sequence specs)
    [<EB(never)>]
    member inline _.Yield spec = InputSpec.map StageContext.addStepFn spec
    // =================================================================
    //                              Zero
    // =================================================================
    [<EB(never)>]
    member inline _.Zero(): BuildStage = id
    // =================================================================
    //                              Delay
    // =================================================================
    [<EB(never)>]
    member inline _.Delay ([<IIL>] fn): BuildStage = fn()
    [<EB(never)>]
    member inline _.Delay ([<IIL>] fn) = StageContext.addSubStage (fn())
    [<EB(never)>]
    member inline _.Delay ([<IIL>] fn) = StageContext.addStepFn (fn())
    [<EB(never)>]
    member inline _.Delay ([<IIL>] fn) = StageContext.addPredicate (fn())
    [<EB(never)>]
    member inline _.Delay ([<IIL>] fn): InputSpec<BuildStage> = fn ()
    [<EB(never)>]
    member inline _.Delay ([<IIL>] fn) = InputSpec.map StageContext.addSubStage (fn())
    // =================================================================
    //                              Combine
    // =================================================================
    [<EB(never)>]
    member inline _.Combine ([<IIL>] builder, [<IIL>] build): BuildStage = StageContext.addStepFn builder >> build
    [<EB(never)>]
    member inline _.Combine (stage: StageContext, [<IIL>] build: BuildStage): BuildStage = StageContext.addSubStage stage >> build
    [<EB(never)>]
    member inline _.Combine ([<IIL>] condition, [<IIL>] build) = StageContext.buildStageIsActive build condition
    [<EB(never)>]
    member inline _.Combine ([<IIL>] build1, [<IIL>] build2) = BuildStage.merge build1 build2
    [<EB(never)>]
    member inline _.Combine ([<IIL>] build, spec) = InputSpec.map (BuildStage.merge build) spec
    [<EB(never)>]
    member inline _.Combine (spec, rest): InputSpec<BuildStage> = InputSpec.map2 (>>) spec rest
    [<EB(never)>]
    member inline _.Combine (spec, [<IIL>] rest: BuildStage): InputSpec<BuildStage> = InputSpec.map (fun build -> build >> rest) spec
    [<EB(never)>]
    member inline _.Combine (spec, [<IIL>] build: BuildStage): InputSpec<BuildStage> = InputSpec.map (fun stage -> StageContext.addSubStage stage >> build) spec
    [<EB(never)>]
    member inline _.Combine (spec, rest): InputSpec<BuildStage> = InputSpec.map2 (fun stage ->  (>>) (StageContext.addSubStage stage)) spec rest
    [<EB(never)>]
    member inline _.Combine (stage, spec): InputSpec<BuildStage> = InputSpec.map ((>>) (StageContext.addSubStage stage)) spec
    [<EB(never)>]
    member inline _.Combine ([<IIL>] builder, spec): InputSpec<BuildStage> = InputSpec.map ((>>) (StageContext.addStepFn builder)) spec
    [<EB(never)>]
    member inline _.Combine ([<IIL>] condition, spec): InputSpec<BuildStage> = InputSpec.map (StageContext.buildStageIsActive >> fun build -> build condition) spec
    // =================================================================
    //                              For
    // =================================================================
    [<EB(never)>]
    member inline _.For ([<IIL>] build, [<IIL>] fn: unit -> BuildStage) : BuildStage = build >> fn ()
    [<EB(never)>]
    member inline _.For ([<IIL>] build: BuildStage, [<IIL>] fn: unit -> StageContext): BuildStage = build >> StageContext.addSubStage (fn())
    [<EB(never)>]
    member inline _.For ([<IIL>] build: BuildStage, [<IIL>] fn: unit -> BuildStep): BuildStage = build >> StageContext.addStepFn (fn())
    [<EB(never)>]
    member inline _.For ([<IIL>] build, [<IIL>] fn) = StageContext.buildStageIsActive build (fn ())
    [<EB(never)>]
    member inline _.For<'T> (items: 'T seq, [<IIL>] fn: 'T -> StageContext): BuildStage = StageContext.addSubStages (Seq.map fn items)
    [<EB(never)>]
    member inline _.For<'T> (items: 'T seq, [<IIL>] fn: 'T -> BuildStage): BuildStage = fun ctx -> items |> Seq.fold (fun ctx item -> fn item ctx) ctx
    [<EB(never)>]
    member inline _.For<'T>(items: 'T seq, [<IIL>] fn: 'T -> InputSpec<StageContext>): InputSpec<BuildStage> = InputSpec.map StageContext.addSubStages (InputSpec.traverse fn items)
    [<EB(never)>]
    member inline _.For<'T>(items: 'T seq, [<IIL>] fn: 'T -> InputSpec<BuildStage>): InputSpec<BuildStage> =
        InputSpec.map (fun builds ctx -> builds |> List.fold (fun ctx build -> build ctx) ctx) (InputSpec.traverse fn items)
    [<EB(never)>]
    member inline _.For<'T>(items: 'T seq, [<IIL>]fn: 'T -> BuildStep): BuildStage = fun ctx -> items |> Seq.fold (fun ctx item -> StageContext.addStepFn (fn item) ctx) ctx
    [<EB(never)>]
    member inline _.For ([<IIL>] build, [<IIL>] fn: unit -> InputSpec<BuildStage>): InputSpec<BuildStage> = InputSpec.map (fun rest -> build >> rest) (fn ())
    [<EB(never)>]
    member inline _.For ([<IIL>] build: BuildStage, [<IIL>] fn: unit -> InputSpec<StageContext>): InputSpec<BuildStage> =
        InputSpec.map (fun stage -> build >> StageContext.addSubStage stage) (fn ())
    [<EB(never)>]
    member inline _.For (spec, [<IIL>] fn: unit -> BuildStage): InputSpec<BuildStage> =
        InputSpec.map (fun build -> build >> fn ()) spec
    [<EB(never)>]
    member inline _.For (spec, [<IIL>] fn: unit -> StageContext): InputSpec<BuildStage> =
        InputSpec.map (fun build -> build >> StageContext.addSubStage (fn ())) spec
    [<EB(never)>]
    member inline _.For (spec, [<IIL>] fn: unit -> BuildStep): InputSpec<BuildStage> =
        InputSpec.map (fun build -> build >> StageContext.addStepFn (fn ())) spec
    [<EB(never)>]
    member inline _.For (spec, [<IIL>] fn: unit -> BuildStageIsActive): InputSpec<BuildStage> =
        InputSpec.map (fun build -> StageContext.buildStageIsActive build (fn ())) spec
    [<EB(never)>]
    member inline _.For (spec, [<IIL>] fn: unit -> InputSpec<BuildStage>): InputSpec<BuildStage> =
        InputSpec.map2 (>>) spec (fn ())
    [<EB(never)>]
    member inline _.For(spec: InputSpec<BuildStage>, [<IIL>] fn: unit -> InputSpec<StageContext>): InputSpec<BuildStage> =
        InputSpec.map2 (fun build stage -> build >> StageContext.addSubStage stage) spec (fn ())
    // =================================================================
    //                              YieldFrom
    // =================================================================
    [<EB(never)>]
    member inline _.YieldFrom(stages: StageContext seq): BuildStage = StageContext.addSubStages stages
    [<EB(never)>]
    member inline _.YieldFrom(specs: InputSpec<StageContext> seq): InputSpec<BuildStage> =
        InputSpec.map StageContext.addSubStages (InputSpec.sequence specs)
    [<EB(never)>]
    member inline _.YieldFrom(steps: BuildStep seq): BuildStage = fun ctx ->
        { ctx with Steps = steps |> Seq.map Step.StepFn |> Seq.append ctx.Steps |> Seq.toList }


    // =================================================================
    //                         CustomOperations
    // =================================================================
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
    [<CustomOperation>] member inline _.
        outputTo
        ([<InlineIfLambda>] build: BuildStage, output: StageOutput): BuildStage
        = build >> fun ctx -> { ctx with Output = ValueSome output }

    /// <summary>Drops the output of this stage's steps.</summary>
    /// <remarks>For a step whose noise is never worth reading. A failure still reports its exit code.</remarks>
    [<CustomOperation>] member inline _.
        silentOutput
        ([<InlineIfLambda>] build: BuildStage): BuildStage
        = build >> fun ctx -> { ctx with Output = ValueSome StageOutput.Silent }

    /// <summary>Holds the output of this stage's steps back, and lifts it into the error message if one fails.</summary>
    /// <remarks>
    /// A quiet run that still says why it failed: stderr if the process used it, and everything it wrote
    /// otherwise. Pass an <c>OutputCapture</c> to keep a handle on the lines regardless of the outcome.
    /// </remarks>
    [<CustomOperation>] member inline _.
        captureOutput
        ([<InlineIfLambda>] build: BuildStage, ?capture: OutputCapture): BuildStage
        = build >> fun ctx -> { ctx with Output = ValueSome(StageOutput.Captured(defaultArg capture (OutputCapture()))) }

    /// <summary>Hands each line of this stage's step output to write as it arrives.</summary>
    /// <remarks>Called from the reader threads of both streams, so write must tolerate that.</remarks>
    [<CustomOperation>] member inline _.
        redirectOutput
        ([<InlineIfLambda>] build: BuildStage, [<InlineIfLambda>] write: StdStream -> string -> unit): BuildStage
        = build >> fun ctx -> { ctx with Output = ValueSome(StageOutput.Redirect write) }

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
    /// <param name="build">The stage to add the step to.</param>
    /// <param name="exe">The executable to run.</param>
    /// <param name="args">The arguments to pass to the executable.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the step.</param>
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
                  then StageContext.writeLine ctx StdStream.Out $"%s{msg ctx}"
                  else StageContext.writeLine ctx StdStream.Out $"%s{StageContext.buildStepPrefix ctx i}: %s{msg ctx}"
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


    // =================================================================
    //                        InputSpec mirrors
    // =================================================================
    // One per custom operation above, for a stage that has already picked up a sub-stage declaring inputs.
    // Without these, placing a setting *after* such a sub-stage is an overload error rather than a no-op.

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("envVars")>] member inline this.
        envVars
        (spec: InputSpec<BuildStage>, kvs: seq<string * string>): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.envVars(build, kvs)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("acceptExitCodes")>] member inline this.
        acceptExitCodes
        (spec: InputSpec<BuildStage>, codes: int seq): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.acceptExitCodes(build, codes)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("failIfIgnored")>] member inline this.
        failIfIgnored
        (spec: InputSpec<BuildStage>, ?flag: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.failIfIgnored(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("failIfNoActiveSubStage")>] member inline this.
        failIfNoActiveSubStage
        (spec: InputSpec<BuildStage>, ?flag: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.failIfNoActiveSubStage(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("continueStepsOnFailure")>] member inline this.
        continueStepsOnFailure
        (spec: InputSpec<BuildStage>, ?flag): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.continueStepsOnFailure(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("continueStageOnFailure")>] member inline this.
        continueStageOnFailure
        (spec: InputSpec<BuildStage>, ?flag): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.continueStageOnFailure(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("continueOnStepFailure")>] member inline this.
        continueOnStepFailure
        (spec: InputSpec<BuildStage>, ?flag): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.continueOnStepFailure(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("timeout")>] member inline this.
        timeout
        (spec: InputSpec<BuildStage>, seconds: int<second>): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.timeout(build, seconds)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("timeout")>] member inline this.
        timeout
        (spec: InputSpec<BuildStage>, seconds: float): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.timeout(build, seconds)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("timeout")>] member inline this.
        timeout
        (spec: InputSpec<BuildStage>, timespan: TimeSpan): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.timeout(build, timespan)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("timeoutForStep")>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildStage>, seconds: int<second>): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.timeoutForStep(build, seconds)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("timeoutForStep")>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildStage>, seconds: float): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.timeoutForStep(build, seconds)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("timeoutForStep")>] member inline this.
        timeoutForStep
        (spec: InputSpec<BuildStage>, timeSpan: TimeSpan): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.timeoutForStep(build, timeSpan)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("parallel'")>] member inline this.
        parallel'
        (spec: InputSpec<BuildStage>, ?flag: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.parallel'(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("parallel'")>] member inline this.
        parallel'
        (spec: InputSpec<BuildStage>, throttle: int): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.parallel'(build, throttle)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("parallel'")>] member inline this.
        parallel'
        (spec: InputSpec<BuildStage>, condition: StageContext -> int voption): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.parallel'(build, condition)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("parallel'")>] member inline this.
        parallel'
        (spec: InputSpec<BuildStage>, condition: StageContext -> Choice<bool, int>): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.parallel'(build, condition)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("parallel'")>] member inline this.
        parallel'
        (spec: InputSpec<BuildStage>, condition: StageContext -> Choice<int, bool>): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.parallel'(build, condition)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("parallel'")>] member inline this.
        parallel'
        (spec: InputSpec<BuildStage>, condition: StageContext -> bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.parallel'(build, condition)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("workingDir")>] member inline this.
        workingDir
        (spec: InputSpec<BuildStage>, path: string): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.workingDir(build, path)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("workingDir")>] member inline this.
        workingDir
        (spec: InputSpec<BuildStage>, path: IO.DirectoryInfo): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.workingDir(build, path)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("noPrefixForStep")>] member inline this.
        noPrefixForStep
        (spec: InputSpec<BuildStage>, ?flag: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.noPrefixForStep(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("noStdRedirectForStep")>] member inline this.
        noStdRedirectForStep
        (spec: InputSpec<BuildStage>, ?flag: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.noStdRedirectForStep(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("outputTo")>] member inline this.
        outputTo
        (spec: InputSpec<BuildStage>, output: StageOutput): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.outputTo(build, output)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("silentOutput")>] member inline this.
        silentOutput
        (spec: InputSpec<BuildStage>): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.silentOutput(build)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("captureOutput")>] member inline this.
        captureOutput
        (spec: InputSpec<BuildStage>, ?capture: OutputCapture): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.captureOutput(build, ?capture = capture)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("redirectOutput")>] member inline this.
        redirectOutput
        (spec: InputSpec<BuildStage>, write: StdStream -> string -> unit): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.redirectOutput(build, write)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("shuffleExecuteSequence")>] member inline this.
        shuffleExecuteSequence
        (spec: InputSpec<BuildStage>, ?flag: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.shuffleExecuteSequence(build, ?flag = flag)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("run")>] member inline this.
        run
        (spec: InputSpec<BuildStage>, buildStep: StageContext -> BuildStep): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, buildStep)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("run")>] member inline this.
        run
        (spec: InputSpec<BuildStage>, exe: string, args: string, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, exe, args, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("run")>] member inline this.
        run
        (spec: InputSpec<BuildStage>, command: string, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, command, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("run")>] member inline this.
        run
        (spec: InputSpec<BuildStage>, command: Cmd, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, command, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("run")>] member inline this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> string, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("run")>] member inline this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Async<string>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("run")>] member inline this.
        run
        (spec: InputSpec<BuildStage>, buildCmd: StageContext -> Cmd, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, buildCmd, ?cancellationToken = cancellationToken)) spec

    // `runSensitive` deliberately has no mirror. Its argument is a `FormattableString`, and F# only applies the
    // `string` -> `FormattableString` conversion when a single overload is in play: adding a second one turns
    // `runSensitive $"docker login -p {password}"` - the whole point of the operation - into an overload error.
    // Bind the input outside the stage instead, so the stage itself stays a plain `BuildStage`.

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("run")>] member inline _.
        run
        (spec: InputSpec<BuildStage>, step): InputSpec<BuildStage>
        // Inlines the flexible-signature step itself rather than delegating: forwarding to the `BuildStage`
        // overload would resolve `step` to one concrete signature, colliding with the mirror above it.
        = InputSpec.map (fun (build: BuildStage) -> build >> fun ctx -> {
            ctx with Steps = ctx.Steps @ [ Step.StepFn((^T or SRTPStageBuilderRunner):(static member unifyResult: ^T -> StepFnSignature) step) ]
        }) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("runHttpHealthCheck")>] member inline this.
        runHttpHealthCheck
        (spec: InputSpec<BuildStage>, url: string, ?configRequest, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.runHttpHealthCheck(build, url, ?configRequest = configRequest, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("echo")>] member inline this.
        echo
        (spec: InputSpec<BuildStage>, msg: StageContext -> string): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.echo(build, msg)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("verbosity")>] member inline this.
        verbosity
        (spec: InputSpec<BuildStage>, verbosity: Verbosity): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.verbosity(build, verbosity)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("verbose")>] member inline this.
        verbose
        (spec: InputSpec<BuildStage>): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.verbose(build)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("quiet")>] member inline this.
        quiet
        (spec: InputSpec<BuildStage>): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.quiet(build)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("echo")>] member inline this.
        echo
        (spec: InputSpec<BuildStage>, msg: string): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.echo(build, msg)) spec

let inline stage name = StageBuilder(name)
