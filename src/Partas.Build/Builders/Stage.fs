[<AutoOpen>]
module Partas.Build.StageBuilder

open System
open System.ComponentModel
open Partas.Build.Internal
open Partas.Build
open FSharp.Data.UnitSystems.SI

[<Measure>] type second = UnitNames.second
[<Measure>] type s = UnitSymbols.s

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

[<EB(advanced)>]
type StageBuilder(name: string) =
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
    /// <remarks>
    /// The function receives the current stage context and returns a step function that operates on that context.
    /// <para>
    /// Unlike its neighbours this overload stays a mirrored pair over the concrete representations. It is the
    /// only <c>run</c> taking no optional argument, which is what resolves <c>run (fun ctx -> failwith "...")</c>
    /// - a lambda whose return type the call site leaves open. Generic in the state it ties with the
    /// flexible-signature overload and such a call site stops compiling.
    /// </para>
    /// </remarks>
    [<CustomOperation>] member _.
        run
        (build: BuildStage, buildStep: StageContext -> BuildStep): BuildStage
        = build >> fun ctx ->
        { ctx with Steps = ctx.Steps @ [ Step.StepFn(ValueNone, fun ctx i -> async { return! buildStep ctx ctx i }) ] }

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation>] member inline this.
        run
        (spec: InputSpec<BuildStage>, buildStep: StageContext -> BuildStep): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.run(build, buildStep)) spec

let inline stage name = StageBuilder(name)
