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

/// The inputs a plain branch declares, read off the stage it builds on an empty context.
let private declaredBuildInputs name (build: BuildStage) =
    StageContext.create name |> build |> StageContext.declaredInputs

let private withDeclaredBuildInputs name (build: BuildStage) (spec: InputSpec<'T>): InputSpec<'T> =
    { spec with Inputs = InputSpec.union [ declaredBuildInputs name build; spec.Inputs ] }

let private withDeclaredStageInputs (stage: StageContext) (spec: InputSpec<'T>): InputSpec<'T> =
    { spec with Inputs = InputSpec.union [ StageContext.declaredInputs stage; spec.Inputs ] }

type private IILAttribute = InlineIfLambdaAttribute
type private EBAttribute = EditorBrowsableAttribute
[<Literal>]
let private never = EditorBrowsableState.Never
[<Literal>]
let private advanced = EditorBrowsableState.Advanced

/// <summary>
/// SRTP management for accepted <c>run</c> signatures which are transformed into
/// StepFnSignatures.
/// </summary>
[<EB(never)>]
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

and [<EB(advanced)>]
    StageBuilder(name: string) =
    inherit StageSettingsBuilder()
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
    member _.Combine (build: BuildStage, spec: InputSpec<BuildStage>): InputSpec<BuildStage> =
        InputSpec.map (BuildStage.merge build) spec |> withDeclaredBuildInputs name build
    [<EB(never)>]
    member inline _.Combine (spec, rest): InputSpec<BuildStage> = InputSpec.map2 (>>) spec rest
    [<EB(never)>]
    member _.Combine (spec: InputSpec<BuildStage>, rest: BuildStage): InputSpec<BuildStage> =
        InputSpec.map (fun build -> build >> rest) spec |> withDeclaredBuildInputs name rest
    [<EB(never)>]
    member _.Combine (spec: InputSpec<StageContext>, build: BuildStage): InputSpec<BuildStage> =
        InputSpec.map (fun stage -> StageContext.addSubStage stage >> build) spec |> withDeclaredBuildInputs name build
    [<EB(never)>]
    member inline _.Combine (spec, rest): InputSpec<BuildStage> = InputSpec.map2 (fun stage ->  (>>) (StageContext.addSubStage stage)) spec rest
    [<EB(never)>]
    member _.Combine (stage: StageContext, spec: InputSpec<BuildStage>): InputSpec<BuildStage> =
        InputSpec.map ((>>) (StageContext.addSubStage stage)) spec |> withDeclaredStageInputs stage
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
    member _.For (build: BuildStage, fn: unit -> InputSpec<BuildStage>): InputSpec<BuildStage> =
        InputSpec.map (fun rest -> build >> rest) (fn ()) |> withDeclaredBuildInputs name build
    [<EB(never)>]
    member _.For (build: BuildStage, fn: unit -> InputSpec<StageContext>): InputSpec<BuildStage> =
        InputSpec.map (fun stage -> build >> StageContext.addSubStage stage) (fn ()) |> withDeclaredBuildInputs name build
    [<EB(never)>]
    member _.For (spec: InputSpec<BuildStage>, fn: unit -> BuildStage): InputSpec<BuildStage> =
        let rest = fn ()
        InputSpec.map (fun build -> build >> rest) spec |> withDeclaredBuildInputs name rest
    [<EB(never)>]
    member _.For (spec: InputSpec<BuildStage>, fn: unit -> StageContext): InputSpec<BuildStage> =
        let stage = fn ()
        InputSpec.map (fun build -> build >> StageContext.addSubStage stage) spec |> withDeclaredStageInputs stage
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
        { ctx with Steps = steps |> Seq.map (fun step -> Step.StepFn(ValueNone, step)) |> Seq.append ctx.Steps |> Seq.toList }


    // =================================================================
    //                         CustomOperations
    // =================================================================
    /// <summary>Adds a step built from a context-dependent function.</summary>
    /// <remarks>The function receives the current stage context and returns a step function that operates on that context.</remarks>
    [<CustomOperation>] member _.
        run
        (build: BuildStage, buildStep: StageContext -> BuildStep): BuildStage
        = build >> fun ctx ->
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, fun ctx i -> async { return! buildStep ctx ctx i }) ] }

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
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step buildCmd cancellationToken) ] }

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> Task<string>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        let buildCmd ctx = step ctx |> Task.map Cmd.ofString |> Async.AwaitTask
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step buildCmd cancellationToken) ] }

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member _.
        run
        (build: BuildStage, buildCmd: StageContext -> Cmd, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step (buildCmd >> Async.singleton) cancellationToken) ] }

    /// <summary>Adds a step that runs a command line derived from the stage context.</summary>
    /// <remarks>The command line string is split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> string option, ?cancellationToken: CancellationToken): BuildStage
        = this.run (build, (fun ctx -> step ctx |> Option.map Cmd.ofString), ?cancellationToken = cancellationToken)



    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> Async<string option>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        let buildCmd ctx = step ctx |> Async.map (Option.map Cmd.ofString)
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption buildCmd cancellationToken) ] }

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> Task<string option>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        let buildCmd ctx = step ctx |> Task.map (Option.map Cmd.ofString) |> Async.AwaitTask
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption buildCmd cancellationToken) ] }

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> Async<Cmd>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.step step cancellationToken) ] }

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> Async<Cmd option>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption step cancellationToken) ] }

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> Async<Result<Cmd, string>>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepResult step cancellationToken) ] }

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (build: BuildStage, step: StageContext -> Async<Result<Cmd option, string>>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepResultOption step cancellationToken) ] }

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member _.
        run
        (build: BuildStage, buildCmd: StageContext -> Cmd option, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepOption (buildCmd >> Async.singleton) cancellationToken) ] }

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member _.
        run
        (build: BuildStage, buildCmd: StageContext -> Result<Cmd, string>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepResult (buildCmd >> Async.singleton) cancellationToken) ] }

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member _.
        run
        (build: BuildStage, buildCmd: StageContext -> Result<Cmd option, string>, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, CmdRunner.stepResultOption (buildCmd >> Async.singleton) cancellationToken) ] }

    /// <summary>Adds a step with flexible signature support.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/run/*"/>
    [<CustomOperation>] member inline _.
        run
        ([<InlineIfLambda>] build: BuildStage, step): BuildStage
        = build >> fun ctx -> {
            ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, ((^T or SRTPStageBuilderRunner):(static member unifyResult: ^T -> StepFnSignature) step)) ]
        }
    /// <summary>Adds a step running deferred work.</summary>
    /// <remarks>
    /// The operation runs under the stage's working directory, environment, acceptable exit codes and output
    /// routing, and reports a structured failure the runner renders at the print site.
    /// <c>label</c> is what <c>--explain</c> shows for the step; without one it shows the step's index.
    /// </remarks>
    [<CustomOperation>] member _.
        runOperation
        (build: BuildStage, operation: Operation<unit>, ?label: string): BuildStage
        = build >> fun ctx ->
        { ctx with Steps = ctx.Steps @ [ Step.Operation(ValueOption.ofOption label, Operation.toStepOutcome operation) ] }

    /// <summary>Adds a step that polls an HTTP endpoint for health.</summary>
    /// <remarks>The step repeatedly polls the given URL until it succeeds or the stage is cancelled. Useful for waiting for services to become available.</remarks>
    [<CustomOperation>] member _.
        runHttpHealthCheck
        (build: BuildStage, url: string, ?configRequest, ?cancellationToken: CancellationToken): BuildStage
        = build >> fun ctx ->
        let configRequest = defaultArg configRequest ignore
        let cancellationToken = defaultArg cancellationToken CancellationToken.None
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, fun ctx _ -> StageContext.runHttpHealthCheckCancelableWithConfigRequest ctx cancellationToken configRequest url) ] }

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation>] member inline this.
        run
        (spec: InputSpec<BuildStage>, buildStep: StageContext -> BuildStep): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, buildStep)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation>] member inline this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> string, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation>] member inline this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Async<string>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation>] member inline this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Task<string>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation>] member inline this.
        run
        (spec: InputSpec<BuildStage>, buildCmd: StageContext -> Cmd, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, buildCmd, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a command line derived from the stage context.</summary>
    /// <remarks>The command line string is split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> string option, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run (build, (fun ctx -> step ctx |> Option.map Cmd.ofString), ?cancellationToken = cancellationToken)) spec



    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Async<string option>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Task<string option>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Async<Cmd>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Async<Cmd option>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Async<Result<Cmd, string>>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a command line asynchronously derived from the stage context.</summary>
    /// <remarks>The command line string is computed asynchronously and split on whitespace, honouring quotes. Use <c>run (cmd $"...")</c> to preserve interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Async<Result<Cmd option, string>>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Cmd option, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Result<Cmd, string>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>Adds a step that runs a prepared command derived from the stage context.</summary>
    /// <remarks>Use this overload when the command is built dynamically. Pair with <c>cmd</c> to keep interpolation holes as single arguments.</remarks>
    [<CustomOperation>] member this.
        run
        (spec: InputSpec<BuildStage>, step: StageContext -> Result<Cmd option, string>, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, step, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation>] member inline _.
        run
        (spec: InputSpec<BuildStage>, step): InputSpec<BuildStage>
        // Inlines the flexible-signature step itself rather than delegating: forwarding to the `BuildStage`
        // overload would resolve `step` to one concrete signature, colliding with the mirror above it.
        = InputSpec.map (fun (build: BuildStage) -> build >> fun ctx -> {
            ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, ((^T or SRTPStageBuilderRunner):(static member unifyResult: ^T -> StepFnSignature) step)) ]
        }) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("runHttpHealthCheck")>] member inline this.
        runHttpHealthCheck
        (spec: InputSpec<BuildStage>, url: string, ?configRequest, ?cancellationToken: CancellationToken): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.runHttpHealthCheck(build, url, ?configRequest = configRequest, ?cancellationToken = cancellationToken)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("runOperation")>] member inline this.
        runOperation
        (spec: InputSpec<BuildStage>, operation: Operation<unit>, ?label: string): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.runOperation(build, operation, ?label = label)) spec

let inline stage name = StageBuilder(name)
