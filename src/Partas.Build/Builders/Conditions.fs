[<AutoOpen>]
module Partas.Build.ConditionsBuilder

open System
open System.ComponentModel
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open Spectre.Console
open Partas.Build
open Partas.Build.Internal

type private IILAttribute = InlineIfLambdaAttribute
type private EBAttribute = EditorBrowsableAttribute
let [<Literal>] private never = EditorBrowsableState.Never
let [<Literal>] private advanced = EditorBrowsableState.Advanced

/// <summary>The leaf conditions, as plain <c>StageContext -&gt; bool</c> functions.</summary>
/// <remarks>
/// Fun.Build's originals branch on <c>Mode</c> to double as help and verification printers. That mode is not
/// ported — System.CommandLine generates the help — so each of these is only the predicate Fun.Build evaluates
/// under <c>Mode.Execution</c>.
/// </remarks>
module Conditions =
    let whenPlatform (platform: OSPlatform): BuildStageIsActive = fun _ -> RuntimeInformation.IsOSPlatform platform

    let whenEnvArg (info: EnvArg): BuildStageIsActive = fun ctx ->
        if String.IsNullOrEmpty info.Name then failwith "ENV variable name cannot be empty"
        info.IsOptional
        || (match StageContext.tryGetEnvVar ctx info.Name with
            | ValueSome value -> info.Values.IsEmpty || List.contains value info.Values
            | ValueNone -> false)

    let whenEnvVar (name: string) = whenEnvArg (EnvArg.Create name)
    let whenEnvVarValue (name: string) (value: string) = whenEnvArg (EnvArg.Create(name, values = [ value ]))

    /// The branch is read with <c>git branch --show-current</c> in the stage's working directory.
    // TODO Phase 6: retarget onto the `Cmd` runner once it exists, so this inherits env vars and cancellation too.
    let whenBranches (branches: string seq): BuildStageIsActive = fun ctx ->
        try
            let startInfo = ProcessStartInfo("git", "branch --show-current", RedirectStandardOutput = true)
            startInfo.StandardOutputEncoding <- Text.Encoding.UTF8
            StageContext.getWorkingDir ctx |> ValueOption.iter (fun dir -> startInfo.WorkingDirectory <- dir)
            use proc = Process.Start startInfo
            let branch = proc.StandardOutput.ReadLine()
            proc.WaitForExit()
            Seq.contains branch branches
        with ex ->
            AnsiConsole.MarkupLineInterpolated $"[red]Run git to get branch info failed: {ex.Message}[/]"
            false

    let whenBranch (branch: string) = whenBranches [ branch ]

    /// Runs <paramref name="stage"/> as a condition stage, reparented onto the stage being tested, and reports
    /// whether it succeeded. The stage runs for real: side effects and console output included.
    let whenStageSucceeds (stage: StageContext): BuildStageIsActive = fun ctx ->
        let stage = { stage with ParentContext = ValueSome(StageParent.Stage ctx) }
        StageContext.run stage StageIndex.Condition CancellationToken.None |> fst

let inline private addCondition ([<IIL>] build: BuildConditions) (condition: BuildStageIsActive): BuildConditions =
    fun conditions -> build conditions @ [ condition ]

/// <summary>Collects conditions for <c>whenAll</c>, <c>whenAny</c> and <c>whenNot</c>.</summary>
/// <remarks>
/// There is no <c>cmdArg</c> operation: System.CommandLine owns arguments now, so a stage that wants to branch on
/// a flag binds it in an <c>inputs</c> CE and tests the bound value with <c>when'</c>.
/// </remarks>
[<EB(advanced)>]
type ConditionsBuilder() =
    [<EB(never)>] member inline _.Yield(_: unit): BuildConditions = id
    [<EB(never)>] member inline _.Zero(): BuildConditions = id
    [<EB(never)>]
    member inline _.Yield([<IIL>] condition: BuildStageIsActive): BuildStageIsActive = condition
    [<EB(never)>]
    member inline _.Delay([<IIL>] fn: unit -> BuildConditions): BuildConditions = fn ()
    [<EB(never)>]
    member inline _.Delay([<IIL>] fn: unit -> BuildStageIsActive): BuildConditions = fun conditions -> conditions @ [ fn () ]
    [<EB(never)>]
    member inline _.Combine([<IIL>] condition: BuildStageIsActive, [<IIL>] build: BuildConditions): BuildConditions =
        fun conditions -> build (conditions @ [ condition ])
    [<EB(never)>]
    member inline _.For([<IIL>] build: BuildConditions, [<IIL>] fn: unit -> BuildConditions): BuildConditions = build >> fn ()
    [<EB(never)>]
    member inline _.For([<IIL>] build: BuildConditions, [<IIL>] fn: unit -> BuildStageIsActive): BuildConditions =
        addCondition build (fn ())

    /// <summary>Adds a literal boolean condition to the builder.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The value is typically a boolean bound by an enclosing <c>inputs</c> CE.
    /// </remarks>
    [<CustomOperation("when'")>]
    member inline _.when'([<IIL>] build: BuildConditions, value: bool) = addCondition build (fun _ -> value)

    /// <summary>Runs a stage as a condition and uses its success as the activation answer.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition stage runs immediately during condition evaluation and must complete (or fail) before proceeding.
    /// </remarks>
    [<CustomOperation("when'")>]
    member inline _.when'([<IIL>] build: BuildConditions, stage: StageContext) = addCondition build (Conditions.whenStageSucceeds stage)

    /// <summary>Adds an environment variable condition using an <c>EnvArg</c> specification.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/envVar/*"/>
    /// </remarks>
    [<CustomOperation("envVar")>]
    member inline _.envVar([<IIL>] build: BuildConditions, arg: EnvArg) = addCondition build (Conditions.whenEnvArg arg)

    /// <summary>Adds an environment variable condition by name only.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition is met if the environment variable is set (non-empty).
    /// </remarks>
    [<CustomOperation("envVar")>]
    member inline _.envVar([<IIL>] build: BuildConditions, name: string) = addCondition build (Conditions.whenEnvVar name)

    /// <summary>Adds an environment variable condition that checks both name and value.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition is met only if the environment variable is set and its value matches exactly.
    /// </remarks>
    [<CustomOperation("envVar")>]
    member inline _.envVar([<IIL>] build: BuildConditions, name: string, value: string) =
        addCondition build (Conditions.whenEnvVarValue name value)

    /// <summary>Adds a Git branch condition for a single branch name.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/gitBranch/*"/>
    /// </remarks>
    [<CustomOperation("branch")>]
    member inline _.branch([<IIL>] build: BuildConditions, branch: string) = addCondition build (Conditions.whenBranch branch)

    /// <summary>Adds a Git branch condition that matches any of the specified branch names.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/gitBranch/*"/>
    /// </remarks>
    [<CustomOperation("branches")>]
    member inline _.branches([<IIL>] build: BuildConditions, branches: string seq) = addCondition build (Conditions.whenBranches branches)

    /// <summary>Adds a condition that is met when running on Windows.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// </remarks>
    [<CustomOperation("platformWindows")>]
    member inline _.platformWindows([<IIL>] build: BuildConditions, ?isTrue: bool) = addCondition build (Conditions.whenPlatform OSPlatform.Windows >> if defaultArg isTrue true then id else not)

    /// <summary>Adds a condition that is met when running on Linux.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// </remarks>
    [<CustomOperation("platformLinux")>]
    member inline _.platformLinux([<IIL>] build: BuildConditions, ?isTrue: bool) = addCondition build (Conditions.whenPlatform OSPlatform.Linux >> (if defaultArg isTrue true then id else not))

    /// <summary>Adds a condition that is met when running on macOS.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// </remarks>
    [<CustomOperation("platformOSX")>]
    member inline _.platformOSX([<IIL>] build: BuildConditions, ?isTrue: bool) = addCondition build (Conditions.whenPlatform OSPlatform.OSX >> (if defaultArg isTrue true then id else not))

    [<CustomOperation>] member inline _.
        platform([<IIL>] build: BuildConditions, platform: OSPlatform) = addCondition build (Conditions.whenPlatform platform)

// The three `Run` members below are deliberately not `inline`: they apply a `BuildConditions`, and inlining an
// application of a plain function type defeats the optimiser (`FS1118`) in Release builds only.
// Each collects its conditions once, at construction, and closes over the resulting list.
// An empty body is the identity of the fold: `whenAll { }` is active, `whenAny { }` is not.

[<EB(advanced)>]
type WhenAnyBuilder() =
    inherit ConditionsBuilder()
    [<EB(never)>]
    member _.Run(build: BuildConditions): BuildStageIsActive =
        let conditions = build []
        fun ctx -> conditions |> List.exists (fun condition -> condition ctx)

[<EB(advanced)>]
type WhenAllBuilder() =
    inherit ConditionsBuilder()
    [<EB(never)>]
    member _.Run(build: BuildConditions): BuildStageIsActive =
        let conditions = build []
        fun ctx -> conditions |> List.forall (fun condition -> condition ctx)

[<EB(advanced)>]
type WhenNotBuilder() =
    inherit ConditionsBuilder()
    [<EB(never)>]
    member _.Run(build: BuildConditions): BuildStageIsActive =
        let conditions = build []
        fun ctx -> conditions |> List.forall (fun condition -> not (condition ctx))

/// Describes one environment variable, in place of a wall of `whenEnvVar` overloads.
[<EB(advanced)>]
type WhenEnvBuilder() =
    [<EB(never)>]
    member _.Run(build: BuildEnvInfo): BuildStageIsActive = Conditions.whenEnvArg (build (EnvArg.Create ""))
    [<EB(never)>] member inline _.Yield(_: unit): BuildEnvInfo = id
    [<EB(never)>] member inline _.Zero(): BuildEnvInfo = id
    [<EB(never)>] member inline _.Yield([<IIL>] build: BuildEnvInfo): BuildEnvInfo = build
    [<EB(never)>]
    member inline _.Delay([<IIL>] fn: unit -> BuildEnvInfo): BuildEnvInfo = fn ()

    /// <summary>Sets the environment variable name to check.</summary>
    [<CustomOperation "name">]
    member inline _.name([<IIL>] build: BuildEnvInfo, name: string) = build >> _.WithName(name)

    /// <summary>Sets an optional description for the environment variable.</summary>
    [<CustomOperation "description">]
    member inline _.description([<IIL>] build: BuildEnvInfo, description: string) = build >> _.WithDescription(Some description)

    /// <summary>Sets a single required value for the environment variable.</summary>
    /// <remarks>The condition is met if the environment variable equals this value.</remarks>
    [<CustomOperation "value">]
    member inline _.value([<IIL>] build: BuildEnvInfo, value: string) = build >> _.WithValues[value]

    /// <summary>Sets multiple accepted values for the environment variable.</summary>
    /// <remarks>The condition is met if the environment variable value is one of the accepted values.</remarks>
    [<CustomOperation "acceptValues">]
    member inline _.acceptValues([<IIL>] build: BuildEnvInfo, values: string list) = build >> _.WithValues(values)

    /// <summary>Marks the environment variable as optional.</summary>
    /// <remarks>When optional, the condition is met even if the variable is unset.</remarks>
    [<CustomOperation "optional">]
    member inline _.optional([<IIL>] build: BuildEnvInfo) = build >> _.WithIsOptional(true)

/// A stage run purely for its result. Everything `stage` accepts is accepted here.
[<EB(advanced)>]
type WhenStageBuilder(name: string) =
    inherit StageBuilder(name)
    [<EB(never)>]
    member _.Run(build: BuildStage): BuildStageIsActive = Conditions.whenStageSucceeds (build (StageContext.create name))

type StageBuilder with
    /// <summary>Sets whether the stage is active using a literal boolean condition.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The value is typically a boolean bound by an enclosing <c>inputs</c> CE.
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("when'")>]
    member inline _.when'([<IIL>] build: BuildStage, value: bool) = StageContext.buildStageIsActive build (fun _ -> value)

    /// <summary>Runs a stage as a condition and uses its success as the activation answer.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition stage runs immediately during condition evaluation and must complete (or fail) before proceeding.
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("when'")>]
    member inline _.when'([<IIL>] build: BuildStage, stage: StageContext) =
        StageContext.buildStageIsActive build (Conditions.whenStageSucceeds stage)

    /// <summary>Adds an environment variable condition using an <c>EnvArg</c> specification.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/envVar/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenEnvVar")>]
    member inline _.whenEnvVar([<IIL>] build: BuildStage, arg: EnvArg) = StageContext.buildStageIsActive build (Conditions.whenEnvArg arg)

    /// <summary>Adds an environment variable condition by name only.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition is met if the environment variable is set (non-empty).
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenEnvVar")>]
    member inline _.whenEnvVar([<IIL>] build: BuildStage, name: string) = StageContext.buildStageIsActive build (Conditions.whenEnvVar name)

    /// <summary>Adds an environment variable condition that checks both name and value.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition is met only if the environment variable is set and its value matches exactly.
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenEnvVar")>]
    member inline _.whenEnvVar([<IIL>] build: BuildStage, name: string, value: string) =
        StageContext.buildStageIsActive build (Conditions.whenEnvVarValue name value)

    /// <summary>Adds a Git branch condition for a single branch name.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/gitBranch/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenBranch")>]
    member inline _.whenBranch([<IIL>] build: BuildStage, branch: string) = StageContext.buildStageIsActive build (Conditions.whenBranch branch)

    /// <summary>Adds a Git branch condition that matches any of the specified branch names.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/gitBranch/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenBranches")>]
    member inline _.whenBranches([<IIL>] build: BuildStage, branches: string seq) =
        StageContext.buildStageIsActive build (Conditions.whenBranches branches)

    /// <summary>Adds a condition that is met when running on Windows.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    /// <summary>Adds a condition that is met when running on Windows.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenWindows")>]
    member inline _.whenWindows([<IIL>] build: BuildStage, ?isTrue: bool) = StageContext.buildStageIsActive build (Conditions.whenPlatform OSPlatform.Windows >> if defaultArg isTrue true then id else not)

    /// <summary>Adds a condition that is met when running on Linux.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenLinux")>]
    member inline _.whenLinux([<IIL>] build: BuildStage, ?isTrue: bool) = StageContext.buildStageIsActive build (Conditions.whenPlatform OSPlatform.Linux >> (if defaultArg isTrue true then id else not))

    /// <summary>Adds a condition that is met when running on macOS.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenOSX")>]
    member inline _.whenOSX([<IIL>] build: BuildStage, ?isTrue: bool) = StageContext.buildStageIsActive build (Conditions.whenPlatform OSPlatform.OSX >> (if defaultArg isTrue true then id else not))
    [<CustomOperation>] member inline _.
        whenPlatform
        ([<IIL>] build: BuildStage, platform: OSPlatform): BuildStage
        = StageContext.buildStageIsActive build (Conditions.whenPlatform platform)


    // =================================================================
    //                        InputSpec mirrors
    // =================================================================
    // One per custom operation above, for a stage that has already picked up a sub-stage declaring inputs.
    // Without these, placing a setting *after* such a sub-stage is an overload error rather than a no-op.

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("when'")>] member inline this.
        when'
        (spec: InputSpec<BuildStage>, value: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.when'(build, value)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("when'")>] member inline this.
        when'
        (spec: InputSpec<BuildStage>, stage: StageContext): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.when'(build, stage)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenEnvVar")>] member inline this.
        whenEnvVar
        (spec: InputSpec<BuildStage>, arg: EnvArg): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenEnvVar(build, arg)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenEnvVar")>] member inline this.
        whenEnvVar
        (spec: InputSpec<BuildStage>, name: string): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenEnvVar(build, name)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenEnvVar")>] member inline this.
        whenEnvVar
        (spec: InputSpec<BuildStage>, name: string, value: string): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenEnvVar(build, name, value)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenBranch")>] member inline this.
        whenBranch
        (spec: InputSpec<BuildStage>, branch: string): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenBranch(build, branch)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenBranches")>] member inline this.
        whenBranches
        (spec: InputSpec<BuildStage>, branches: string seq): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenBranches(build, branches)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenWindows")>] member inline this.
        whenWindows
        (spec: InputSpec<BuildStage>, ?isTrue: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenWindows(build, ?isTrue = isTrue)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenLinux")>] member inline this.
        whenLinux
        (spec: InputSpec<BuildStage>, ?isTrue: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenLinux(build, ?isTrue = isTrue)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenOSX")>] member inline this.
        whenOSX
        (spec: InputSpec<BuildStage>, ?isTrue: bool): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenOSX(build, ?isTrue = isTrue)) spec

    /// <summary>The <c>InputSpec</c> mirror of the operation of the same name.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/mirror/*"/>
    [<CustomOperation("whenPlatform")>] member inline this.
        whenPlatform
        (spec: InputSpec<BuildStage>, platform: OSPlatform): InputSpec<BuildStage>
        = InputSpec.map (fun (build: BuildStage) -> this.whenPlatform(build, platform)) spec

/// The stage is active when any of the conditions in the body is met. An empty body is never active.
let whenAny = WhenAnyBuilder()
/// The stage is active when every condition in the body is met. An empty body is always active.
let whenAll = WhenAllBuilder()
/// The stage is active when none of the conditions in the body is met. An empty body is always active.
let whenNot = WhenNotBuilder()
/// The stage is active when the described environment variable is set, and matches one of its values if any are given.
let whenEnv = WhenEnvBuilder()
/// The stage is active when the stage described in the body finishes successfully.
let inline whenStage name = WhenStageBuilder name

/// <summary>Stage-producing conditions that bind the value they tested.</summary>
[<AutoOpen>]
module ValueConditions =
    /// <summary>Yields the stage <paramref name="build"/> makes of the value, and nothing when there is none.</summary>
    /// <remarks>
    /// The alternative is <c>when' value.IsSome</c> above a <c>run</c> that reaches for <c>value.Value</c>,
    /// which is correct only because of evaluation order — nothing the compiler checks, and a refactor that
    /// moves the condition below the step compiles cleanly and throws at run time.
    /// <para>
    /// It returns a list so that the absent case contributes no stage at all, rather than an inactive one
    /// that would need a name it was never given.
    /// </para>
    /// </remarks>
    let whenSome (value: 'a option) (build: 'a -> StageContext): StageContext list =
        match value with
        | Some value -> [ build value ]
        | None -> []

    /// <summary>Yields the stage <paramref name="build"/> makes of an <c>Ok</c> value, and nothing for <c>Error</c>.</summary>
    let whenOk (value: Result<'a, 'b>) (build: 'a -> StageContext): StageContext list =
        match value with
        | Ok value -> [ build value ]
        | Error _ -> []
