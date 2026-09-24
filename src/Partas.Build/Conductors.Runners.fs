[<AutoOpen>]
module Partas.Build.Internal.Runners

open System.Diagnostics
open System.Threading
open Partas.Build
open System

open Partas.Build.Internal.Output
open StageContext
open Spectre.Console
open FSharp.Control

module StageContext =
    module PipelineFailedException =
        let raise message =
            Logging.PipelineFailed.print message
            raise (PipelineFailedException message)

    /// <summary>The exception a step handed back, with the aggregate the await wrapped it in removed.</summary>
    /// <remarks>One layer, which is the one the await added: an aggregate a step raised itself arrives inside
    /// that wrapper and travels on as the cause the step produced.</remarks>
    let internal awaited (error: exn) =
        match error with
        | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 -> aggregate.InnerExceptions[0]
        | _ -> error

    /// The step one attempt is executing, while it is executing.
    [<Struct>]
    type internal InFlightStep = {
        index: int
        label: string voption
        prefix: string
    }

    /// <summary>The steps of one attempt that have started and not finished, by step index.</summary>
    /// <remarks>
    /// A step a token ended stays recorded here through the attempt's unwind, and an expiry of the stage's own
    /// budget is reported against every step still recorded when the attempt reaches its classification.
    /// </remarks>
    type internal InFlightSteps = System.Collections.Concurrent.ConcurrentDictionary<int, InFlightStep>

    /// <summary>The tokens one step of an attempt runs under.</summary>
    /// <remarks>
    /// <c>expiry</c> is the <c>timeoutForStep</c> budget the stage gave the step, running from the moment that
    /// step starts and the only clock over it, so each step of a sequential stage gets the whole budget. Where
    /// the stage set no budget it is <c>ValueNone</c> and <c>cancellation</c> is the attempt's own source.
    /// <para><c>cancellation</c> is the token the step's whole body runs under, a sub-stage and a command
    /// alike: a command reads it through <c>Async.CancellationToken</c> and registers its kill on it, so an
    /// expiry takes the process tree with it.</para>
    /// <para>A step still recorded as in flight when the attempt unwinds — abandoned by the token that ended
    /// it, or by the stage's own fail-fast cancellation — leaves its sources undisposed rather than racing a
    /// straggler that may still read them.</para>
    /// </remarks>
    [<Struct>]
    type internal StepBudget = {
        expiry: CancellationTokenSource voption
        cancellation: CancellationTokenSource
    }

    /// The budget each step of one attempt was given, by step index, held until the attempt ends.
    type internal StepBudgets = System.Collections.Concurrent.ConcurrentDictionary<int, StepBudget>

    /// <summary>Where one attempt of a stage collects what its steps produce.</summary>
    /// <remarks>
    /// A step's evidence reaches the stage through these writes, issued as the step produces it. Every write
    /// and every read goes through this module and takes the same lock, which covers the steps of a parallel
    /// stage and a straggler of an earlier attempt.
    /// </remarks>
    type internal StepEvidence = private {
        sync: obj
        failures: ResizeArray<StepFailure>
        nested: ResizeArray<ScopeReport>
        exns: ResizeArray<exn>
    }

    module internal StepEvidence =
        let create() = { sync = obj (); failures = ResizeArray(); nested = ResizeArray(); exns = ResizeArray() }
        let addFailure (failure: StepFailure) (evidence: StepEvidence) = lock evidence.sync (fun () -> evidence.failures.Add failure)
        let addNested (report: ScopeReport) (evidence: StepEvidence) = lock evidence.sync (fun () -> evidence.nested.Add report)
        let addExceptions (errors: exn seq) (evidence: StepEvidence) = lock evidence.sync (fun () -> evidence.exns.AddRange errors)
        /// Discards what an earlier attempt of the stage wrote.
        let clear (evidence: StepEvidence) =
            lock evidence.sync (fun () ->
                evidence.failures.Clear()
                evidence.nested.Clear()
                evidence.exns.Clear())
        /// The causes, the sub-stage reports and the exceptions written so far, as one consistent copy.
        let snapshot (evidence: StepEvidence) =
            lock evidence.sync (fun () ->
                List.ofSeq evidence.failures, List.ofSeq evidence.nested, List.ofSeq evidence.exns)

    type internal StepHandlerParameters = {
        inFlight: InFlightSteps
        stage: StageContext
        stepStage: StageContext
        parallelism: int voption
        i: int
        escapedPrefix: string
        linkedStepCts: CancellationTokenSource
        evidence: StepEvidence
        stepErrorCts: CancellationTokenSource
    }

    module internal Internal =
        // A condition stage belongs to the condition that runs it, not to the run, and so does every stage
        // under one: `getParentTimingOrder` answers `ValueNone` for those.
        let getTimings (pipeline: PipelineContext option) (index: StageIndex) (stage: StageContext) =
            match index, pipeline, getParentTimingOrder stage with
            | StageIndex.Condition, _, _ -> None
            | _, Some pipeline, ValueSome parent -> Some(pipeline.Timings, parent, StageTimings.start pipeline.Timings)
            | _ -> None
        // A stage of the pipeline reports to it directly. A sub-stage travels inside its parent's report, and
        // a condition stage belongs to the condition that runs it.
        let getReports (pipeline: PipelineContext option) (index: StageIndex) (stage: StageContext) =
            match index, pipeline, stage.ParentContext with
            | StageIndex.Condition, _, _ -> None
            | _, Some pipeline, ValueSome(StageParent.Pipeline _) -> Some pipeline.Reports
            | _ -> None
        let checkFailIfIgnoredCondition isActive stage =
            if not isActive && stage.FailIfIgnored then
                Error $"Stage ({getNamePath stage}) cannot be ignored (inactive)"
            else Ok stage
        let checkFailIfNoActiveSubStageCondition stage =
            if not stage.FailIfNoActiveSubStage then Ok stage else
            let parentContext = ValueSome(StageParent.Stage stage)
            let hasActiveStep =
                stage.Steps
                |> Seq.exists (function Step.StepOfStage stage -> stage.IsActive { stage with ParentContext = parentContext } | _ -> false)
            if hasActiveStep then Ok stage
            else Error $"Pipeline failed because there were no active sub-stages; stage ({getNamePath stage}) required at least one"
        let getStepEscapedPrefix stage index = function
            | Step.StepFn _ | Step.Operation _ ->
                buildStepPrefix stage (LanguagePrimitives.Int32WithMeasure index)
            | Step.StepOfStage subStage ->
                { subStage with ParentContext = ValueSome(StageParent.Stage stage) }
                |> buildCurrentStepPrefix
                |> sprintf "%s>"
                |> Markup.escape
        /// The label a step was declared with.
        let getStepLabel = function
            | Step.StepFn(label, _) -> label
            | Step.Operation(label, _) -> label
            | Step.StepOfStage subStage -> ValueSome subStage.Name
        /// <summary>What the stage did, as its report and its timing both record it.</summary>
        /// <remarks>The failure text is the message of the first exception the stage propagated, and the first
        /// line of the first cause it recorded where a step failed without raising.</remarks>
        let getOutcome isActive isSuccess (stepExns: exn list) (stepFailures: StepFailure list) =
            if not isSuccess then
                stepExns
                |> List.tryHead
                |> Option.map _.Message
                |> Option.orElseWith (fun () -> stepFailures |> List.tryHead |> Option.map (_.Cause >> FailureCause.summarise))
                |> Option.defaultValue ""
                |> StageOutcome.Failed
            elif not isActive then StageOutcome.Skipped
            else StageOutcome.Succeeded
        let handleTimings (stage: StageContext) (outcome: StageOutcome) (stageSw: Stopwatch) (timings: StageTimings, parent: int64, order: int64) =
            StageTimings.add parent order { Name = stage.Name; Depth = getDepth stage; Elapsed = stageSw.Elapsed; Outcome = outcome } timings
        let checkBeforeHooks stage = Option.iter _.RunBeforeEachStage(stage)

    let rec run (stage: StageContext) (index: StageIndex) (ct: CancellationToken) =
        let mutable isSuccess = true
        let inline SUCCESS() = isSuccess <- true
        let inline AND_SUCCESS value = isSuccess <- isSuccess && value
        let inline FAIL() = isSuccess <- false
        // What the attempt being reported produced: its causes, held whatever `ContinueStageOnFailure`
        // decides, the reports of the sub-stages it ran, and the exceptions it offers the enclosing scope.
        let evidence = StepEvidence.create()
        // Assigned in the `finally` below, on every path out of the stage.
        let mutable reported = Unchecked.defaultof<ScopeReport>
        // Whether what ended this stage was a cancellation rather than a failure of its own. A cancelled
        // scope runs no failure handler.
        let mutable cancelled = false
        let isActive = stage.IsActive stage
        let pipeline = getParentPipeline stage
        let timings = Internal.getTimings pipeline index stage
        let reports = Internal.getReports pipeline index stage
        // Where this stage sits in the run. A condition stage takes no position of its own: its address, its
        // timing and its report all belong to the condition that runs it, and none of the three is recorded.
        let address =
            let enclosing = mapStageParentContext ScopeAddress.root _.Address stage
            match index with
            | StageIndex.Condition -> enclosing
            | StageIndex.Stage ordinal
            | StageIndex.Step ordinal -> ScopeAddress.child ordinal stage.Name enclosing
        // Sub-stages read their parent's address and ordinal off the value given to them as `ParentContext`.
        let stage =
            match timings with
            | Some(_, _, order) -> { stage with Address = address; TimingOrder = ValueSome order }
            | None -> { stage with Address = address }

        let stageSw = Stopwatch.StartNew()

        pipeline
        |> Internal.checkBeforeHooks stage

        try
            match
                Internal.checkFailIfIgnoredCondition isActive stage
                |> Result.bind Internal.checkFailIfNoActiveSubStageCondition
            with
            | Error errMessage ->
                FAIL()
                PipelineFailedException.raise errMessage
            | _ when not isActive -> Logging.stageCondition.inactive stage index
            | _ ->
            // Only a capture this stage declared itself: one it inherited belongs to an ancestor that is
            // still running, and clearing that would throw away what its earlier stages wrote.
            match stage.Output with
            | ValueSome(StageOutput.Captured capture) -> OutputCapture.clear capture
            | _ -> ()

            // The length of the capture this stage writes into, as the stage found it. Each attempt trims
            // back to this length, discarding the lines of earlier attempts and retaining those contributed
            // by an ancestor's earlier stages. A stage sharing a capture with a concurrent sibling has no
            // length that separates the two, and keeps every attempt.
            let capturedBefore =
                tryGetOwnCapture stage
                |> ValueOption.map (fun capture -> capture, OutputCapture.count capture)

            // The producer values as the stage found them. Each attempt restores them: a producer this
            // scope owns runs again inside the attempt that needs it, while one published before the
            // stage started belongs to an enclosing scope and stays.
            let producedBefore = pipeline |> Option.map (fun pipeline -> pipeline.Producers, ExecutionState.values pipeline.Producers)

            let parallelism = stage.IsParallel stage
            let timeoutForStep: int = getTimeoutForStep stage
            let timeoutForStage: int = getTimeoutForStage stage

            // The stage timeout bounds every attempt together.
            use cts = new CancellationTokenSource(timeoutForStage)

            Logging.stageCondition.start stage index timeoutForStage timeoutForStep

            let mutable retriesLeft = max 0 stage.Retry
            let mutable attempting = true
            while attempting do
                attempting <- false
                SUCCESS()
                StepEvidence.clear evidence

                match capturedBefore with
                | ValueSome(capture, count) -> OutputCapture.trimTo count capture
                | ValueNone -> ()

                match producedBefore with
                | Some(producers, values) -> ExecutionState.resetTo values producers
                | None -> ()

                let mutable isStageSoftCancelled = false
                // Whether an exception escaped this attempt's steps, which is what a timeout of any budget
                // arrives as.
                let mutable escaped = false

                let inFlight = InFlightSteps()

                use stepErrorCts = new CancellationTokenSource()
                use linkedStepErrorCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stepErrorCts.Token)
                use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(linkedStepErrorCts.Token, ct)

                let stepBudgets = StepBudgets()

                /// The budget a step starting now runs under, recorded for the attempt to read once it unwinds.
                let takeBudget index =
                    let budget =
                        if timeoutForStep < 0 then { expiry = ValueNone; cancellation = linkedCts }
                        else
                            let expiry = new CancellationTokenSource(timeoutForStep)
                            {
                                expiry = ValueSome expiry
                                cancellation = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, linkedCts.Token)
                            }

                    stepBudgets[index] <- budget
                    budget.cancellation

                let expired index =
                    match stepBudgets.TryGetValue index with
                    | true, budget -> budget.expiry |> ValueOption.exists _.IsCancellationRequested
                    | _ -> false

                // Held by every step's flush of this stage, serialising them against each other.
                let flushLock = obj ()
                // Whether two of this stage's steps can be running at the same time. A `parallel' 1`
                // throttle takes the sequential branch below, alongside `ValueNone`.
                let canOverlap = parallelism |> ValueOption.map ((<>) 1) |> ValueOption.defaultValue false

                let steps =
                    stage.Steps
                    |> Seq.indexed
                    // shuffle
                    |> if stage.ShuffleExecuteSequence then Seq.randomShuffle else id
                    |> Seq.map (fun (i, step) -> async {
                        let escapedPrefix = Internal.getStepEscapedPrefix stage i step
                        // A step's own transport buffer, ahead of `stage`'s `Output`, holding every line
                        // this one step writes until its flush.
                        let flush, stepStage =
                            if not canOverlap then ignore, stage else
                            let capture = OutputCapture.create()

                            (fun () -> lock flushLock (fun () ->
                                for struct(stream, line) in OutputCapture.entries capture do
                                    writeLine stage stream line)),
                            { stage with StepBuffer = ValueSome capture }

                        // Taken before the step starts, so the clause below can ask whether the token this step
                        // ran under is the one that ended it.
                        let budget = takeBudget i

                        try
                            try
                                inFlight[i] <- { index = i; label = Internal.getStepLabel step; prefix = escapedPrefix }
                                // The step's work runs under the budget this step was given, so the token it
                                // reads through `Async.CancellationToken` — the one a command registers its
                                // kill on — expires with that budget.
                                let work =
                                    Async.StartAsTask(
                                        stepHandler {
                                            inFlight = inFlight
                                            stage = stage
                                            stepStage = stepStage
                                            parallelism = parallelism
                                            i = i
                                            escapedPrefix = escapedPrefix
                                            linkedStepCts = budget
                                            evidence = evidence
                                            stepErrorCts = stepErrorCts
                                        } step,
                                        cancellationToken = budget.Token)

                                let! outcome =
                                    async {
                                        try return! Async.AwaitTask work
                                        with error -> return raise (awaited error)
                                    }

                                inFlight.TryRemove i |> ignore
                                return outcome
                            with
                            | :? PipelineCancelledException as ex ->
                                raise ex
                                return false
                            | :? PipelineFailedException as ex ->
                                raise ex
                                return false
                            // The token this step ran under fired: its own budget, or the attempt's. The step
                            // stays in flight and the cancellation travels, which is what the attempt
                            // classifies once it has unwound. A cancellation the step raised while that token
                            // stands takes the handler below and is recorded as the cause it is.
                            | :? OperationCanceledException as ex when budget.IsCancellationRequested ->
                                raise ex
                                return false
                            | :? StepSoftCancelledException as ex ->
                                inFlight.TryRemove i |> ignore
                                $"{escapedPrefix} {Markup.escape ex.Message}"
                                |> Markup.yellow
                                |> nprintn stage
                                return true
                            | :? StageSoftCancelledException as ex ->
                                inFlight.TryRemove i |> ignore
                                $"{escapedPrefix} {Markup.escape ex.Message}"
                                |> Markup.yellow
                                |> nprintn stage
                                isStageSoftCancelled <- true
                                return true
                            | ex ->
                                inFlight.TryRemove i |> ignore
                                $"{escapedPrefix} raised an exception."
                                |> Markup.red
                                |> printn
                                AnsiConsole.WriteException ex
                                // The exception is evidence of the attempt; whether it also reaches the
                                // enclosing scope is `ContinueStageOnFailure`'s to say.
                                evidence
                                |> StepEvidence.addFailure { Index = i; Label = Internal.getStepLabel step; Cause = FailureCause.Raised ex }
                                if not stage.ContinueStageOnFailure then
                                    evidence
                                    |> StepEvidence.addExceptions [ Exception($"{escapedPrefix} {ex.Message}", ex.InnerException) ]
                                return false
                        finally flush ()
                    })
                try
                    try
                        let ts =
                            // The budget each step was given is the only clock over it, so `Async.StartChild`
                            // takes none. It starts the work there and then, which is why it has to happen inside
                            // the handler: producing the sequence instead lets the throttle pull -- and therefore
                            // start -- one more step than it is meant to have in flight.
                            let inline asyncHandler step = async {
                                if stage.ContinueStepsOnFailure || isSuccess then
                                    let! child = Async.StartChild step
                                    let! result = child
                                    if not result && not stage.ContinueStepsOnFailure then stepErrorCts.Cancel()
                                    AND_SUCCESS result
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
                    | :? PipelineCancelledException as ex -> FAIL(); cancelled <- true; raise ex
                    | :? PipelineFailedException as ex -> FAIL(); raise ex
                    | _ when isStageSoftCancelled -> SUCCESS()
                    | ex ->
                        FAIL()
                        escaped <- true
                        if
                            (linkedCts.Token.IsCancellationRequested || stepBudgets.Keys |> Seq.exists expired)
                            && not stepErrorCts.IsCancellationRequested
                        then
                            $"{buildCurrentStepPrefix stage |> Markup.escape}> stage is cancelled or timed-out."
                            |> Markup.yellow
                            |> nprintn stage
                        else if not stepErrorCts.IsCancellationRequested then
                            $"{buildCurrentStepPrefix stage |> Markup.escape}> stage's step failed."
                            |> Markup.red
                            |> printn
                            AnsiConsole.WriteException ex

                    // Which token fired decides this, and only the runner holds them: `cts` is the budget this
                    // stage was given and each `StepBudget.expiry` the budget it gave one of its steps, so an
                    // expiry of either is a failure of this stage and reports `FailureCause.TimedOut`. `ct`
                    // belongs to an ancestor and `stepErrorCts` to stage policy; a step either of those ended
                    // stays a cancellation, reported as one above. The stage's own budget catches every step
                    // still in flight — several, under `parallel'` — and a step budget catches its own step.
                    if
                        escaped
                        && (cts.IsCancellationRequested || stepBudgets.Keys |> Seq.exists expired)
                        && not ct.IsCancellationRequested
                        && not stepErrorCts.IsCancellationRequested
                    then
                        let message = FailureCause.describe FailureCause.TimedOut
                        let caught =
                            inFlight.Values
                            |> Seq.filter (fun step -> cts.IsCancellationRequested || expired step.index)
                            |> Seq.sortBy _.index
                            |> List.ofSeq

                        let timedOut =
                            match caught with
                            // A budget that expired between two steps leaves the stage itself as the whole of the
                            // evidence, and the stage carries no step index.
                            | [] -> [ { index = StepFailure.NoStep; label = ValueNone; prefix = buildCurrentStepPrefix stage } ]
                            | steps -> steps

                        FAIL()

                        for step in timedOut do
                            let line = if parallelism.IsNone && getNoPrefixForStep stage then message else $"{step.prefix} {message}"
                            printError stage line
                            evidence |> StepEvidence.addFailure { Index = step.index; Label = step.label; Cause = FailureCause.TimedOut }
                            if not stage.ContinueStageOnFailure then
                                evidence |> StepEvidence.addExceptions [ Exception line ]
                    elif escaped && ct.IsCancellationRequested then
                        cancelled <- true

                finally
                    // A step still in flight was abandoned rather than finished and may still read the token it
                    // was given, so its sources are left to the collector. A step the stage gave no budget ran
                    // under the attempt's own source, which the attempt owns.
                    for KeyValue(index, budget) in stepBudgets do
                        if not (inFlight.ContainsKey index) then
                            budget.expiry
                            |> ValueOption.iter (fun expiry ->
                                budget.cancellation.Dispose()
                                expiry.Dispose())

                if not isSuccess && retriesLeft > 0 && not cts.IsCancellationRequested && not ct.IsCancellationRequested then
                    $"%s{getNamePath stage |> Markup.escape} failed. Retrying, {retriesLeft} attempt(s) left."
                    |> Markup.yellow
                    |> nprintn stage
                    retriesLeft <- retriesLeft - 1
                    attempting <- true

                Logging.stageCondition.finished stage stageSw isSuccess index

        finally // finished stage run; cleanup/report/post
            pipeline |> Option.iter _.RunAfterEachStage(stage)

            let failures, nested, exns = StepEvidence.snapshot evidence

            reported <- {
                Name = stage.Name
                Address = address
                Outcome = Internal.getOutcome isActive isSuccess exns failures
                Propagates = not (stage.ContinueStageOnFailure || isSuccess)
                Failures = failures
                Exceptions = exns
                Nested = nested
            }

            // The stage's own wall time ends here: a handler reports on the stage rather than belonging to it,
            // and the timing row and the finish line above it quote the same figure.
            stageSw.Stop()

            // Once per failed execution of this stage, after its retries and after the handlers of every
            // scope nested in it. A handler that raises leaves one more cause, and the stage keeps the
            // outcome it already reported. A condition stage answers its condition by failing, and belongs
            // to that condition rather than to the run.
            if index <> StageIndex.Condition && ScopeReport.failed reported && not (cancelled || ct.IsCancellationRequested) then
                let published = pipeline |> Option.map (_.Producers >> ExecutionState.values) |> Option.defaultValue ProducerValues.empty

                match FailureContext.runHandlers stage.OnFailure (FailureContext.ofReport published reported) with
                | [] -> ()
                | raised ->
                    for failure in raised do
                        printError stage (FailureCause.describe failure.Cause)

                    // On the stage's own report, behind the cause it failed with, so a cause a handler raised
                    // travels to the pipeline alongside it and a reader of `ScopeReports.propagated` sees both.
                    // A cause raised by a *pipeline* handler propagates nowhere: `FailureContext.handlerReport`
                    // is where that asymmetry is written down.
                    reported <- { reported with Failures = reported.Failures @ raised }

            timings
            |> Option.iter (Internal.handleTimings stage reported.Outcome stageSw)

            reports
            |> Option.iter (ScopeReports.add reported)

        reported
    and internal stepHandler args step: Async<bool> = async {
        let sw = Stopwatch.StartNew()

        args.escapedPrefix + " started" + (if args.parallelism.IsSome then " in parallel -->" else "")
        |> Markup.grey
        |> vprintn args.stage

        let! isSuccess =
            match step with
            | Step.StepFn(label, fn) -> async {
                match! fn args.stepStage (LanguagePrimitives.Int32WithMeasure args.i) with
                | Ok _ -> return true
                | Error e ->
                    // A legacy step reports its failure as text, and the text is the whole of its cause.
                    args.evidence
                    |> StepEvidence.addFailure { Index = args.i; Label = label; Cause = FailureCause.Reported e }
                    if not (String.IsNullOrEmpty e) then
                        printError args.stage (
                            if args.parallelism.IsNone && getNoPrefixForStep args.stage
                            then e
                            else $"{args.escapedPrefix} {e}"
                            )
                    return false
                }
            // A structured outcome becomes a string here and nowhere earlier: the
            // print site is the only reader that needs one.
            | Step.Operation(label, operation) -> async {
                let! outcome =
                    operation {
                        Stage = args.stepStage
                        StepIndex = LanguagePrimitives.Int32WithMeasure args.i
                    }

                match outcome with
                | StepOutcome.Completed -> return true
                | StepOutcome.Failed cause ->
                    args.evidence |> StepEvidence.addFailure { Index = args.i; Label = label; Cause = cause }
                    FailureCause.describe cause
                    |> fun message ->
                        if args.parallelism.IsNone && getNoPrefixForStep args.stage
                        then message
                        else $"{args.escapedPrefix} {message}"
                    |> printError args.stage
                    return false
                }
            | Step.StepOfStage subStage -> async {
                let subStage = { subStage with ParentContext = ValueSome(StageParent.Stage args.stepStage) }
                let report = run subStage (StageIndex.Step args.i) args.linkedStepCts.Token
                args.evidence |> StepEvidence.addNested report
                if not args.stage.ContinueStageOnFailure then
                    args.evidence |> StepEvidence.addExceptions report.Exceptions
                return ScopeReport.continues report
            }
        let color = if isSuccess then Markup.grey else Markup.red
        let shouldCancelStage = not isSuccess && not args.stage.ContinueStepsOnFailure && not args.stepErrorCts.IsCancellationRequested
        [
            args.escapedPrefix
            if args.parallelism.IsSome then "finished in parallel."
            else "finished."
            $"{sw.ElapsedMilliseconds}ms."
            if shouldCancelStage then
                "Stage policy triggered cancellation."
        ]
        |> String.concat " "
        |> color
        |> (if shouldCancelStage then nprintn else vprintn) args.stage
        if shouldCancelStage then args.stepErrorCts.Cancel()
        return isSuccess
    }
module PipelineContext =
    open System.Text

    let runStagesWithFailFast (ctx: PipelineContext) (failFast: bool) (cancelToken: CancellationToken) (stages: StageContext seq) =
        let stages =
            stages
            |> Seq.map (fun stage -> { stage with ParentContext = ValueSome(StageParent.Pipeline ctx) })
            |> Seq.toList
        let mutable i = 0
        let mutable hasError = false
        let stageExns = ResizeArray<exn>()
        while i < stages.Length && (not failFast || not hasError) do
            let stage = stages[i]
            let report = StageContext.run stage (StageIndex.Stage i) cancelToken
            stageExns.AddRange report.Exceptions
            hasError <- hasError || report.Propagates
            i <- i + 1
        hasError, stageExns

    let runStages (ctx: PipelineContext) (cancelToken: CancellationToken) (stages: StageContext seq) = runStagesWithFailFast ctx false cancelToken stages

    /// <summary>
    /// Runs every stage of <paramref name="this"/> under <paramref name="cancellationToken"/>, and raises on
    /// failure or cancellation.
    /// </summary>
    /// <remarks>
    /// A cancellation of <paramref name="cancellationToken"/> acts as the pipeline's own <c>timeout</c> does: the
    /// stages observe a cancellation, a process started by a step is killed with its tree, and the run raises
    /// <see cref="T:Partas.Build.ErrorHandling.PipelineCancelledException"/>. Skips <c>DependencyPlan.validate</c>,
    /// as <c>run</c> does.
    /// </remarks>
    let runWith (cancellationToken: CancellationToken) (this: PipelineContext) =
        Console.InputEncoding <- Encoding.UTF8
        Console.OutputEncoding <- Encoding.UTF8
        StageTimings.clear this.Timings
        ScopeReports.clear this.Reports
        // Execution state is invocation-local: a second run of the same pipeline value executes its
        // producers again rather than reading what the first one published.
        ExecutionState.clear this.Producers

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
        use cts = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
        cts.CancelAfter timeoutForPipeline
        let mutable hasErrors = false

        // Once per failed run, after every stage of it has reported to its own handlers. The failure the
        // pipeline ends with is the first that reached it, whichever scope produced it; `raised` carries the
        // exception of a run a stage ended by raising, which leaves no cause behind.
        let mutable reportedFailure = false

        let reportFailure (raised: exn list) =
            if not reportedFailure then
                reportedFailure <- true
                let propagated = ScopeReports.propagated this.Reports
                let exns = List.ofSeq pipelineExns @ raised

                let context: FailureContext = {
                    Scope = this.Name
                    Address = ScopeAddress.root
                    Outcome = StageContext.Internal.getOutcome true false exns propagated
                    Failures = propagated
                    Exceptions = exns
                    Nested = ScopeReports.stages this.Reports
                    Published = ExecutionState.values this.Producers
                }

                match FailureContext.runHandlers this.OnFailure context with
                | [] -> ()
                | raised ->
                    for failure in raised do
                        PipelineContext.printError this (FailureCause.describe failure.Cause)

                    this.Reports |> ScopeReports.add (FailureContext.handlerReport context raised)

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

            match ex with
            | :? PipelineCancelledException -> ()
            | _ -> reportFailure [ ex ]

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

        if hasErrors || pipelineExns.Count > 0 then reportFailure []

        if pipelineExns.Count > 0 then
            for exn in pipelineExns do
                let innerMessage = if exn.InnerException <> null then exn.InnerException.Message else ""
                PipelineContext.printError this (exn.Message + " " + innerMessage)
            // The cause comes off the structured evidence: the first failure that reached the pipeline, as the
            // exception it retained. The printed lines above are the rendering of the same thing.
            let cause =
                match ScopeReports.propagated this.Reports with
                | failure :: _ -> FailureCause.toException failure.Cause
                | [] -> pipelineExns[0]
            raise (PipelineFailedException("Pipeline is failed because of exception", cause))
        else if hasErrors then
            let message = "Pipeline is failed because result is not indicating as successful"
            PipelineContext.printError this message
            // A step that failed without raising leaves a cause and no exception. The cause travels here all
            // the same: a caller reads what failed off the exception rather than off the log.
            match ScopeReports.propagated this.Reports with
            | failure :: _ -> raise (PipelineFailedException(message, FailureCause.toException failure.Cause))
            | [] -> raise (PipelineFailedException message)

    /// <summary>Runs every stage of <paramref name="this"/> and raises on failure or cancellation.</summary>
    /// <remarks>
    /// Skips <c>DependencyPlan.validate</c>: the command path validates placement before this runs and never
    /// reaches it for <c>--explain</c>, but a caller invoking this directly gets no such check, and an
    /// arrangement <c>DependencyPlan.validate</c> would reject — a producer placed under a <c>parallel'</c> or
    /// <c>shuffleExecuteSequence</c> scope, say — runs instead of failing at validation.
    /// </remarks>
    let run (this: PipelineContext) = runWith CancellationToken.None this

