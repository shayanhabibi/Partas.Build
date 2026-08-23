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

let inline private addCondition ([<InlineIfLambda>] build: BuildConditions) (condition: BuildStageIsActive): BuildConditions =
    fun conditions -> build conditions @ [ condition ]

/// <summary>Collects conditions for <c>whenAll</c>, <c>whenAny</c> and <c>whenNot</c>.</summary>
/// <remarks>
/// There is no <c>cmdArg</c> operation: System.CommandLine owns arguments now, so a stage that wants to branch on
/// a flag binds it in an <c>inputs</c> CE and tests the bound value with <c>when'</c>.
/// </remarks>
[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type ConditionsBuilder() =
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Yield(_: unit): BuildConditions = id
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Zero(): BuildConditions = id
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Yield([<InlineIfLambda>] condition: BuildStageIsActive): BuildStageIsActive = condition
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildConditions): BuildConditions = fn ()
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildStageIsActive): BuildConditions = fun conditions -> conditions @ [ fn () ]
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Combine([<InlineIfLambda>] condition: BuildStageIsActive, [<InlineIfLambda>] build: BuildConditions): BuildConditions =
        fun conditions -> build (conditions @ [ condition ])
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.For([<InlineIfLambda>] build: BuildConditions, [<InlineIfLambda>] fn: unit -> BuildConditions): BuildConditions = build >> fn ()
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.For([<InlineIfLambda>] build: BuildConditions, [<InlineIfLambda>] fn: unit -> BuildStageIsActive): BuildConditions =
        addCondition build (fn ())

    /// <summary>Adds a literal boolean condition to the builder.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The value is typically a boolean bound by an enclosing <c>inputs</c> CE.
    /// </remarks>
    [<CustomOperation("when'")>]
    member inline _.when'([<InlineIfLambda>] build: BuildConditions, value: bool) = addCondition build (fun _ -> value)

    /// <summary>Runs a stage as a condition and uses its success as the activation answer.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition stage runs immediately during condition evaluation and must complete (or fail) before proceeding.
    /// </remarks>
    [<CustomOperation("when'")>]
    member inline _.when'([<InlineIfLambda>] build: BuildConditions, stage: StageContext) = addCondition build (Conditions.whenStageSucceeds stage)

    /// <summary>Adds an environment variable condition using an <c>EnvArg</c> specification.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/envVar/*"/>
    /// </remarks>
    [<CustomOperation("envVar")>]
    member inline _.envVar([<InlineIfLambda>] build: BuildConditions, arg: EnvArg) = addCondition build (Conditions.whenEnvArg arg)

    /// <summary>Adds an environment variable condition by name only.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition is met if the environment variable is set (non-empty).
    /// </remarks>
    [<CustomOperation("envVar")>]
    member inline _.envVar([<InlineIfLambda>] build: BuildConditions, name: string) = addCondition build (Conditions.whenEnvVar name)

    /// <summary>Adds an environment variable condition that checks both name and value.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition is met only if the environment variable is set and its value matches exactly.
    /// </remarks>
    [<CustomOperation("envVar")>]
    member inline _.envVar([<InlineIfLambda>] build: BuildConditions, name: string, value: string) =
        addCondition build (Conditions.whenEnvVarValue name value)

    /// <summary>Adds a Git branch condition for a single branch name.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/gitBranch/*"/>
    /// </remarks>
    [<CustomOperation("branch")>]
    member inline _.branch([<InlineIfLambda>] build: BuildConditions, branch: string) = addCondition build (Conditions.whenBranch branch)

    /// <summary>Adds a Git branch condition that matches any of the specified branch names.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/gitBranch/*"/>
    /// </remarks>
    [<CustomOperation("branches")>]
    member inline _.branches([<InlineIfLambda>] build: BuildConditions, branches: string seq) = addCondition build (Conditions.whenBranches branches)

    /// <summary>Adds a condition that is met when running on Windows.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// </remarks>
    [<CustomOperation("platformWindows")>]
    member inline _.platformWindows([<InlineIfLambda>] build: BuildConditions, ?isTrue: bool) = addCondition build (Conditions.whenPlatform OSPlatform.Windows >> if defaultArg isTrue true then id else not)

    /// <summary>Adds a condition that is met when running on Linux.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// </remarks>
    [<CustomOperation("platformLinux")>]
    member inline _.platformLinux([<InlineIfLambda>] build: BuildConditions, ?isTrue: bool) = addCondition build (Conditions.whenPlatform OSPlatform.Linux >> (if defaultArg isTrue true then id else not))

    /// <summary>Adds a condition that is met when running on macOS.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// </remarks>
    [<CustomOperation("platformOSX")>]
    member inline _.platformOSX([<InlineIfLambda>] build: BuildConditions, ?isTrue: bool) = addCondition build (Conditions.whenPlatform OSPlatform.OSX >> (if defaultArg isTrue true then id else not))

    [<CustomOperation>] member inline _.
        platform([<InlineIfLambda>] build: BuildConditions, platform: OSPlatform) = addCondition build (Conditions.whenPlatform platform)

// The three `Run` members below are deliberately not `inline`: they apply a `BuildConditions`, and inlining an
// application of a plain function type defeats the optimiser (`FS1118`) in Release builds only.
// Each collects its conditions once, at construction, and closes over the resulting list.
// An empty body is the identity of the fold: `whenAll { }` is active, `whenAny { }` is not.

[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type WhenAnyBuilder() =
    inherit ConditionsBuilder()
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member _.Run(build: BuildConditions): BuildStageIsActive =
        let conditions = build []
        fun ctx -> conditions |> List.exists (fun condition -> condition ctx)

[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type WhenAllBuilder() =
    inherit ConditionsBuilder()
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member _.Run(build: BuildConditions): BuildStageIsActive =
        let conditions = build []
        fun ctx -> conditions |> List.forall (fun condition -> condition ctx)

[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type WhenNotBuilder() =
    inherit ConditionsBuilder()
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member _.Run(build: BuildConditions): BuildStageIsActive =
        let conditions = build []
        fun ctx -> conditions |> List.forall (fun condition -> not (condition ctx))

/// Describes one environment variable, in place of a wall of `whenEnvVar` overloads.
[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type WhenEnvBuilder() =
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member _.Run(build: BuildEnvInfo): BuildStageIsActive = Conditions.whenEnvArg (build (EnvArg.Create ""))
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Yield(_: unit): BuildEnvInfo = id
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Zero(): BuildEnvInfo = id
    [<EditorBrowsable(EditorBrowsableState.Never)>] member inline _.Yield([<InlineIfLambda>] build: BuildEnvInfo): BuildEnvInfo = build
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildEnvInfo): BuildEnvInfo = fn ()

    /// <summary>Sets the environment variable name to check.</summary>
    [<CustomOperation "name">]
    member inline _.name([<InlineIfLambda>] build: BuildEnvInfo, name: string) = build >> fun info -> info.WithName name

    /// <summary>Sets an optional description for the environment variable.</summary>
    [<CustomOperation "description">]
    member inline _.description([<InlineIfLambda>] build: BuildEnvInfo, description: string) = build >> fun info -> info.WithDescription(Some description)

    /// <summary>Sets a single required value for the environment variable.</summary>
    /// <remarks>The condition is met if the environment variable equals this value.</remarks>
    [<CustomOperation "value">]
    member inline _.value([<InlineIfLambda>] build: BuildEnvInfo, value: string) = build >> fun info -> info.WithValues [ value ]

    /// <summary>Sets multiple accepted values for the environment variable.</summary>
    /// <remarks>The condition is met if the environment variable value is one of the accepted values.</remarks>
    [<CustomOperation "acceptValues">]
    member inline _.acceptValues([<InlineIfLambda>] build: BuildEnvInfo, values: string list) = build >> fun info -> info.WithValues values

    /// <summary>Marks the environment variable as optional.</summary>
    /// <remarks>When optional, the condition is met even if the variable is unset.</remarks>
    [<CustomOperation "optional">]
    member inline _.optional([<InlineIfLambda>] build: BuildEnvInfo) = build >> fun info -> info.WithIsOptional true

/// A stage run purely for its result. Everything `stage` accepts is accepted here.
[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type WhenStageBuilder(name: string) =
    inherit StageBuilder(name)
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member _.Run(build: BuildStage): BuildStageIsActive = Conditions.whenStageSucceeds (build (StageContext.create name))

type StageBuilder with
    /// <summary>Sets whether the stage is active using a literal boolean condition.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The value is typically a boolean bound by an enclosing <c>inputs</c> CE.
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("when'")>]
    member inline _.when'([<InlineIfLambda>] build: BuildStage, value: bool) = StageContext.buildStageIsActive build (fun _ -> value)

    /// <summary>Runs a stage as a condition and uses its success as the activation answer.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition stage runs immediately during condition evaluation and must complete (or fail) before proceeding.
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("when'")>]
    member inline _.when'([<InlineIfLambda>] build: BuildStage, stage: StageContext) =
        StageContext.buildStageIsActive build (Conditions.whenStageSucceeds stage)

    /// <summary>Adds an environment variable condition using an <c>EnvArg</c> specification.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/envVar/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenEnvVar")>]
    member inline _.whenEnvVar([<InlineIfLambda>] build: BuildStage, arg: EnvArg) = StageContext.buildStageIsActive build (Conditions.whenEnvArg arg)

    /// <summary>Adds an environment variable condition by name only.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition is met if the environment variable is set (non-empty).
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenEnvVar")>]
    member inline _.whenEnvVar([<InlineIfLambda>] build: BuildStage, name: string) = StageContext.buildStageIsActive build (Conditions.whenEnvVar name)

    /// <summary>Adds an environment variable condition that checks both name and value.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// The condition is met only if the environment variable is set and its value matches exactly.
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenEnvVar")>]
    member inline _.whenEnvVar([<InlineIfLambda>] build: BuildStage, name: string, value: string) =
        StageContext.buildStageIsActive build (Conditions.whenEnvVarValue name value)

    /// <summary>Adds a Git branch condition for a single branch name.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/gitBranch/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenBranch")>]
    member inline _.whenBranch([<InlineIfLambda>] build: BuildStage, branch: string) = StageContext.buildStageIsActive build (Conditions.whenBranch branch)

    /// <summary>Adds a Git branch condition that matches any of the specified branch names.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/gitBranch/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenBranches")>]
    member inline _.whenBranches([<InlineIfLambda>] build: BuildStage, branches: string seq) =
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
    member inline _.whenWindows([<InlineIfLambda>] build: BuildStage, ?isTrue: bool) = StageContext.buildStageIsActive build (Conditions.whenPlatform OSPlatform.Windows >> if defaultArg isTrue true then id else not)

    /// <summary>Adds a condition that is met when running on Linux.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenLinux")>]
    member inline _.whenLinux([<InlineIfLambda>] build: BuildStage, ?isTrue: bool) = StageContext.buildStageIsActive build (Conditions.whenPlatform OSPlatform.Linux >> (if defaultArg isTrue true then id else not))

    /// <summary>Adds a condition that is met when running on macOS.</summary>
    /// <remarks>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/conjoin/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/platformNote/*"/>
    /// <include file="../xmldoc/conditions.xml" path="/conditions/ceRestriction/*"/>
    /// </remarks>
    [<CustomOperation("whenOSX")>]
    member inline _.whenOSX([<InlineIfLambda>] build: BuildStage, ?isTrue: bool) = StageContext.buildStageIsActive build (Conditions.whenPlatform OSPlatform.OSX >> (if defaultArg isTrue true then id else not))
    [<CustomOperation>] member inline _.
        whenPlatform
        ([<InlineIfLambda>] build: BuildStage, platform: OSPlatform): BuildStage
        = StageContext.buildStageIsActive build (Conditions.whenPlatform platform)

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
