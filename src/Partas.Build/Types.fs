namespace Partas.Build
open System

[<Struct; RequireQualifiedAccess>]
type Verbosity =
    | Quiet
    | Normal
    | Verbose
    static member Default = Normal

type PipelineCancelledException(msg: string)  = inherit Exception(msg)

type PipelineFailedException =
    inherit Exception
    new(msg: string) = { inherit Exception(msg) }
    new(msg: string, ex: exn) = { inherit Exception(msg, ex) }
type EnvArg =
    {
        Name: string
        Values: string list
        Description: string option
        IsOptional: bool
    }
    static member Create(name: string, ?description, ?values, ?isOptional) = {
        Name = name
        Values = defaultArg values []
        Description = description
        IsOptional = defaultArg isOptional false
    }
    member inline this.WithName name = { this with Name = name }
    member inline this.WithDescription description = { this with Description = description }
    member inline this.WithValues values = { this with Values = values }
    member inline this.WithIsOptional isOptional = { this with IsOptional = isOptional }

/// <summary>Which of a step's two streams a line of output came from.</summary>
[<Struct; RequireQualifiedAccess>]
type StdStream =
    | Out
    | Err

/// <summary>The lines a stage held back, in the order they were written.</summary>
/// <remarks>
/// One capture is shared by every step of the stage that declared it and by its sub-stages, so it locks:
/// steps run in parallel, and a process's two streams are read on two threads of their own.
/// </remarks>
type OutputCapture() =
    let lines = ResizeArray<struct (StdStream * string)>()

    member _.Add(stream, line) = lock lines (fun () -> lines.Add (struct (stream, line)))
    member _.Clear() = lock lines lines.Clear
    member _.IsEmpty = lock lines (fun () -> lines.Count = 0)

    member _.Count = lock lines (fun () -> lines.Count)

    /// <summary>Discards every line after the first <paramref name="count"/>.</summary>
    /// <remarks>A <paramref name="count"/> at or above <see cref="P:Count"/> leaves the capture as it is.</remarks>
    member _.TrimTo(count: int) =
        lock lines (fun () ->
            let count = max 0 count
            if lines.Count > count then lines.RemoveRange (count, lines.Count - count))

    /// Everything written, both streams, interleaved in the order it arrived.
    member _.Lines = lock lines (fun () -> [ for struct (_, line) in lines -> line ])

    /// Only what went to stderr.
    member _.Errors = lock lines (fun () -> [ for struct (stream, line) in lines do if stream = StdStream.Err then yield line ])

    member this.Text = String.Join (Environment.NewLine, this.Lines)
    member this.ErrorText = String.Join (Environment.NewLine, this.Errors)

    /// <summary>What a failure lifts.</summary>
    /// <remarks>
    /// stderr when the process used it, and everything otherwise: a test runner that reports its failures on
    /// stdout is the ordinary case, and lifting only stderr there would lift nothing at all.
    /// </remarks>
    member this.FailureText = if this.Errors.IsEmpty then this.Text else this.ErrorText

/// <summary>Where the output of a stage's steps goes.</summary>
/// <remarks>
/// This is the steps' output only — what the child processes write, and what <c>echo</c> says. The pipeline's
/// own log (the stage rules, the command lines, the timings) always goes to the console; <c>verbosity</c> is
/// what controls that.
/// </remarks>
[<RequireQualifiedAccess>]
type StageOutput =
    | Console
    /// Dropped.
    | Silent
    /// Held, and lifted into the error message if a step fails.
    | Captured of capture: OutputCapture
    /// Handed to a function, line by line, as it arrives.
    | Redirect of write: (StdStream -> string -> unit)

/// <summary>How a stage ended.</summary>
[<Struct; RequireQualifiedAccess>]
type StageOutcome =
    | Succeeded
    /// Inactive: a condition on the stage was false.
    | Skipped
    /// <summary><c>error</c> is the message of the first exception a step raised, and empty where a step
    /// reported its failure through its exit code alone.</summary>
    | Failed of error: string

/// <summary>The wall time of one stage of a run, with how the stage ended.</summary>
/// <remarks><c>Elapsed</c> covers the stage's own steps and every stage nested under them.</remarks>
type StageTiming = {
    Name: string
    /// The number of stages enclosing this one; 0 for a stage of the pipeline itself.
    Depth: int
    Elapsed: TimeSpan
    Outcome: StageOutcome
}

/// <summary>The stages a pipeline run has finished, as the tree the stages nest into.</summary>
/// <remarks>
/// Stages append concurrently under <c>parallel'</c>. <c>Start</c> supplies the ordinal that orders a stage
/// among its siblings and that its own sub-stages record as their parent; a stage of the pipeline records
/// <c>0L</c>.
/// </remarks>
type StageTimings() =
    let entries = System.Collections.Concurrent.ConcurrentBag<struct (int64 * int64 * StageTiming)>()
    let mutable started = 0L

    /// The ordinal of the stage starting now.
    member _.Start() = System.Threading.Interlocked.Increment &started

    member _.Add(parent: int64, order: int64, timing: StageTiming) = entries.Add (struct (parent, order, timing))

    /// Discards every recorded stage. A second run of the same pipeline value reports itself alone.
    member _.Clear() =
        // netstandard2.0's ConcurrentBag has no Clear.
        let mutable entry = Unchecked.defaultof<struct (int64 * int64 * StageTiming)>
        while entries.TryTake &entry do ()

    /// <summary>Every recorded stage in pre-order, each sub-stage under the stage containing it.</summary>
    /// <remarks>Siblings read in start order, which <c>parallel'</c> makes nondeterministic.</remarks>
    member _.Ordered =
        let children =
            entries
            |> List.ofSeq
            |> List.groupBy (fun struct (parent, _, _) -> parent)
            |> List.map (fun (parent, siblings) -> parent, siblings |> List.sortBy (fun struct (_, order, _) -> order))
            |> Map.ofList

        let rec walk parent = [
            match Map.tryFind parent children with
            | None -> ()
            | Some siblings ->
                for struct (_, order, timing) in siblings do
                    timing
                    yield! walk order
        ]

        walk 0L

[<Struct>]
type InputSpec<'T> = { Inputs: ActionInput list; Read: CommandLine.ParseResult -> 'T }

namespace Partas.Build.Internal

open System
open Partas.Build

type StepSoftCancelledException(msg: string) = inherit Exception(msg)
type StageSoftCancelledException(msg: string) = inherit Exception(msg)

[<Measure>] type stepIndex
type StepIndex = int<stepIndex>

type [<Struct; RequireQualifiedAccess>]
    Step =
    /// <summary>A step, and the command line it prints as when there is one.</summary>
    /// <remarks>
    /// <c>--explain</c> renders the label. It is populated for the <c>run</c> overloads whose command is
    /// available at construction — a literal command line, a <c>Cmd</c>, or <c>runSensitive</c>'s interpolated
    /// string — and empty for the overloads taking a function. <c>--explain</c> renders an empty label as the
    /// step's index.
    /// <para>Labels come from <c>Cmd.toLogString</c>: secrets are masked.</para>
    /// </remarks>
    | StepFn of label: string voption * fn: (StageContext -> StepIndex -> Async<Result<unit, string>>)
    | StepOfStage of stage: StageContext

/// <summary>
/// Stage can nest in a pipeline or another stage.
/// </summary>
and [<Struct; RequireQualifiedAccess>]
    StageParent =
    | Stage of stage: StageContext
    | Pipeline of pipeline: PipelineContext

and [<Struct; RequireQualifiedAccess>]
    StageIndex =
    | Step of step: int
    | Stage of stage: int
    | Condition

/// <summary>One condition conjoined into a stage's <c>IsActive</c>, with the text that attributes a skip to it.</summary>
/// <remarks><c>Reason</c> is <c>ValueNone</c> for <c>when'</c>, whose <c>bool</c> argument leaves nothing to report.</remarks>
and [<Struct>] StageCondition = {
    Reason: string voption
    Predicate: StageContext -> bool
}

and StageContext = {
    Id: int
    Name: string
    Verbosity: Verbosity voption
    IsActive: StageContext -> bool
    /// <summary>Every condition conjoined into <c>IsActive</c>, in the order the stage declared them.</summary>
    /// <remarks>
    /// <c>--explain</c> reads this to say <em>why</em> a stage is skipped. <c>IsActive</c> remains the authority
    /// on <em>whether</em> it is: re-evaluating a predicate here repeats whatever IO it performs.
    /// </remarks>
    Conditions: StageCondition list
    IsParallel: StageContext -> int voption
    ContinueStepsOnFailure: bool
    ContinueStageOnFailure: bool
    Timeout: TimeSpan voption
    TimeoutForStep: TimeSpan voption
    /// <summary>How many further attempts the stage's steps get after a failing one; <c>0</c> gives a single attempt.</summary>
    /// <remarks>
    /// <c>Timeout</c> is the budget for the whole stage, retries included. A stage that exhausts it stops with
    /// attempts remaining. A failure lifts the last attempt's output; under a <c>parallel'</c> ancestor whose
    /// capture the stage shares, it lifts every attempt together.
    /// </remarks>
    Retry: int
    WorkingDir: string voption
    EnvVars: Map<string, string>
    AcceptableExitCodes: Set<int>
    FailIfIgnored: bool
    FailIfNoActiveSubStage: bool
    NoPrefixForStep: bool
    NoStdRedirectForStep: bool
    Output: StageOutput voption
    ShuffleExecuteSequence: bool
    ParentContext: StageParent voption
    /// <summary>The ordinal this stage took from the pipeline's <c>StageTimings</c> when it started.</summary>
    /// <remarks>
    /// Set on the value the stage's sub-stages take as their parent, so that a timing records whose child it
    /// is. <c>ValueNone</c> before the stage runs, and throughout a condition stage, whose stages belong to the
    /// condition rather than to the run.
    /// </remarks>
    TimingOrder: int64 voption
    Steps: Step list
}
and PipelineContext = {
    Name: string
    Description: string voption
    Verbosity: Verbosity voption
    Verify: PipelineContext -> bool
    EnvVars: Map<string, string>
    AcceptableExitCodes: Set<int>
    Timeout: TimeSpan voption
    TimeoutForStep: TimeSpan voption
    TimeoutForStage: TimeSpan voption
    WorkingDir: string voption
    NoPrefixForStep: bool
    NoStdRedirectForStep: bool
    Output: StageOutput voption
    Stages: StageContext list
    PostStages: StageContext list
    RunBeforeEachStage: StageContext -> unit
    RunAfterEachStage: StageContext -> unit
    /// <summary>What each stage of the run took, filled in as the stages finish.</summary>
    /// <remarks>
    /// A nested stage records itself here too, reaching the pipeline through <c>ParentContext</c>. A condition
    /// stage belongs to the condition that runs it, and the summary covers the stages of the run itself.
    /// </remarks>
    Timings: StageTimings
}

type BuildPipeline = PipelineContext -> PipelineContext
type BuildConditions = (StageContext -> bool) list -> (StageContext -> bool) list
type BuildStage = StageContext -> StageContext
type BuildStageIsActive = StageContext -> bool
type BuildStep = StageContext -> StepIndex -> Async<Result<unit, string>>
type BuildEnvInfo = EnvArg -> EnvArg

open System.CommandLine

/// <summary>What a command is, before it becomes a <c>System.CommandLine.Command</c>.</summary>
/// <remarks>
/// The options a command registers are not listed here: they are read off <c>Pipelines</c>, whose
/// <c>InputSpec.Inputs</c> are reachable without a <c>ParseResult</c>. <c>ExtraInputs</c> holds only what is
/// declared on the command directly — inputs no pipeline asks for, such as a root's global flags.
/// </remarks>
type CommandSpec = {
    Name: string
    /// <summary>The name a root command calls itself in help and usage text.</summary>
    /// <remarks>
    /// Unrelated to <c>Name</c>, which is the subcommand's invocation token. Only a root command reads this
    /// field; it starts <c>ValueNone</c> and is set by the <c>name</c> custom operation, available there alone.
    /// </remarks>
    DisplayName: string voption
    Description: string voption
    PipelineDefaults: BuildPipeline
    Aliases: string list
    Hidden: bool
    ExtraInputs: ActionInput list
    Pipelines: InputSpec<PipelineContext> list
    SubCommands: Command list
    ParserConfiguration: ParserConfiguration voption
    InvocationConfiguration: InvocationConfiguration voption
}

type BuildCommand = CommandSpec -> CommandSpec

module StageContext =
    let create name = {
            Id = Random().Next()
            Name = name
            IsActive = fun _ -> true
            Conditions = []
            IsParallel = fun _ -> ValueNone
            ContinueStepsOnFailure = false
            ContinueStageOnFailure = false
            Timeout = ValueNone
            TimeoutForStep = ValueNone
            Retry = 0
            WorkingDir = ValueNone
            Verbosity = ValueNone
            EnvVars = Map.empty
            AcceptableExitCodes = Set [| 0 |]
            FailIfIgnored = false
            FailIfNoActiveSubStage = false
            NoPrefixForStep = true
            NoStdRedirectForStep = false
            Output = ValueNone
            ShuffleExecuteSequence = false
            ParentContext = ValueNone
            TimingOrder = ValueNone
            Steps = []
        }
    let inline mapParentContext ifNone ([<InlineIfLambda>] mapPipe) ([<InlineIfLambda>] mapStage) ctx =
        match ctx.ParentContext with
        | ValueNone -> ifNone
        | ValueSome(StageParent.Pipeline pipeline) -> mapPipe pipeline
        | ValueSome(StageParent.Stage stage) -> mapStage stage
    let inline mapStageParentContext ifNoParentStage ([<InlineIfLambda>] mapStage) = mapParentContext ifNoParentStage (fun _ -> ifNoParentStage) mapStage
    let inline mapPipelineParentContext ifNoParentPipeline ([<InlineIfLambda>] mapPipeline) = mapParentContext ifNoParentPipeline mapPipeline (fun _ -> ifNoParentPipeline)

    let rec getParentPipeline ctx = mapParentContext None Some getParentPipeline ctx

    let rec getNamePath ctx =
        mapStageParentContext "" (getNamePath >> sprintf "%s/") ctx
        + ctx.Name

    /// The number of stages enclosing <paramref name="ctx"/>; 0 for a stage of a pipeline.
    let rec getDepth ctx = mapStageParentContext 0 (getDepth >> (+) 1) ctx

    /// <summary>The timing ordinal of the stage enclosing <paramref name="ctx"/>; <c>0L</c> for a stage of a
    /// pipeline.</summary>
    /// <remarks><c>ValueNone</c> where the enclosing stage holds no ordinal, which is every stage under a
    /// condition stage.</remarks>
    let getParentTimingOrder ctx = mapParentContext ValueNone (fun _ -> ValueSome 0L) _.TimingOrder ctx

    let tryGetEnvVar (stage: StageContext) (name: string) =
        stage.EnvVars
        |> Map.tryFind name
        |> Option.toValueOption
        |> ValueOption.orElse (
            match Environment.GetEnvironmentVariable(name) with
            | null -> ValueNone
            | value -> ValueSome value
            )

    let rec getNoPrefixForStep (stage: StageContext) =
        match stage.ParentContext with
        | ValueNone -> stage.NoPrefixForStep
        | _ when stage.NoPrefixForStep -> true
        | ValueSome(StageParent.Pipeline pipeline) -> pipeline.NoPrefixForStep
        | ValueSome(StageParent.Stage parentStage) -> getNoPrefixForStep parentStage

    let rec getNoStdRedirectForStep (ctx: StageContext) =
        match ctx.ParentContext with
        | ValueNone -> ctx.NoStdRedirectForStep
        | _ when ctx.NoStdRedirectForStep -> true
        | ValueSome(StageParent.Pipeline pipeline) -> pipeline.NoStdRedirectForStep
        | ValueSome(StageParent.Stage parentStage) -> getNoStdRedirectForStep parentStage

    /// <summary>Where this stage's step output goes, taking the nearest declaration walking upward.</summary>
    /// <remarks><c>ValueNone</c> means nobody asked for anything, which is <c>StageOutput.Console</c>.</remarks>
    let rec getOutput (ctx: StageContext) =
        ctx.Output
        |> ValueOption.orElseWith (fun () -> mapParentContext ValueNone _.Output getOutput ctx)

    /// The capture this stage writes into, if that is where its output goes.
    let tryGetCapture (ctx: StageContext) =
        match getOutput ctx with
        | ValueSome(StageOutput.Captured capture) -> ValueSome capture
        | _ -> ValueNone

    /// <summary>The capture into which this stage is the only writer while it runs.</summary>
    /// <remarks>
    /// A stage's own capture always qualifies, and an inherited one qualifies while every stage between the two
    /// runs its sub-stages sequentially. Under a <c>parallel'</c> ancestor a sibling writes into the same capture
    /// at the same time, and the answer is <c>ValueNone</c>.
    /// </remarks>
    let tryGetOwnCapture (ctx: StageContext) =
        match ctx.Output with
        | ValueSome(StageOutput.Captured capture) -> ValueSome capture
        | ValueSome _ -> ValueNone
        | ValueNone ->
            match tryGetCapture ctx with
            | ValueNone -> ValueNone
            | ValueSome capture ->
                let rec sharesWithSiblings (child: StageContext) =
                    mapParentContext
                        false
                        (fun _ -> false)
                        (fun parent -> (parent.IsParallel parent).IsSome || (parent.Output.IsNone && sharesWithSiblings parent))
                        child
                if sharesWithSiblings ctx then ValueNone else ValueSome capture

    /// <summary>Writes one line of step output wherever <see cref="M:getOutput"/> says it belongs.</summary>
    /// <remarks>
    /// The way for a step to emit something the stage can suppress or capture. A bare <c>printfn</c> goes to
    /// the console whatever the stage says, because nothing routes it.
    /// </remarks>
    let writeLine (ctx: StageContext) (stream: StdStream) (line: string) =
        match getOutput ctx with
        // Both streams merged onto stdout, as they were before there was anywhere else to put them.
        | ValueNone | ValueSome StageOutput.Console -> Console.WriteLine line
        | ValueSome StageOutput.Silent -> ()
        | ValueSome(StageOutput.Captured capture) -> capture.Add (stream, line)
        | ValueSome(StageOutput.Redirect write) -> write stream line

    let rec buildEnvVars (ctx: StageContext) =
        mapParentContext Map.empty _.EnvVars buildEnvVars ctx
        |> Map.foldBack Map.add ctx.EnvVars

    let rec getVerbosity (ctx: StageContext) =
        let stageVerbosity = defaultValueArg ctx.Verbosity Verbosity.Default
        match ctx.ParentContext with
        | ValueNone -> stageVerbosity
        | ValueSome(StageParent.Pipeline pipeline) -> defaultValueArg pipeline.Verbosity stageVerbosity
        | ValueSome(StageParent.Stage parentStage) when parentStage.Verbosity.IsSome -> getVerbosity parentStage
        | _ -> stageVerbosity

    let rec buildCurrentStepPrefix (ctx: StageContext) =
        let mutable isSubStage = false
        let prefix = ctx |> mapStageParentContext "" (
            fun parentStage ->
                let postfix =
                    parentStage.Steps
                    |> List.tryFindIndex (function
                        | Step.StepOfStage step -> step.Id = ctx.Id
                        | _ -> false
                        )
                    |> Option.defaultValue 0
                    |> string
                isSubStage <- true
                $"%s{buildCurrentStepPrefix parentStage}/step-%s{postfix}"
            )
        if String.IsNullOrEmpty prefix then ctx.Name
        elif isSubStage then $"%s{prefix}-%s{ctx.Name}"
        else $"%s{prefix}/%s{ctx.Name}"

    let inline buildStepPrefix (ctx: StageContext) (index: int<stepIndex>) = $"%s{buildCurrentStepPrefix ctx}/step-%i{index}"

    let buildIndent (ctx: StageContext) (margin: int) = String(' ', (getNamePath ctx).Length - ctx.Name.Length + margin)
    let buildDefaultIndent (ctx: StageContext) = buildIndent ctx 4

    open Spectre.Console

    /// <summary>Percent-encodes a message for a GitHub Actions workflow command.</summary>
    /// <remarks>
    /// A workflow command ends at the first newline, so a multi-line message — which is exactly what a
    /// captured failure lifts — loses everything after its first line unless it arrives encoded.
    /// </remarks>
    let encodeWorkflowData (msg: string) =
        msg.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A")

    let printError (stage: StageContext) (msg: string) =
        match tryGetEnvVar stage "GITHUB_ENV" with
        | ValueSome _ ->
            getNamePath stage
            |> _.Replace(",", "_")
            |> (+) "[STAGE] "
            |> fun title -> $"::error title={title}::{encodeWorkflowData msg}"
            |> AnsiConsole.WriteLine
        | _ ->
            AnsiConsole.MarkupLineInterpolated $"""[red]Error: {msg}[/]"""

    let isAcceptableExitCode (stage: StageContext) exitCode =
        Set.contains exitCode stage.AcceptableExitCodes
        || mapParentContext Set.empty _.AcceptableExitCodes _.AcceptableExitCodes stage
           |> Set.contains exitCode
    let mapExitCodeToResult (stage: StageContext) exitCode =
        if isAcceptableExitCode stage exitCode then Ok() else Error "Exit code not acceptable."

module PipelineContext =
    open Spectre.Console

    /// The `RunBeforeEachStage`/`RunAfterEachStage` a freshly created pipeline carries. Named rather than
    /// inlined as `ignore` so that `applyDefaults` can tell "no hook" from "a hook that happens to do nothing"
    /// by reference.
    let internal noStageHook: StageContext -> unit = ignore
    /// The `Verify` a freshly created pipeline carries. Named for the same reason as `noStageHook`.
    let internal alwaysVerify: PipelineContext -> bool = fun _ -> true

    let create (name: string): PipelineContext =
        let envVars =
            seq {
                for key in Environment.GetEnvironmentVariables().Keys ->
                try
                    string key, Environment.GetEnvironmentVariable(string key)
                with _ -> string key, ""
            }
            |> Map.ofSeq
        {
            Name = name
            Description = ValueNone
            Verbosity = ValueNone
            Verify = alwaysVerify
            EnvVars = envVars
            AcceptableExitCodes = set [ 0 ]
            Timeout = ValueNone
            TimeoutForStep = ValueNone
            TimeoutForStage = ValueNone
            WorkingDir = ValueNone
            NoPrefixForStep = true
            NoStdRedirectForStep = false
            Output = ValueNone
            Stages = []
            PostStages = []
            RunBeforeEachStage = noStageHook
            RunAfterEachStage = noStageHook
            Timings = StageTimings()
        }

    /// <summary>Fills in the settings a pipeline left alone with those a command supplies as defaults.</summary>
    /// <remarks>
    /// A command's pipeline-level operations are *defaults*, not overrides: whatever the pipeline set for
    /// itself wins. "Left alone" is decided against a pristine `PipelineContext.create`, which is the only
    /// baseline available once a pipeline is a finished value - `ValueNone` for the optional settings, the
    /// ambient environment for `EnvVars`, `set [0]` for the exit codes, `noStageHook` for the hooks, and the
    /// record's own literals for the flags. Stages are never touched.
    /// </remarks>
    let applyDefaults (defaults: BuildPipeline) (ctx: PipelineContext): PipelineContext =
        let pristine = create ctx.Name
        let defaulted = defaults pristine
        let inline orDefault (pipelineValue: 'T voption) (defaultValue: 'T voption) =
            if pipelineValue.IsSome then pipelineValue else defaultValue
        let inline orDefaultIfUntouched (pipelineValue: 'T) (pristineValue: 'T) (defaultValue: 'T) =
            if pipelineValue = pristineValue then defaultValue else pipelineValue
        {
            ctx with
                Description = orDefault ctx.Description defaulted.Description
                Verbosity = orDefault ctx.Verbosity defaulted.Verbosity
                Timeout = orDefault ctx.Timeout defaulted.Timeout
                TimeoutForStep = orDefault ctx.TimeoutForStep defaulted.TimeoutForStep
                TimeoutForStage = orDefault ctx.TimeoutForStage defaulted.TimeoutForStage
                WorkingDir = orDefault ctx.WorkingDir defaulted.WorkingDir
                Output = orDefault ctx.Output defaulted.Output
                NoPrefixForStep = orDefaultIfUntouched ctx.NoPrefixForStep pristine.NoPrefixForStep defaulted.NoPrefixForStep
                NoStdRedirectForStep =
                    orDefaultIfUntouched ctx.NoStdRedirectForStep pristine.NoStdRedirectForStep defaulted.NoStdRedirectForStep
                AcceptableExitCodes =
                    orDefaultIfUntouched ctx.AcceptableExitCodes pristine.AcceptableExitCodes defaulted.AcceptableExitCodes
                // Per key, since the pipeline's map starts as the whole ambient environment: a key the pipeline
                // did not touch still reads back whatever the process was started with.
                EnvVars =
                    defaulted.EnvVars
                    |> Map.fold
                        (fun envVars key value ->
                            if Map.tryFind key ctx.EnvVars = Map.tryFind key pristine.EnvVars
                            then Map.add key value envVars
                            else envVars)
                        ctx.EnvVars
                PostStages = if List.isEmpty ctx.PostStages then defaulted.PostStages else ctx.PostStages
                RunBeforeEachStage =
                    if obj.ReferenceEquals(ctx.RunBeforeEachStage, noStageHook) then defaulted.RunBeforeEachStage else ctx.RunBeforeEachStage
                RunAfterEachStage =
                    if obj.ReferenceEquals(ctx.RunAfterEachStage, noStageHook) then defaulted.RunAfterEachStage else ctx.RunAfterEachStage
                Verify = if obj.ReferenceEquals(ctx.Verify, alwaysVerify) then defaulted.Verify else ctx.Verify
        }

    let findStageByName (ctx: PipelineContext) (name: string) =
        ctx.PostStages
        |> List.append ctx.Stages
        |> List.tryFind _.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        |> Option.toValueOption

    let makeVerificationStage (ctx: PipelineContext) =
        { StageContext.create "" with ParentContext = ValueSome(StageParent.Pipeline ctx) }

    let printError (ctx: PipelineContext) (msg: string) =
        if
            ctx.EnvVars
            |> Map.containsKey "GITHUB_ENV"
        then
            (ctx.Name.Replace(",", "_"), StageContext.encodeWorkflowData msg)
            ||> sprintf "::error title=[PIPELINE] %s::%s"
            |> AnsiConsole.WriteLine
        else
            AnsiConsole.MarkupLineInterpolated $"[red]Error: {msg}[/]"

    // let inline buildPipelineVerification ([<InlineIfLambda>] build: BuildPipeline) ([<InlineIfLambda>] conditionFn: BuildStageIsActive): BuildPipeline = fun ctx ->
    //     let newCtx = build ctx
    //     // TODO
    //     {
    //         newCtx with
    //             Verify = fun ctx -> false
    //     }

module SpectreConsoleExt =
    open Spectre.Console
    module Markup =
        let inline escape (str: string) = Markup.Escape str
        let inline red (str: string): string = $"[red]{str}[/]"
        let inline turquoise2 (str: string): string = $"[turquoise2]{str}[/]"
        let inline turquoise4 (str: string): string = $"[turquoise4]{str}[/]"
        let inline bold (str: string): string = $"[bold]{str}[/]"
        let inline grey (str: string): string = $"[grey50]{str}[/]"
        let inline yellow (str: string): string = $"[yellow]{str}[/]"
        let inline green (str: string): string = $"[green]{str}[/]"
        let inline lime (str: string): string = $"[lime]{str}[/]"

    type SRTPHelper =
        static member inline getVerbosity (verbosity: Verbosity) = verbosity
        static member inline getVerbosity (stageContext: StageContext) = StageContext.getVerbosity stageContext
        static member inline getVerbosity (pipelineContext: PipelineContext) = defaultValueArg pipelineContext.Verbosity Verbosity.Default
        static member inline write(str: Rule): unit = AnsiConsole.Write(str)
        static member inline write(str: FigletText): unit = AnsiConsole.Write(str)
        static member inline write(str: string): unit = AnsiConsole.Markup(str)
        static member inline writen(renderable: Rule) = AnsiConsole.Write(renderable); AnsiConsole.WriteLine()
        static member inline writen(renderable: FigletText) = AnsiConsole.Write(renderable); AnsiConsole.WriteLine()
        static member inline writen(str: string) = AnsiConsole.MarkupLine(str)
    let inline getVerbosity value = ((^T or SRTPHelper):(static member getVerbosity: ^T -> Verbosity) value)
    type private Printable<^T when (^T or SRTPHelper):(static member write: ^T -> unit) and (^T or SRTPHelper):(static member writen: ^T -> unit)> = ^T
    let inline print (message: ^T when Printable<^T>) = ((^T or SRTPHelper):(static member write: ^T -> unit) message)
    let inline printn (message: ^T when Printable<^T>) = ((^T or SRTPHelper):(static member writen: ^T -> unit) message)
    let inline nprint value (message: ^T when Printable<^T>) = if (getVerbosity value).IsQuiet |> not then print message
    let inline nprintn value (message: ^T when Printable<^T>) = if (getVerbosity value).IsQuiet |> not then printn message
    let inline vprint value (message: ^T when Printable<^T>) = if (getVerbosity value).IsVerbose then print message
    let inline vprintn value (message: ^T when Printable<^T>) = if (getVerbosity value).IsVerbose then printn message
    let inline line () = AnsiConsole.WriteLine()
    let inline vline value = if (getVerbosity value).IsVerbose then AnsiConsole.WriteLine()
    let inline nline value = if (getVerbosity value).IsQuiet |> not then AnsiConsole.WriteLine()
    let inline withVerbose fn value = if (getVerbosity value).IsVerbose then fn()
    let inline withNormal fn value = if (getVerbosity value).IsQuiet |> not then fn()

namespace Partas.Build

open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Partas.Build.Internal
open System.Net.Http
open Spectre.Console
open SpectreConsoleExt

module InputSpec =
    /// Concatenates input sets, keeping the first occurrence of each input.
    /// <c>ActionInput</c> has no custom equality, so this compares by reference: the same <c>let</c>-bound
    /// option declared by two specs collapses to one, while two separately created options do not.
    let union (inputs: ActionInput list list) = inputs |> List.concat |> List.distinct
    let inline ret v = { Inputs = []; Read = fun _ -> v }
    let map f s = { Inputs = s.Inputs; Read = s.Read >> f }
    let map2 f a b = { Inputs = union [ a.Inputs; b.Inputs ]; Read = fun pr -> f (a.Read pr) (b.Read pr) }
    let ofInput (input: ActionInput<'T>) = { Inputs = [ input :> ActionInput ]; Read = input.GetValue }
    /// Collapses a sequence of specs into one spec of a list, unioning their inputs.
    /// This is what lets a collection of ready-made blocks - a stage per project, say - be yielded as a unit.
    let sequence (specs: InputSpec<'T> seq) =
        let specs = List.ofSeq specs
        { Inputs = union [ for spec in specs -> spec.Inputs ]; Read = fun pr -> specs |> List.map (fun spec -> spec.Read pr) }
    /// <c>sequence</c> over the results of mapping <c>fn</c>, for <c>for x in xs do</c> over an input-declaring body.
    let traverse (fn: 'T -> InputSpec<'U>) (items: 'T seq) = items |> Seq.map fn |> sequence

module CommandSpec =
    let create (name: string) = {
        Name = name
        DisplayName = ValueNone
        Description = ValueNone
        PipelineDefaults = id
        Aliases = []
        Hidden = false
        ExtraInputs = []
        Pipelines = []
        SubCommands = []
        ParserConfiguration = ValueNone
        InvocationConfiguration = ValueNone
    }

    /// Every input the command has to register, in declaration order: those declared on the command
    /// itself, then those harvested from its pipelines. Deduplicated, so an option two pipelines
    /// (or two stages) ask for is registered once.
    let inputs (spec: CommandSpec) = InputSpec.union (spec.ExtraInputs :: [ for pipeline in spec.Pipelines -> pipeline.Inputs ])

module StageContext =
    let rec getStageLevel (ctx: StageContext) = StageContext.mapStageParentContext 0 (getStageLevel >> (+) 1) ctx

    let rec getWorkingDir (ctx: StageContext) =
        ctx.WorkingDir
        |> ValueOption.orElse (
            StageContext.mapParentContext ValueNone _.WorkingDir getWorkingDir ctx
            )

    let rec getTimeoutForStage (ctx: StageContext) =
        ctx.Timeout
        |> ValueOption.map (_.TotalMilliseconds >> int)
        |> ValueOption.orElseWith (fun _ ->
            StageContext.mapParentContext
                ValueNone
                (_.TimeoutForStage >> ValueOption.map (_.TotalMilliseconds >> int))
                (getTimeoutForStage >> ValueSome)
                ctx
            )
        |> ValueOption.defaultValue -1

    let rec getTimeoutForStep (ctx: StageContext) =
        ctx.TimeoutForStep
        |> ValueOption.map (_.TotalMilliseconds >> int)
        |> ValueOption.orElseWith(fun _ ->
            ctx
            |> StageContext.mapParentContext
                ValueNone
                (_.TimeoutForStep >> ValueOption.map (_.TotalMilliseconds >> int))
                (getTimeoutForStep >> ValueSome)
            )
        |> ValueOption.defaultValue -1

    let rec getAllEnvVars (ctx: StageContext) =
        StageContext.mapParentContext Map.empty _.EnvVars getAllEnvVars ctx
        |> fun envVars -> Map.fold (fun s k v -> Map.add k v s) envVars ctx.EnvVars

    let rec tryGetEnvVar (ctx: StageContext) (key: string) =
        ctx.EnvVars
        |> Map.tryFind key
        |> ValueOption.ofOption
        |> ValueOption.orElseWith(fun _ ->
            ctx
            |> StageContext.mapParentContext
                   ValueNone
                   (_.EnvVars >> Map.tryFind key >> ValueOption.ofOption)
                   (tryGetEnvVar >> fun fn -> fn key)
            )

    let inline getEnvVar (ctx: StageContext) (key: string) = tryGetEnvVar ctx key |> ValueOption.defaultValue ""

    let softCancelStep (_: StageContext) =
        "Step is soft cancelled."
        |> StepSoftCancelledException
        |> raise

    let softCancelStage (_: StageContext) =
        "Stage is soft cancelled."
        |> StageSoftCancelledException
        |> raise


    let runHttpHealthCheckCancelableWithConfigRequest
        (ctx: StageContext)
        (cancellationToken: System.Threading.CancellationToken)
        (configRequest: HttpRequestMessage -> unit)
        (url: string): Async<Result<unit, string>> = asyncResult {
        use client = new HttpClient()
        let mutable shouldContinue = true
        while shouldContinue && not cancellationToken.IsCancellationRequested do
            try
                Markup.escape url
                |> sprintf "Check %s ..."
                |> Markup.yellow
                |> vprintn ctx

                use message = new HttpRequestMessage(HttpMethod.Get, url)
                configRequest message
                let! result = client.SendAsync(message, cancellationToken = cancellationToken) |> AsyncResult.ofTask |> AsyncResult.mapError _.Message
                shouldContinue <- not result.IsSuccessStatusCode
            with
            | :? TaskCanceledException when cancellationToken.IsCancellationRequested -> shouldContinue <- false
            | ex ->
                Markup.escape ex.Message
                |> sprintf "Health check failed: %s"
                |> Markup.red
                |> printn

            do! Async.Sleep 1000 |> Async.map Ok
        if cancellationToken.IsCancellationRequested
        then do! AsyncResult.error "Health check is cancelled."
        else
            Markup.escape $"{url} is healthy!"
            |> Markup.green
            |> nprintn ctx
    }

    let inline runHttpHealthCheckCancelable ctx cancellationToken url =
        runHttpHealthCheckCancelableWithConfigRequest ctx cancellationToken ignore url
    let inline runHttpHealthCheckWithConfigRequest ctx configRequest url =
        runHttpHealthCheckCancelableWithConfigRequest ctx System.Threading.CancellationToken.None configRequest url
    let inline runHttpHealthCheck (ctx: StageContext) (url: string) =
        runHttpHealthCheckCancelableWithConfigRequest ctx System.Threading.CancellationToken.None ignore url

    /// Conjoins a condition onto a stage and records <paramref name="reason"/> against it.
    /// The single writer of both <c>IsActive</c> and <c>Conditions</c>, so the two stay in step.
    let inline addPredicateBecause (reason: string voption) ([<InlineIfLambda>] condition: BuildStageIsActive) (stage: StageContext) =
        { stage with
            IsActive = fun ctx -> stage.IsActive ctx && condition ctx
            Conditions = stage.Conditions @ [ { Reason = reason; Predicate = condition } ] }

    /// Conjoins a condition onto the stage a <c>BuildStage</c> produces, recording <paramref name="reason"/> against it.
    /// Conditions accumulate rather than replace, so a stage declaring several is active only when all hold.
    let inline buildStageIsActiveBecause
        (reason: string voption)
        ([<InlineIfLambda>] build: BuildStage)
        ([<InlineIfLambda>] conditionFn: BuildStageIsActive)
        : BuildStage
        = fun ctx -> addPredicateBecause reason conditionFn (build ctx)

    /// Conjoins a condition onto the stage a <c>BuildStage</c> produces.
    /// Conditions accumulate rather than replace, so a stage declaring several is active only when all hold.
    /// <c>--explain</c> reports a skip caused by this condition without a reason.
    let inline buildStageIsActive ([<InlineIfLambda>] build: BuildStage) ([<InlineIfLambda>] conditionFn: BuildStageIsActive): BuildStage =
        buildStageIsActiveBecause ValueNone build conditionFn

    let inline addStep (step: Step) (stage: StageContext) = { stage with Steps = stage.Steps @ [ step ] }
    let inline addSteps (steps: Step seq) (stage: StageContext) = { stage with Steps = stage.Steps @ (steps |> Seq.toList) }
    let inline addSubStage (subStage: StageContext) stage = addStep (Step.StepOfStage subStage) stage
    let inline addSubStages (subStages: StageContext seq) stage = addSteps (subStages |> Seq.map Step.StepOfStage) stage
    let inline addBuildStep ([<InlineIfLambda>] step: BuildStep) stage = addStep (Step.StepFn(ValueNone, step)) stage
    let inline addBuildSteps (steps: BuildStep seq) stage = addSteps (steps |> Seq.map (fun step -> Step.StepFn(ValueNone, step))) stage
    let addStepFn = addBuildStep
    let inline addLabelledStepFn (label: string) ([<InlineIfLambda>] step: BuildStep) stage = addStep (Step.StepFn(ValueSome label, step)) stage
    /// Conjoins a condition onto a stage. <c>--explain</c> reports a skip caused by it without a reason.
    let inline addPredicate ([<InlineIfLambda>] condition: BuildStageIsActive) stage = addPredicateBecause ValueNone condition stage
    let inline addEnvVars (kvs: seq<string * string>) (stage: StageContext) = { stage with EnvVars = kvs |> Seq.fold (fun state (k, v) -> Map.add k v state) stage.EnvVars }
    let inline setAcceptableExitCodes (codes: int seq) (stage: StageContext) = { stage with AcceptableExitCodes = codes |> Set.ofSeq }
    let inline setFailIfIgnored (failIfIgnored: bool) (stage: StageContext) = { stage with FailIfIgnored = failIfIgnored }
    let inline setFailIfNoActiveSubStage (failIfNoActiveSubStage: bool) (stage: StageContext) = { stage with FailIfNoActiveSubStage = failIfNoActiveSubStage }
    let inline setContinueStepsOnFailure (continueStepsOnFailure: bool) (stage: StageContext) = { stage with ContinueStepsOnFailure = continueStepsOnFailure }
    let inline setContinueStageOnFailure (continueStageOnFailure: bool) (stage: StageContext) = { stage with ContinueStageOnFailure = continueStageOnFailure }
    let inline setContinueOnStepFailure (continueOnStepFailure: bool) (stage: StageContext) = { stage with ContinueStepsOnFailure = continueOnStepFailure; ContinueStageOnFailure = continueOnStepFailure }
    let inline setTimeoutTimeSpan (timeout: System.TimeSpan voption) (stage: StageContext) = { stage with Timeout = timeout }
    let inline setTimeoutSeconds (timeout: int voption) (stage: StageContext) = { stage with Timeout = timeout |> ValueOption.map (fun ms -> System.TimeSpan.FromSeconds(float ms)) }
    let inline setTimeoutMilliseconds (timeout: int voption) (stage: StageContext) = { stage with Timeout = timeout |> ValueOption.map (fun ms -> System.TimeSpan.FromMilliseconds(float ms)) }
    let inline setTimeoutForStepTimeSpan (timeout: System.TimeSpan voption) (stage: StageContext) = { stage with TimeoutForStep = timeout }
    let inline setTimeoutForStepSeconds (timeout: int voption) (stage: StageContext) = { stage with TimeoutForStep = timeout |> ValueOption.map (fun ms -> System.TimeSpan.FromSeconds(float ms)) }
    let inline setTimeoutForStepMilliseconds (timeout: int voption) (stage: StageContext) = { stage with TimeoutForStep = timeout |> ValueOption.map (fun ms -> System.TimeSpan.FromMilliseconds(float ms)) }
    let inline toggleParallel (toggle: bool) (stage: StageContext) = { stage with IsParallel = fun _ -> if toggle then ValueSome -1 else ValueNone }
    let inline setParallelism (parallelism: int) (stage: StageContext) = { stage with IsParallel = fun _ -> ValueSome parallelism }
    let inline setWorkingDir (workingDir: string voption) (stage: StageContext) = { stage with WorkingDir = workingDir }
    let inline setNoPrefixForStep (noPrefixForStep: bool) (stage: StageContext) = { stage with NoPrefixForStep = noPrefixForStep }
    let inline setNoStdRedirectForStep (noStdRedirectForStep: bool) (stage: StageContext) = { stage with NoStdRedirectForStep = noStdRedirectForStep }
    let inline setOutput (output: StageOutput voption) (stage: StageContext) = { stage with Output = output }
    let inline setShuffleExecuteSequence (shuffleExecuteSequence: bool) (stage: StageContext) = { stage with ShuffleExecuteSequence = shuffleExecuteSequence }
    let inline setSteps (steps: Step list) (stage: StageContext) = { stage with Steps = steps }

module BuildStage =
    let inline merge ([<InlineIfLambda>] firstStage: BuildStage) ([<InlineIfLambda>] secondStage: BuildStage) = firstStage >> secondStage
    let inline mergeMany (stages: BuildStage seq) = Seq.reduce merge stages
    let inline mergeManyWith ([<InlineIfLambda>] mergeFn: BuildStage -> BuildStage -> BuildStage) (stages: BuildStage seq) = Seq.reduce mergeFn stages
    let inline addEnvVars (kvs: seq<string * string>) ([<InlineIfLambda>] build): BuildStage = build >> StageContext.addEnvVars kvs
    let inline setAcceptableExitCodes (codes: int seq) ([<InlineIfLambda>] build): BuildStage = build >> StageContext.setAcceptableExitCodes codes
    let inline setFailIfIgnored (failIfIgnored: bool) ([<InlineIfLambda>] build): BuildStage = build >> StageContext.setFailIfIgnored failIfIgnored
    let inline setFailIfNoActiveSubStage (failIfNoActiveSubStage: bool) ([<InlineIfLambda>] build): BuildStage = build >> StageContext.setFailIfNoActiveSubStage failIfNoActiveSubStage
    let inline setContinueStepsOnFailure (continueStepsOnFailure: bool) ([<InlineIfLambda>] build): BuildStage = build >> StageContext.setContinueStepsOnFailure continueStepsOnFailure

namespace Partas.Build.Internal

open System
open System.Diagnostics
open Spectre.Console
open Partas.Build
open SpectreConsoleExt

[<AutoOpen>]
module Runners =
    open StageContext
    open FSharp.Control
    module StageContext =
        module PipelineFailedException =
            let raise message =
                Markup.escape message
                |> Markup.red
                |> printn
                raise (PipelineFailedException message)
        let rec run (stage: StageContext) (index: StageIndex) (ct: System.Threading.CancellationToken) =
            let mutable isSuccess = true
            let inline succeed() = isSuccess <- true
            let inline succeedAND value = isSuccess <- isSuccess && value
            let inline fail() = isSuccess <- false
            let stepExns = ResizeArray<exn>()
            let isActive = stage.IsActive stage
            let pipeline = getParentPipeline stage

            // A condition stage belongs to the condition that runs it, not to the run, and so does every stage
            // under one: `getParentTimingOrder` answers `ValueNone` for those.
            let timings =
                match index, pipeline, getParentTimingOrder stage with
                | StageIndex.Condition, _, _ -> None
                | _, Some pipeline, ValueSome parent -> Some(pipeline.Timings, parent, pipeline.Timings.Start())
                | _ -> None

            // Sub-stages read their parent's ordinal off the value given to them as `ParentContext`.
            let stage =
                match timings with
                | Some(_, _, order) -> { stage with TimingOrder = ValueSome order }
                | None -> stage

            let stageSw = Stopwatch.StartNew()

            pipeline |> Option.iter _.RunBeforeEachStage(stage)
            try
                if not isActive && stage.FailIfIgnored then
                    fail()
                    PipelineFailedException.raise $"Stage ({getNamePath stage}) cannot be ignored (inactive)"
                elif isActive then
                    if stage.FailIfNoActiveSubStage then
                        let parentContext = ValueSome(StageParent.Stage stage)
                        let hasActiveStep =
                            stage.Steps
                            |> Seq.exists (function
                                | Step.StepOfStage stage -> stage.IsActive { stage with ParentContext = parentContext }
                                | _ -> false
                                )
                        if not hasActiveStep then
                            fail()
                            $"Pipeline failed because there were no active sub-stages; stage ({getNamePath stage}) required at least one"
                            |> PipelineFailedException.raise
                    // Only a capture this stage declared itself: one it inherited belongs to an ancestor that is
                    // still running, and clearing that would throw away what its earlier stages wrote.
                    match stage.Output with
                    | ValueSome(StageOutput.Captured capture) -> capture.Clear()
                    | _ -> ()

                    // The length of the capture this stage writes into, as the stage found it. Each attempt trims
                    // back to this length, discarding the lines of earlier attempts and retaining those contributed
                    // by an ancestor's earlier stages. A stage sharing a capture with a concurrent sibling has no
                    // length that separates the two, and keeps every attempt.
                    let capturedBefore =
                        tryGetOwnCapture stage
                        |> ValueOption.map (fun capture -> capture, capture.Count)

                    let parallelism = stage.IsParallel stage
                    let timeoutForStep: int = getTimeoutForStep stage
                    let timeoutForStage: int = getTimeoutForStage stage

                    // The stage timeout bounds every attempt together.
                    use cts = new System.Threading.CancellationTokenSource(timeoutForStage)

                    let extraInfo =
                        $"timeout: {timeoutForStage}ms. step timeout: {timeoutForStep}ms."
                        + if stage.Retry > 0 then $" retries: {stage.Retry}." else ""
                    let inline makeStageConditionMsg msg =
                        getNamePath stage
                        |> Markup.escape
                        |> Markup.turquoise2
                        |> Markup.bold
                        |> sprintf "%s %s started." (Markup.escape msg)
                        |> fun msg -> msg + " " + extraInfo
                        |> Markup.grey
                        |> Rule
                        |> _.LeftJustified()
                        |> vprint stage
                    match index with
                    | StageIndex.Condition -> makeStageConditionMsg "CONDITION STAGE"
                    | StageIndex.Stage i -> makeStageConditionMsg $"STAGE #{i}"
                    | StageIndex.Step _ ->
                        $"%s{buildCurrentStepPrefix stage |> Markup.escape}> sub-stage started %s{extraInfo}"
                        |> Markup.grey
                        |> vprintn stage

                    let mutable retriesLeft = max 0 stage.Retry
                    let mutable attempting = true
                    while attempting do
                        attempting <- false
                        succeed()
                        stepExns.Clear()

                        match capturedBefore with
                        | ValueSome(capture, count) -> capture.TrimTo count
                        | ValueNone -> ()

                        let mutable isStageSoftCancelled = false

                        use stepErrorCts = new System.Threading.CancellationTokenSource()
                        use linkedStepErrorCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stepErrorCts.Token)
                        use linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(linkedStepErrorCts.Token, ct)

                        use stepCts = new System.Threading.CancellationTokenSource(timeoutForStep)
                        use linkedStepCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(stepCts.Token, linkedCts.Token)

                        let steps =
                            stage.Steps
                            |> Seq.indexed
                            // shuffle
                            |> if stage.ShuffleExecuteSequence then Seq.randomShuffle else id
                            |> Seq.map (fun (i, step) -> async {
                                let escapedPrefix =
                                    match step with
                                    | Step.StepFn _ -> buildStepPrefix stage (LanguagePrimitives.Int32WithMeasure i)
                                    | Step.StepOfStage subStage ->
                                        { subStage with ParentContext = ValueSome(StageParent.Stage stage) }
                                        |> buildCurrentStepPrefix
                                        |> sprintf "%s>"
                                        |> Markup.escape

                                let exns = ResizeArray<Exception>()
                                try
                                    let sw = Stopwatch.StartNew()
                                    escapedPrefix + " started" + (if parallelism.IsSome then " in parallel -->" else "")
                                    |> Markup.grey
                                    |> vprintn stage
                                    let! isSuccess =
                                        match step with
                                        | Step.StepFn(_, fn) -> async {
                                            match! fn stage (i |> LanguagePrimitives.Int32WithMeasure) with
                                            | Error e when not (String.IsNullOrEmpty e) ->
                                                if parallelism.IsNone && getNoPrefixForStep stage
                                                then e
                                                else $"{escapedPrefix} {e}"
                                                |> printError stage
                                                return false
                                            | Ok _ -> return true
                                            | _ -> return false
                                            }
                                        | Step.StepOfStage subStage -> async {
                                            let subStage = { subStage with ParentContext = ValueSome(StageParent.Stage stage) }
                                            let isSuccess, es = run subStage (StageIndex.Step i) linkedStepCts.Token
                                            exns.AddRange es
                                            return isSuccess
                                        }
                                    let color = if isSuccess then Markup.grey else Markup.red
                                    let shouldCancelStage = not isSuccess && not stage.ContinueStepsOnFailure && not stepErrorCts.IsCancellationRequested
                                    [
                                        escapedPrefix
                                        if parallelism.IsSome then "finished in parallel."
                                        else "finished."
                                        $"{sw.ElapsedMilliseconds}ms."
                                        if shouldCancelStage then
                                            "Stage policy triggered cancellation."
                                    ]
                                    |> String.concat " "
                                    |> color
                                    |> (if shouldCancelStage then nprintn else vprintn) stage
                                    if shouldCancelStage then stepErrorCts.Cancel()
                                    // if i = stage.Steps.Length - 1 then line()
                                    return isSuccess, exns
                                with
                                | :? PipelineCancelledException as ex ->
                                    raise ex
                                    return false, exns
                                | :? PipelineFailedException as ex ->
                                    raise ex
                                    return false, exns
                                | :? StepSoftCancelledException as ex ->
                                    $"{escapedPrefix} {Markup.escape ex.Message}"
                                    |> Markup.yellow
                                    |> nprintn stage
                                    return true, exns
                                | :? StageSoftCancelledException as ex ->
                                    $"{escapedPrefix} {Markup.escape ex.Message}"
                                    |> Markup.yellow
                                    |> nprintn stage
                                    isStageSoftCancelled <- true
                                    return true, exns
                                | ex ->
                                    $"{escapedPrefix} raised an exception."
                                    |> Markup.red
                                    |> printn
                                    AnsiConsole.WriteException ex
                                    if not stage.ContinueStageOnFailure then
                                        exns.Add(Exception($"{escapedPrefix} {ex.Message}", ex.InnerException))
                                    return false, exns
                            })
                        try
                            let handleExn (exns: ResizeArray<Exception>) =
                                if exns.Count > 0 then
                                    if not stage.ContinueStageOnFailure then stepExns.AddRange exns
                                    if not stage.ContinueStepsOnFailure then stepErrorCts.Cancel()

                            let ts =
                                // Async.StartChild is what applies timeoutForStep, and it starts the work there and then, so it
                                // has to happen inside the handler. Doing it while producing the sequence instead lets the
                                // throttle pull -- and therefore start -- one more step than it is meant to have in flight.
                                let inline asyncHandler step = async {
                                    if stage.ContinueStepsOnFailure || isSuccess then
                                        let! child = Async.StartChild(step, timeoutForStep)
                                        let! result, exns = child
                                        handleExn exns
                                        if not result && not stage.ContinueStepsOnFailure then stepErrorCts.Cancel()
                                        succeedAND result
                                }
                                let steps = AsyncSeq.ofSeq steps
                                match parallelism with
                                | ValueSome p when p > 1 ->
                                    steps
                                    |> AsyncSeq.iterAsyncParallelThrottled p asyncHandler
                                | ValueSome p when p < 1 ->
                                    steps
                                    |> AsyncSeq.iterAsyncParallel asyncHandler
                                | _ ->
                                    steps
                                    |> AsyncSeq.iterAsync asyncHandler
                            Async.RunSynchronously(ts, cancellationToken = linkedCts.Token)
                        with
                        | :? PipelineCancelledException as ex -> fail(); raise ex
                        | :? PipelineFailedException as ex -> fail(); raise ex
                        | _ when isStageSoftCancelled -> succeed()
                        | ex ->
                            fail()
                            if linkedCts.Token.IsCancellationRequested && not stepErrorCts.IsCancellationRequested then
                                $"{buildCurrentStepPrefix stage |> Markup.escape}> stage is cancelled or timed-out."
                                |> Markup.yellow
                                |> nprintn stage
                            else if not stepErrorCts.IsCancellationRequested then
                                $"{buildCurrentStepPrefix stage |> Markup.escape}> stage's step failed."
                                |> Markup.red
                                |> printn
                                AnsiConsole.WriteException ex

                        if not isSuccess && retriesLeft > 0 && not cts.IsCancellationRequested && not ct.IsCancellationRequested then
                            $"%s{getNamePath stage |> Markup.escape} failed. Retrying, {retriesLeft} attempt(s) left."
                            |> Markup.yellow
                            |> nprintn stage
                            retriesLeft <- retriesLeft - 1
                            attempting <- true

                    let color = if isSuccess then Markup.turquoise2 else Markup.red
                    let inline escapedNamePath() = getNamePath stage |> Markup.escape
                    match index with
                    | StageIndex.Condition ->
                        let namePath =
                            escapedNamePath()
                            |> color
                            |> Markup.bold
                        $"CONDITION STAGE %s{namePath} finished. {stageSw.ElapsedMilliseconds}ms."
                        |> Markup.grey
                        |> Rule
                        |> _.LeftJustified()
                        |> nprintn stage
                    | StageIndex.Stage i ->
                        let namePath =
                            escapedNamePath()
                            |> color
                            |> Markup.bold
                        $"STAGE #{i} %s{namePath} finished. {stageSw.ElapsedMilliseconds}ms."
                        |> Markup.grey
                        |> Rule
                        |> _.LeftJustified()
                        |> nprintn stage
                    | StageIndex.Step _ ->
                        $"%s{escapedNamePath()}> sub-stage finished. {stageSw.ElapsedMilliseconds}ms."
                        |> Markup.grey
                        |> vprintn stage
                else
                    let inline escapedNamePath() = getNamePath stage |> Markup.escape
                    match index with
                    | StageIndex.Condition ->
                        $"CONDITION STAGE %s{escapedNamePath()} is " + Markup.yellow "inactive"
                        |> Markup.grey
                        |> Rule
                        |> _.LeftJustified()
                        |> vprintn stage
                    | StageIndex.Stage i ->
                        $"STAGE #{i} %s{escapedNamePath()} is " + Markup.yellow "inactive"
                        |> Markup.grey
                        |> Rule
                        |> _.LeftJustified()
                        |> vprintn stage
                    | StageIndex.Step _ ->
                        $"{buildCurrentStepPrefix stage |> Markup.escape}> sub-stage is " + Markup.yellow "inactive"
                        |> Markup.grey
                        |> vprintn stage
            finally
                pipeline |> Option.iter _.RunAfterEachStage(stage)

                timings
                |> Option.iter (fun (timings, parent, order) ->
                    let outcome =
                        if not isSuccess then
                            stepExns
                            |> Seq.tryHead
                            |> Option.map _.Message
                            |> Option.defaultValue ""
                            |> StageOutcome.Failed
                        elif not isActive then StageOutcome.Skipped
                        else StageOutcome.Succeeded

                    timings.Add (parent, order, { Name = stage.Name; Depth = getDepth stage; Elapsed = stageSw.Elapsed; Outcome = outcome }))

            stage.ContinueStageOnFailure || isSuccess, stepExns

    module PipelineContext =
        open System.Text
        open SpectreConsoleExt

        let runStagesWithFailFast (ctx: PipelineContext) (failFast: bool) (cancelToken: Threading.CancellationToken) (stages: StageContext seq) =
            let stages =
                stages
                |> Seq.map (fun stage -> { stage with ParentContext = ValueSome(StageParent.Pipeline ctx) })
                |> Seq.toList
            let mutable i = 0
            let mutable hasError = false
            let stageExns = ResizeArray<exn>()
            while i < stages.Length && (not failFast || not hasError) do
                let stage = stages[i]
                let isSuccess, _ = StageContext.run stage (StageIndex.Stage i) cancelToken
                hasError <- hasError || not isSuccess
                i <- i + 1
            hasError, stageExns

        let runStages (ctx: PipelineContext) (cancelToken: Threading.CancellationToken) (stages: StageContext seq) = runStagesWithFailFast ctx false cancelToken stages

        let rec run (this: PipelineContext) =
            Console.InputEncoding <- Encoding.UTF8
            Console.OutputEncoding <- Encoding.UTF8
            this.Timings.Clear()

            if not(String.IsNullOrEmpty this.Name) then
                let title = FigletText this.Name
                title.LeftJustified().Color <- Color.Lime
                nprint this title

            let timeoutForPipeline = this.Timeout |> ValueOption.map _.TotalMilliseconds |> ValueOption.defaultValue -1. |> int
            let markedUpName = this.Name |> Markup.escape |> Markup.bold |> Markup.lime
            $"Run PIPELINE %s{markedUpName}. Total timeout: %i{timeoutForPipeline}ms."
            |> nprintn this

            let sw = Stopwatch.StartNew()
            let pipelineExns = ResizeArray<exn>()
            use cts = new Threading.CancellationTokenSource(timeoutForPipeline)
            let mutable hasErrors = false
            try
                if this.Stages.Length > 1 then
                    Markup.turquoise4 "Run stages"
                    |> vprintn this
                let hasFailedStage, stageExns = runStagesWithFailFast this true cts.Token this.Stages
                pipelineExns.AddRange stageExns
                if this.Stages.Length > 1 then
                    Markup.turquoise4 "Run stages finished"
                    |> vprintn this
                    vline this

                let mutable hasFailedPostStage = false
                if not cts.IsCancellationRequested && not (List.isEmpty this.PostStages) then
                    Markup.turquoise4 "Run post-stages"
                    |> vprintn this
                    let result, postStageExns = runStages this cts.Token this.PostStages
                    hasFailedPostStage <- result
                    pipelineExns.AddRange postStageExns
                    Markup.turquoise4 "Run post-stages finished"
                    |> vprintn this
                    vline this

                hasErrors <- hasFailedStage || hasFailedPostStage

            with ex ->
                PipelineContext.printError this ex.Message
                raise ex


            let color =
                if hasErrors then Markup.red
                else if cts.IsCancellationRequested then Markup.yellow
                else Markup.lime

            let exitText = if cts.IsCancellationRequested then "cancelled" else "finished"

            let markupName =
                this.Name
                |> Markup.escape
                |> Markup.bold
                |> color
            $"PIPELINE %s{markupName} is %s{exitText} in %i{sw.ElapsedMilliseconds}ms."
            |> printn

            if cts.IsCancellationRequested then
                raise (PipelineCancelledException "Cancelled by console")

            if pipelineExns.Count > 0 then
                for exn in pipelineExns do
                    let innerMessage = if exn.InnerException <> null then exn.InnerException.Message else ""
                    PipelineContext.printError this (exn.Message + " " + innerMessage)
                raise (PipelineFailedException("Pipeline is failed because of exception", pipelineExns[0]))
            else if hasErrors then
                "Pipeline is failed because result is not indicating as successful"
                |> PipelineContext.printError this
                raise (PipelineFailedException "Pipeline is failed because result is not indicating as successful")

