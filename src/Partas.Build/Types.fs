namespace Partas.Build
open System

[<Struct; RequireQualifiedAccess>]
type Verbosity =
    | Quiet
    | Normal
    | Verbose
    static member Default = Verbose

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
    /// Straight to the console, both streams merged. The default.
    | Console
    /// Dropped.
    | Silent
    /// Held, and lifted into the error message if a step fails.
    | Captured of capture: OutputCapture
    /// Handed to a function, line by line, as it arrives.
    | Redirect of write: (StdStream -> string -> unit)

namespace Partas.Build.Internal

open System
open Partas.Build

type StepSoftCancelledException(msg: string) = inherit Exception(msg)
type StageSoftCancelledException(msg: string) = inherit Exception(msg)

[<Struct>]
type InputSpec<'T> = { Inputs: ActionInput list; Read: CommandLine.ParseResult -> 'T }

[<Measure>] type stepIndex
type StepIndex = int<stepIndex>

type [<Struct; RequireQualifiedAccess>]
    Step =
    | StepFn of fn: (StageContext -> StepIndex -> Async<Result<unit, string>>)
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

and StageContext = {
    Id: int
    Name: string
    Verbosity: Verbosity voption
    IsActive: StageContext -> bool
    IsParallel: StageContext -> int voption
    ContinueStepsOnFailure: bool
    ContinueStageOnFailure: bool
    Timeout: TimeSpan voption
    TimeoutForStep: TimeSpan voption
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
    Description: string voption
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
            IsParallel = fun _ -> ValueNone
            ContinueStepsOnFailure = false
            ContinueStageOnFailure = false
            Timeout = ValueNone
            TimeoutForStep = ValueNone
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
            Verify = fun _ -> true
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
            RunBeforeEachStage = ignore
            RunAfterEachStage = ignore
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
    type SRTPHelper =
        static member inline getVerbosity (verbosity: Verbosity) = verbosity
        static member inline getVerbosity (stageContext: StageContext) = StageContext.getVerbosity stageContext
        static member inline getVerbosity (pipelineContext: PipelineContext) = defaultValueArg pipelineContext.Verbosity Verbosity.Default
    let inline getVerbosity value = ((^T or SRTPHelper):(static member getVerbosity: ^T -> Verbosity) value)
    let mutable hasWrittenLine = false
    let inline setWritten () = hasWrittenLine <- true
    let inline writeLine() =
        if hasWrittenLine then
            Spectre.Console.AnsiConsole.WriteLine()
            hasWrittenLine <- false
    type Spectre.Console.AnsiConsole with
        static member inline vMarkupLineInterpolated(value, format: FormattableString) =
            if (getVerbosity value).IsVerbose
            then Spectre.Console.AnsiConsole.MarkupLineInterpolated format |> setWritten
        static member inline nMarkupLineInterpolated(value, format: FormattableString) =
            if (getVerbosity value).IsQuiet |> not then Spectre.Console.AnsiConsole.MarkupLineInterpolated format |> setWritten
        static member inline qMarkupLineInterpolated(_, format: FormattableString) =
            Spectre.Console.AnsiConsole.MarkupLineInterpolated format |> setWritten
        static member inline vMarkupLine(value, format: string) =
            if (getVerbosity value).IsVerbose then Spectre.Console.AnsiConsole.MarkupLine format |> setWritten
        static member inline nMarkupLine(value, format: string) =
            if (getVerbosity value).IsQuiet |> not then Spectre.Console.AnsiConsole.MarkupLine format |> setWritten
        static member inline qMarkupLine(_, format: string) =
            Spectre.Console.AnsiConsole.MarkupLine format |> setWritten
        static member inline vWrite(value, format: string) =
            if (getVerbosity value).IsVerbose then Spectre.Console.AnsiConsole.Write format |> setWritten
        static member inline vWrite(value, format: Spectre.Console.Rule) =
            if (getVerbosity value).IsVerbose then Spectre.Console.AnsiConsole.Write format |> setWritten
        static member inline nWrite(value, format: string) =
            if (getVerbosity value).IsQuiet |> not then Spectre.Console.AnsiConsole.Write format |> setWritten
        static member inline nWrite(value, format: Spectre.Console.Rule) =
            if (getVerbosity value).IsQuiet |> not then Spectre.Console.AnsiConsole.Write format |> setWritten
        static member inline qWrite(_, format: string) =
            Spectre.Console.AnsiConsole.Write format |> setWritten
        static member inline vWriteLine(value, ?format: string) =
            if (getVerbosity value).IsVerbose then
                if format.IsNone
                then writeLine()
                else Spectre.Console.AnsiConsole.WriteLine format.Value |> setWritten
        static member inline vWriteLine(value, num: int) =
            if (getVerbosity value).IsVerbose then
                if hasWrittenLine && num > 0 then
                    for _ in 1..num do Spectre.Console.AnsiConsole.WriteLine()
                    hasWrittenLine <- false
        static member inline nWriteLine(value, ?format: string) =
            if (getVerbosity value).IsQuiet |> not
            then
                if format.IsNone
                then writeLine()
                else Spectre.Console.AnsiConsole.WriteLine format.Value |> setWritten
        static member inline nWriteLine(value, num: int) =
            if (getVerbosity value).IsQuiet |> not then
                if hasWrittenLine && num > 0 then
                    for _ in 1..num do Spectre.Console.AnsiConsole.WriteLine()
                    hasWrittenLine <- false
        static member inline qWriteLine(_, ?format: string) =
            if format.IsNone
            then writeLine()
            else Spectre.Console.AnsiConsole.WriteLine format.Value |> setWritten

        static member inline qWriteLine(_, num: int) =
            if hasWrittenLine && num > 0 then
                for _ in 1..num do Spectre.Console.AnsiConsole.WriteLine()
                hasWrittenLine <- false

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
    let create name = {
        Name = name
        Description = ValueNone
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
                AnsiConsole.vMarkupLineInterpolated(ctx, $"[yellow]Check {url} ...[/]")
                use message = new HttpRequestMessage(HttpMethod.Get, url)
                configRequest message
                let! result = client.SendAsync(message, cancellationToken = cancellationToken) |> AsyncResult.ofTask |> AsyncResult.mapError _.Message
                shouldContinue <- not result.IsSuccessStatusCode
            with
            | :? TaskCanceledException when cancellationToken.IsCancellationRequested -> shouldContinue <- false
            | ex -> AnsiConsole.qMarkupLineInterpolated(ctx, $"[red]Health check failed: {ex.Message}[/]")

            do! Async.Sleep 1000 |> Async.map Ok
        if cancellationToken.IsCancellationRequested
        then do! AsyncResult.error "Health check is cancelled."
        else AnsiConsole.nMarkupLineInterpolated(ctx, $"[green]{url} is healthy![/]")
    }

    let inline runHttpHealthCheckCancelable ctx cancellationToken url =
        runHttpHealthCheckCancelableWithConfigRequest ctx cancellationToken ignore url
    let inline runHttpHealthCheckWithConfigRequest ctx configRequest url =
        runHttpHealthCheckCancelableWithConfigRequest ctx System.Threading.CancellationToken.None configRequest url
    let inline runHttpHealthCheck (ctx: StageContext) (url: string) =
        runHttpHealthCheckCancelableWithConfigRequest ctx System.Threading.CancellationToken.None ignore url

    /// Conjoins a condition onto the stage a <c>BuildStage</c> produces.
    /// Conditions accumulate rather than replace, so a stage declaring several is active only when all hold.
    let inline buildStageIsActive ([<InlineIfLambda>] build: BuildStage) ([<InlineIfLambda>] conditionFn: BuildStageIsActive): BuildStage =
        fun ctx ->
            let newCtx = build ctx
            { newCtx with IsActive = fun ctx -> newCtx.IsActive ctx && conditionFn ctx }


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
        let rec run (stage: StageContext) (index: StageIndex) (ct: System.Threading.CancellationToken) =
            let mutable isSuccess = true
            let inline succeed() = isSuccess <- true
            let inline succeedAND value = isSuccess <- isSuccess && value
            let inline fail() = isSuccess <- false
            let stepExns = ResizeArray<exn>()
            let isActive = stage.IsActive stage
            let pipeline = getParentPipeline stage
            let raisePipelineFailedException str =
                AnsiConsole.qMarkupLineInterpolated(stage, $"[red]{str}[/]")
                raise (PipelineFailedException str)

            pipeline |> Option.iter _.RunBeforeEachStage(stage)
            try
                if not isActive && stage.FailIfIgnored then
                    raisePipelineFailedException $"Stage ({getNamePath stage}) cannot be ignored (inactive)"
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
                            $"Pipeline failed because there were no active sub-stages; stage ({getNamePath stage}) required at least one"
                            |> raisePipelineFailedException
                    let stageSw = Stopwatch.StartNew()
                    // Only a capture this stage declared itself: one it inherited belongs to an ancestor that is
                    // still running, and clearing that would throw away what its earlier stages wrote.
                    match stage.Output with
                    | ValueSome(StageOutput.Captured capture) -> capture.Clear()
                    | _ -> ()
                    let parallelism = stage.IsParallel stage
                    let timeoutForStep: int = getTimeoutForStep stage
                    let timeoutForStage: int = getTimeoutForStage stage

                    let mutable isStageSoftCancelled = false

                    use cts = new System.Threading.CancellationTokenSource(timeoutForStage)
                    use stepErrorCts = new System.Threading.CancellationTokenSource()
                    use linkedStepErrorCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stepErrorCts.Token)
                    use linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(linkedStepErrorCts.Token, ct)

                    use stepCts = new System.Threading.CancellationTokenSource(timeoutForStep)
                    use linkedStepCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(stepCts.Token, linkedCts.Token)

                    AnsiConsole.qWriteLine(stage)

                    let extraInfo = $"timeout: {timeoutForStage}ms. step timeout: {timeoutForStep}ms."
                    let markupBoldTurquoise = sprintf "[turquoise2 bold]%s[/]"
                    let markupGrey = sprintf "[grey50]%s[/]"
                    let makeStageConditionMsg msg =
                        AnsiConsole.nWrite(
                            stage,
                            getNamePath stage
                            |> markupBoldTurquoise
                            |> sprintf "%s %s started." msg
                            |> fun msg -> msg + " " + extraInfo
                            |> markupGrey
                            |> Rule
                            |> _.LeftJustified()
                            )
                    match index with
                    | StageIndex.Condition -> makeStageConditionMsg "CONDITION STAGE"
                    | StageIndex.Stage i -> makeStageConditionMsg $"STAGE #{i}"
                    | StageIndex.Step _ ->
                        AnsiConsole.nMarkupLine(
                            stage,
                            $"%s{buildCurrentStepPrefix stage}> sub-stage started %s{extraInfo}"
                            |> markupGrey
                        )

                    let steps =
                        stage.Steps
                        |> Seq.indexed
                        // shuffle
                        |> if stage.ShuffleExecuteSequence then Seq.randomShuffle else id
                        |> Seq.map (fun (i, step) -> async {
                            let prefix =
                                match step with
                                | Step.StepFn _ -> buildStepPrefix stage (LanguagePrimitives.Int32WithMeasure i)
                                | Step.StepOfStage subStage ->
                                    { subStage with ParentContext = ValueSome(StageParent.Stage stage) }
                                    |> buildCurrentStepPrefix
                                    |> sprintf "%s>"

                            let exns = ResizeArray<Exception>()
                            try
                                let sw = Stopwatch.StartNew()
                                AnsiConsole.qWriteLine(stage)
                                AnsiConsole.vMarkupLineInterpolated(stage, $"""[grey50]{prefix} started{if parallelism.IsSome then " in parallel -->" else ""}[/]""")
                                let! isSuccess =
                                    match step with
                                    | Step.StepFn fn -> async {
                                        match! fn stage (i |> LanguagePrimitives.Int32WithMeasure) with
                                        | Error e when not (String.IsNullOrEmpty e) ->
                                            if parallelism.IsNone && getNoPrefixForStep stage
                                            then e
                                            else $"{prefix} {e}"
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
                                let color = if isSuccess then "grey50" else "red"
                                let shouldCancelStage = not isSuccess && not stage.ContinueStepsOnFailure && not stepErrorCts.IsCancellationRequested
                                AnsiConsole.nMarkupLineInterpolated(
                                    stage,
                                    $"""[{color}]{prefix} finished{if parallelism.IsSome then " in parallel." else "."} {sw.ElapsedMilliseconds}ms. {if shouldCancelStage then "will trigger cancellation." else ""}[/]"""
                                )
                                if shouldCancelStage then stepErrorCts.Cancel()
                                if i = stage.Steps.Length - 1 then AnsiConsole.qWriteLine(())
                                return isSuccess, exns
                            with
                            | :? PipelineCancelledException as ex ->
                                raise ex
                                return false, exns
                            | :? PipelineFailedException as ex ->
                                raise ex
                                return false, exns
                            | :? StepSoftCancelledException as ex ->
                                AnsiConsole.nMarkupLineInterpolated(stage,$"[yellow]{prefix} {ex.Message}.[/]")
                                return true, exns
                            | :? StageSoftCancelledException as ex ->
                                AnsiConsole.nMarkupLineInterpolated(stage, $"[yellow]{prefix} {ex.Message}.[/]")
                                isStageSoftCancelled <- true
                                return true, exns
                            | ex ->
                                AnsiConsole.qMarkupLineInterpolated(stage, $"[red]{prefix} exception happened.[/]")
                                AnsiConsole.WriteException ex
                                if not stage.ContinueStageOnFailure then
                                    exns.Add(Exception($"{prefix} {ex.Message}", ex.InnerException))
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
                    | :? PipelineCancelledException as ex -> raise ex
                    | :? PipelineFailedException as ex -> raise ex
                    | _ when isStageSoftCancelled -> succeed()
                    | ex ->
                        fail()
                        if linkedCts.Token.IsCancellationRequested && not stepErrorCts.IsCancellationRequested then
                            AnsiConsole.nMarkupLineInterpolated(stage, $"[yellow]{buildCurrentStepPrefix stage}> stage is cancelled or timed-out.[/]")
                            AnsiConsole.nWriteLine(stage)
                        else if not stepErrorCts.IsCancellationRequested then
                            AnsiConsole.qMarkupLineInterpolated((), $"[red]{buildCurrentStepPrefix stage}> stage's step failed[/]")
                            AnsiConsole.WriteException ex
                            AnsiConsole.qWriteLine(())

                    let color = if isSuccess then "turquoise4" else "red"
                    match index with
                    | StageIndex.Condition ->
                        AnsiConsole.nWrite(
                            stage,
                            Rule($"""[grey50]CONDITION STAGE [bold {color}]{getNamePath stage}[/] finished. {stageSw.ElapsedMilliseconds}ms.[/]""")
                                .LeftJustified()
                        )
                    | StageIndex.Stage i ->
                        AnsiConsole.nWrite(
                            stage,
                            Rule($"""[grey50]STAGE #{i} [bold {color}]{getNamePath stage}[/] finished. {stageSw.ElapsedMilliseconds}ms.[/]""")
                                .LeftJustified()
                        )
                    | StageIndex.Step _ ->
                        AnsiConsole.nMarkupLineInterpolated(
                            stage,
                            $"""[grey50]{buildCurrentStepPrefix stage}> sub-stage finished. {stageSw.ElapsedMilliseconds}ms.[/]"""
                        )
                    AnsiConsole.nWriteLine(stage)
                else
                    AnsiConsole.nWriteLine(stage)
                    match index with
                    | StageIndex.Condition -> AnsiConsole.vWrite(stage, Rule($"[grey50]CONDITION STAGE {getNamePath stage} is [yellow]inactive[/][/]").LeftJustified())
                    | StageIndex.Stage i -> AnsiConsole.vWrite(stage, Rule($"[grey50]STAGE #{i} {getNamePath stage} is [yellow]inactive[/][/]").LeftJustified())
                    | StageIndex.Step _ ->
                        AnsiConsole.vMarkupLineInterpolated(stage, $"[grey50]{buildCurrentStepPrefix stage}> sub-stage is [yellow]inactive[/][/]")

                    AnsiConsole.vWriteLine(stage)

            finally pipeline |> Option.iter _.RunAfterEachStage(stage)
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

            if not(String.IsNullOrEmpty this.Name) && (getVerbosity this).IsVerbose then
                let title = FigletText this.Name
                title.LeftJustified().Color <- Color.Lime
                AnsiConsole.Write title

            let timeoutForPipeline = this.Timeout |> ValueOption.map _.TotalMilliseconds |> ValueOption.defaultValue -1. |> int

            AnsiConsole.nMarkupLineInterpolated(this, $"Run PIPELINE [bold lime]{this.Name}[/]. Total timeout: {timeoutForPipeline}ms.")
            AnsiConsole.nWriteLine(this)

            let sw = Stopwatch.StartNew()
            let pipelineExns = ResizeArray<exn>()
            use cts = new Threading.CancellationTokenSource(timeoutForPipeline)
            let mutable hasErrors = false
            try
                if this.Stages.Length > 1 then
                    AnsiConsole.vMarkupLine(this, "[turquoise4]Run stages[/]")
                let hasFailedStage, stageExns = runStagesWithFailFast this true cts.Token this.Stages
                pipelineExns.AddRange stageExns
                if this.Stages.Length > 1 then
                    AnsiConsole.vMarkupLine(this, "[turquoise4]Run stages finished[/]")
                    AnsiConsole.vWriteLine(this)
                    AnsiConsole.vWriteLine(this)

                let mutable hasFailedPostStage = false
                if not cts.IsCancellationRequested && not (List.isEmpty this.PostStages) then
                    AnsiConsole.vMarkupLine(this, "[turquoise4]Run post stages[/]")
                    let result, postStageExns = runStages this cts.Token this.PostStages
                    hasFailedPostStage <- result
                    pipelineExns.AddRange postStageExns
                    AnsiConsole.vMarkupLine(this, "[turquoise4]Run post stages finished[/]")
                    AnsiConsole.vWriteLine(this)
                    AnsiConsole.vWriteLine(this)

                hasErrors <- hasFailedStage || hasFailedPostStage

            with ex ->
                PipelineContext.printError this ex.Message
                raise ex


            let color =
                if hasErrors then "red"
                else if cts.IsCancellationRequested then "yellow"
                else "lime"

            let exitText = if cts.IsCancellationRequested then "cancelled" else "finished"

            AnsiConsole.qMarkupLineInterpolated(this, $"""PIPELINE [bold {color}]{this.Name}[/] is {exitText} in {sw.ElapsedMilliseconds} ms""")
            AnsiConsole.qWriteLine(())


            if cts.IsCancellationRequested then
                raise (PipelineCancelledException "Cancelled by console")

            if pipelineExns.Count > 0 then
                for exn in pipelineExns do
                    let innerMessage = if exn.InnerException <> null then exn.InnerException.Message else ""
                    PipelineContext.printError this (exn.Message + " " + innerMessage)
                AnsiConsole.qWriteLine(())
                raise (PipelineFailedException("Pipeline is failed because of exception", pipelineExns[0]))
            else if hasErrors then
                "Pipeline is failed because result is not indicating as successful"
                |> PipelineContext.printError this
                raise (PipelineFailedException "Pipeline is failed because result is not indicating as successful")

