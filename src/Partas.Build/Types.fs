namespace Partas.Build
open System
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

namespace Partas.Build.Internal

open System
open Partas.Build

type StepSoftCancelledException(msg: string) = inherit Exception(msg)
type StageSoftCancelledException(msg: string) = inherit Exception(msg)

type InputSpec<'T> = { Inputs: ActionInput list; Read: CommandLine.ParseResult -> 'T }

[<Measure>] type stepIndex
type StepIndex = int<stepIndex>

type [<Struct; RequireQualifiedAccess>]
    Step =
    | StepFn of fn: (StageContext -> StepIndex -> Async<Result<unit, string>>)
    | StepOfStage of stage: StageContext

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
    IsActive: StageContext -> bool
    IsParallel: StageContext -> bool
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
    ShuffleExecuteSequence: bool
    ParentContext: StageParent voption
    Steps: Step list
}
and PipelineContext = {
    Name: string
    Description: string voption
    Verify: PipelineContext -> bool
    EnvVars: Map<string, string>
    RemainingCmdArgs: string list
    AcceptableExitCodes: Set<int>
    Timeout: TimeSpan voption
    TimeoutForStep: TimeSpan voption
    TimeoutForStage: TimeSpan voption
    WorkingDir: string voption
    NoPrefixForStep: bool
    NoStdRedirectForStep: bool
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
            IsParallel = fun _ -> false
            ContinueStepsOnFailure = false
            ContinueStageOnFailure = false
            Timeout = ValueNone
            TimeoutForStep = ValueNone
            WorkingDir = ValueNone
            EnvVars = Map.empty
            AcceptableExitCodes = Set [| 0 |]
            FailIfIgnored = false
            FailIfNoActiveSubStage = false
            NoPrefixForStep = true
            NoStdRedirectForStep = false
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

    let rec buildEnvVars (ctx: StageContext) =
        mapParentContext Map.empty _.EnvVars buildEnvVars ctx
        |> Map.foldBack Map.add ctx.EnvVars

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

    let printError (stage: StageContext) (msg: string) =
        match tryGetEnvVar stage "GITHUB_ENV" with
        | ValueSome _ ->
            getNamePath stage
            |> _.Replace(",", "_")
            |> (+) "[STAGE] "
            |> fun title -> $"::error title={title}::{msg}"
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
            Verify = fun _ -> true
            EnvVars = envVars
            RemainingCmdArgs = []
            AcceptableExitCodes = set [ 0 ]
            Timeout = ValueNone
            TimeoutForStep = ValueNone
            TimeoutForStage = ValueNone
            WorkingDir = ValueNone
            NoPrefixForStep = true
            NoStdRedirectForStep = false
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
            (ctx.Name.Replace(",", "_"), msg)
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


namespace Partas.Build

open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Partas.Build.Internal
open System.Net.Http
open Spectre.Console

module InputSpec =
    /// Concatenates input sets, keeping the first occurrence of each input.
    /// <c>ActionInput</c> has no custom equality, so this compares by reference: the same <c>let</c>-bound
    /// option declared by two specs collapses to one, while two separately created options do not.
    let union (inputs: ActionInput list list) = inputs |> List.concat |> List.distinct
    let inline ret v = { Inputs = []; Read = fun _ -> v }
    let map f s = { Inputs = s.Inputs; Read = s.Read >> f }
    let map2 f a b = { Inputs = union [ a.Inputs; b.Inputs ]; Read = fun pr -> f (a.Read pr) (b.Read pr) }
    let ofInput (input: ActionInput<'T>) = { Inputs = [ input :> ActionInput ]; Read = input.GetValue }

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
        (_ctx: StageContext)
        (cancellationToken: System.Threading.CancellationToken)
        (configRequest: HttpRequestMessage -> unit)
        (url: string): Async<Result<unit, string>> = asyncResult {
        use client = new HttpClient()
        let mutable shouldContinue = true
        while shouldContinue && not cancellationToken.IsCancellationRequested do
            try
                AnsiConsole.MarkupLineInterpolated $"[yellow]Check {url} ...[/]"
                use message = new HttpRequestMessage(HttpMethod.Get, url)
                configRequest message
                let! result = client.SendAsync(message, cancellationToken = cancellationToken) |> AsyncResult.ofTask |> AsyncResult.mapError _.Message
                shouldContinue <- not result.IsSuccessStatusCode
            with
            | :? TaskCanceledException when cancellationToken.IsCancellationRequested -> shouldContinue <- false
            | ex -> AnsiConsole.MarkupLineInterpolated $"[red]Health check failed: {ex.Message}[/]"

            do! Async.Sleep 1000 |> Async.map Ok
        if cancellationToken.IsCancellationRequested
        then do! AsyncResult.error "Health check is cancelled."
        else AnsiConsole.MarkupLineInterpolated $"[green]{url} is healthy![/]"
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

[<AutoOpen>]
module Runners =
    open StageContext
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
                AnsiConsole.MarkupLineInterpolated $"[red]{str}[/]"
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
                    let isParallel = stage.IsParallel stage
                    let timeoutForStep: int = getTimeoutForStep stage
                    let timeoutForStage: int = getTimeoutForStage stage

                    let mutable isStageSoftCancelled = false

                    use cts = new System.Threading.CancellationTokenSource(timeoutForStage)
                    use stepErrorCts = new System.Threading.CancellationTokenSource()
                    use linkedStepErrorCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stepErrorCts.Token)
                    use linkedCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(linkedStepErrorCts.Token, ct)

                    use stepCts = new System.Threading.CancellationTokenSource(timeoutForStep)
                    use linkedStepCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(stepCts.Token, linkedCts.Token)

                    AnsiConsole.WriteLine()

                    let extraInfo = $"timeout: {timeoutForStage}ms. step timeout: {timeoutForStep}ms."
                    let markupBoldTurquoise = sprintf "[turquoise2 bold]%s[/]"
                    let markupGrey = sprintf "[grey50]%s[/]"
                    let makeStageConditionMsg msg =
                        getNamePath stage
                        |> markupBoldTurquoise
                        |> sprintf "%s %s started." msg
                        |> fun msg -> msg + " " + extraInfo
                        |> markupGrey
                        |> Rule
                        |> _.LeftJustified()
                        |> AnsiConsole.Write
                    match index with
                    | StageIndex.Condition -> makeStageConditionMsg "CONDITION STAGE"
                    | StageIndex.Stage i -> makeStageConditionMsg $"STAGE #{i}"
                    | StageIndex.Step _ ->
                        $"%s{buildCurrentStepPrefix stage}> sub-stage started %s{extraInfo}"
                        |> markupGrey
                        |> AnsiConsole.MarkupLine

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
                                AnsiConsole.WriteLine()
                                AnsiConsole.MarkupLineInterpolated $"""[grey50]{prefix} started{if isParallel then " in parallel -->" else ""}[/]"""
                                let! isSuccess =
                                    match step with
                                    | Step.StepFn fn -> async {
                                        match! fn stage (i |> LanguagePrimitives.Int32WithMeasure) with
                                        | Error e when String.IsNullOrEmpty e ->
                                            if not isParallel && getNoPrefixForStep stage
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
                                AnsiConsole.MarkupLineInterpolated(
                                    $"""[{color}]{prefix} finished{if isParallel then " in parallel." else "."} {sw.ElapsedMilliseconds}ms. {if shouldCancelStage then "will trigger cancellation." else ""}[/]"""
                                )
                                if shouldCancelStage then stepErrorCts.Cancel()
                                if i = stage.Steps.Length - 1 then AnsiConsole.WriteLine()
                                return isSuccess, exns
                            with
                            | :? PipelineCancelledException as ex ->
                                raise ex
                                return false, exns
                            | :? PipelineFailedException as ex ->
                                raise ex
                                return false, exns
                            | :? StepSoftCancelledException as ex ->
                                AnsiConsole.MarkupLineInterpolated $"[yellow]{prefix} {ex.Message}.[/]"
                                return true, exns
                            | :? StageSoftCancelledException as ex ->
                                AnsiConsole.MarkupLineInterpolated $"[yellow]{prefix} {ex.Message}.[/]"
                                isStageSoftCancelled <- true
                                return true, exns
                            | ex ->
                                AnsiConsole.MarkupLineInterpolated $"[red]{prefix} exception happened.[/]"
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
                            if isParallel then
                                async {
                                    let completers = ResizeArray()
                                    for ts in steps do
                                        let! completer = Async.StartChild(ts, timeoutForStep)
                                        completers.Add completer
                                    let mutable i = 0
                                    while i < completers.Count && (stage.ContinueStepsOnFailure || isSuccess) do
                                        let! result, exns = completers[i]
                                        handleExn exns
                                        if not result && not stage.ContinueStepsOnFailure then stepErrorCts.Cancel()
                                        i <- i + 1
                                        succeedAND result
                                }
                            else
                                async {
                                    let mutable i = 0
                                    let length = Seq.length steps
                                    while i < length && (stage.ContinueStepsOnFailure || isSuccess) do
                                        let! completer = Async.StartChild(Seq.item i steps, timeoutForStep)
                                        let! result, exns = completer
                                        handleExn exns
                                        i <- i + 1
                                        succeedAND result
                                }
                        Async.RunSynchronously(ts, cancellationToken = linkedCts.Token)
                    with
                    | :? PipelineCancelledException as ex -> raise ex
                    | :? PipelineFailedException as ex -> raise ex
                    | _ when isStageSoftCancelled -> succeed()
                    | ex ->
                        fail()
                        if linkedCts.Token.IsCancellationRequested && not stepErrorCts.IsCancellationRequested then
                            AnsiConsole.MarkupLineInterpolated $"[yellow]{buildCurrentStepPrefix stage}> stage is cancelled or timed-out.[/]"
                            AnsiConsole.WriteLine()
                        else if not stepErrorCts.IsCancellationRequested then
                            AnsiConsole.MarkupLineInterpolated $"[red]{buildCurrentStepPrefix stage}> stage's step failed[/]"
                            AnsiConsole.WriteException ex
                            AnsiConsole.WriteLine()

                    let color = if isSuccess then "turquoise4" else "red"
                    match index with
                    | StageIndex.Condition ->
                        AnsiConsole.Write(
                            Rule($"""[grey50]CONDITION STAGE [bold {color}]{getNamePath stage}[/] finished. {stageSw.ElapsedMilliseconds}ms.[/]""")
                                .LeftJustified()
                        )
                    | StageIndex.Stage i ->
                        AnsiConsole.Write(
                            Rule($"""[grey50]STAGE #{i} [bold {color}]{getNamePath stage}[/] finished. {stageSw.ElapsedMilliseconds}ms.[/]""")
                                .LeftJustified()
                        )
                    | StageIndex.Step _ ->
                        AnsiConsole.MarkupLineInterpolated(
                            $"""[grey50]{buildCurrentStepPrefix stage}> sub-stage finished. {stageSw.ElapsedMilliseconds}ms.[/]"""
                        )
                    AnsiConsole.WriteLine()
                else
                    AnsiConsole.WriteLine()
                    match index with
                    | StageIndex.Condition -> AnsiConsole.Write(Rule($"[grey50]CONDITION STAGE {getNamePath stage} is [yellow]inactive[/][/]").LeftJustified())
                    | StageIndex.Stage i -> AnsiConsole.Write(Rule($"[grey50]STAGE #{i} {getNamePath stage} is [yellow]inactive[/][/]").LeftJustified())
                    | StageIndex.Step _ ->
                        AnsiConsole.MarkupLineInterpolated($"[grey50]{buildCurrentStepPrefix stage}> sub-stage is [yellow]inactive[/][/]")

                    AnsiConsole.WriteLine()

            finally pipeline |> Option.iter _.RunAfterEachStage(stage)
            stage.ContinueStageOnFailure || isSuccess, stepExns

    module PipelineContext =
        open System.Text

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

            if not <| String.IsNullOrEmpty this.Name then
                let title = FigletText this.Name
                title.LeftJustified().Color <- Color.Lime
                AnsiConsole.Write title

            let timeoutForPipeline = this.Timeout |> ValueOption.map _.TotalMilliseconds |> ValueOption.defaultValue -1. |> int

            AnsiConsole.MarkupLineInterpolated $"Run PIPELINE [bold lime]{this.Name}[/]. Total timeout: {timeoutForPipeline}ms."
            AnsiConsole.WriteLine()

            let sw = Stopwatch.StartNew()
            let pipelineExns = ResizeArray<exn>()
            use cts = new Threading.CancellationTokenSource(timeoutForPipeline)
            let mutable hasErrors = false
            try
                AnsiConsole.MarkupLine $"[turquoise4]Run stages[/]"
                let hasFailedStage, stageExns = runStagesWithFailFast this true cts.Token this.Stages
                pipelineExns.AddRange stageExns
                AnsiConsole.MarkupLine $"[turquoise4]Run stages finished[/]"
                AnsiConsole.WriteLine()
                AnsiConsole.WriteLine()

                let mutable hasFailedPostStage = false
                if cts.IsCancellationRequested |> not then
                    AnsiConsole.MarkupLine $"[turquoise4]Run post stages[/]"
                    let result, postStageExns = runStages this cts.Token this.PostStages
                    hasFailedPostStage <- result
                    pipelineExns.AddRange postStageExns
                    AnsiConsole.MarkupLine $"[turquoise4]Run post stages finished[/]"
                    AnsiConsole.WriteLine()
                    AnsiConsole.WriteLine()

                hasErrors <- hasFailedStage || hasFailedPostStage

            with ex ->
                PipelineContext.printError this ex.Message
                raise ex


            let color =
                if hasErrors then "red"
                else if cts.IsCancellationRequested then "yellow"
                else "lime"

            let exitText = if cts.IsCancellationRequested then "canncelled" else "finished"


            AnsiConsole.MarkupLineInterpolated $"""PIPELINE [bold {color}]{this.Name}[/] is {exitText} in {sw.ElapsedMilliseconds} ms"""
            AnsiConsole.WriteLine()


            if cts.IsCancellationRequested then
                raise (PipelineCancelledException "Cancelled by console")

            if pipelineExns.Count > 0 then
                for exn in pipelineExns do
                    let innerMessage = if exn.InnerException <> null then exn.InnerException.Message else ""
                    PipelineContext.printError this (exn.Message + " " + innerMessage)
                AnsiConsole.WriteLine()
                raise (PipelineFailedException("Pipeline is failed because of exception", pipelineExns[0]))
            else if hasErrors then
                "Pipeline is failed because result is not indicating as successful"
                |> PipelineContext.printError this
                raise (PipelineFailedException "Pipeline is failed because result is not indicating as successful")

