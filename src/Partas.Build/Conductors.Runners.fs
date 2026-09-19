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

    /// The step an operation is executing in, while it is executing.
    [<Struct>]
    type internal InFlightStep = {
        index: int
        label: string voption
        prefix: string
    }

    /// <summary>Where one attempt of a stage collects what its steps produce.</summary>
    /// <remarks>
    /// Steps write here as they produce evidence. A failing step cancels its own scope before it returns, and
    /// the cancelled <c>async</c> discards its result: what a step hands back at the end never reaches the
    /// stage. Every write is serialised, for the steps of a parallel stage.
    /// </remarks>
    type internal StepEvidence = {
        sync: obj
        failures: ResizeArray<StepFailure>
        nested: ResizeArray<ScopeReport>
        exns: ResizeArray<exn>
    }

    module internal StepEvidence =
        let over (failures, nested, exns) = { sync = obj (); failures = failures; nested = nested; exns = exns }
        let addFailure (failure: StepFailure) (evidence: StepEvidence) = lock evidence.sync (fun () -> evidence.failures.Add failure)
        let addNested (report: ScopeReport) (evidence: StepEvidence) = lock evidence.sync (fun () -> evidence.nested.Add report)
        let addExceptions (errors: exn seq) (evidence: StepEvidence) = lock evidence.sync (fun () -> evidence.exns.AddRange errors)

    type internal StepHandlerParameters = {
        operationInFlight: InFlightStep voption ref
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
        let getOutcome isActive isSuccess (stepExns: ResizeArray<exn>) (stepFailures: ResizeArray<StepFailure>) =
            if not isSuccess then
                stepExns
                |> Seq.tryHead
                |> Option.map _.Message
                |> Option.orElseWith (fun () -> stepFailures |> Seq.tryHead |> Option.map (_.Cause >> FailureCause.summarise))
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
        let stepExns = ResizeArray<exn>()
        // The causes of the attempt being reported, held whatever `ContinueStageOnFailure` decides, and the
        // reports of the sub-stages that attempt ran.
        let stepFailures = ResizeArray<StepFailure>()
        let nestedReports = ResizeArray<ScopeReport>()
        let evidence = StepEvidence.over (stepFailures, nestedReports, stepExns)
        let mutable outcome = StageOutcome.Succeeded
        let isActive = stage.IsActive stage
        let pipeline = getParentPipeline stage
        let timings = Internal.getTimings pipeline index stage
        // Sub-stages read their parent's ordinal off the value given to them as `ParentContext`.
        let stage =
            match timings with
            | Some(_, _, order) -> { stage with TimingOrder = ValueSome order }
            | None -> stage

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
                stepExns.Clear()
                stepFailures.Clear()
                nestedReports.Clear()

                match capturedBefore with
                | ValueSome(capture, count) -> OutputCapture.trimTo count capture
                | ValueNone -> ()

                match producedBefore with
                | Some(producers, values) -> ExecutionState.resetTo values producers
                | None -> ()

                let mutable isStageSoftCancelled = false

                // The step the operation this attempt is inside belongs to, while it is inside one. A value
                // still here once the attempt has unwound belongs to an operation a token ended.
                let operationInFlight: InFlightStep voption ref = ref ValueNone

                use stepErrorCts = new CancellationTokenSource()
                use linkedStepErrorCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stepErrorCts.Token)
                use linkedCts = CancellationTokenSource.CreateLinkedTokenSource(linkedStepErrorCts.Token, ct)

                use stepCts = new CancellationTokenSource(timeoutForStep)
                use linkedStepCts = CancellationTokenSource.CreateLinkedTokenSource(stepCts.Token, linkedCts.Token)

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

                        try
                            try
                            return! stepHandler {
                                operationInFlight = operationInFlight
                                stage = stage
                                stepStage = stepStage
                                parallelism = parallelism
                                i = i
                                escapedPrefix = escapedPrefix
                                linkedStepCts = linkedStepCts
                                evidence = evidence
                                stepErrorCts = stepErrorCts
                            } step
                            with
                            | :? PipelineCancelledException as ex ->
                                raise ex
                                return false
                            | :? PipelineFailedException as ex ->
                                raise ex
                                return false
                            | :? StepSoftCancelledException as ex ->
                                $"{escapedPrefix} {Markup.escape ex.Message}"
                                |> Markup.yellow
                                |> nprintn stage
                                return true
                            | :? StageSoftCancelledException as ex ->
                                $"{escapedPrefix} {Markup.escape ex.Message}"
                                |> Markup.yellow
                                |> nprintn stage
                                isStageSoftCancelled <- true
                                return true
                            | ex ->
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
                    let ts =
                        // Async.StartChild is what applies timeoutForStep, and it starts the work there and then, so it
                        // has to happen inside the handler. Doing it while producing the sequence instead lets the
                        // throttle pull -- and therefore start -- one more step than it is meant to have in flight.
                        let inline asyncHandler step = async {
                            if stage.ContinueStepsOnFailure || isSuccess then
                                let! child = Async.StartChild(step, timeoutForStep)
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
                | :? PipelineCancelledException as ex -> FAIL(); raise ex
                | :? PipelineFailedException as ex -> FAIL(); raise ex
                | _ when isStageSoftCancelled -> SUCCESS()
                | ex ->
                    FAIL()
                    if linkedCts.Token.IsCancellationRequested && not stepErrorCts.IsCancellationRequested then
                        $"{buildCurrentStepPrefix stage |> Markup.escape}> stage is cancelled or timed-out."
                        |> Markup.yellow
                        |> nprintn stage
                    else if not stepErrorCts.IsCancellationRequested then
                        $"{buildCurrentStepPrefix stage |> Markup.escape}> stage's step failed."
                        |> Markup.red
                        |> printn
                        AnsiConsole.WriteException ex

                // Which token fired decides this, and only the runner holds both: `cts` is the budget this
                // stage was given, so its expiry is a failure of this stage and reports
                // `FailureCause.TimedOut`. `ct` belongs to an ancestor and `stepErrorCts` to stage policy;
                // an operation either of those ended stays a cancellation, reported as one above.
                match operationInFlight.Value with
                | ValueSome inFlight when
                    cts.IsCancellationRequested
                    && not ct.IsCancellationRequested
                    && not stepErrorCts.IsCancellationRequested
                    ->
                    let message = FailureCause.describe FailureCause.TimedOut
                    let line = if parallelism.IsNone && getNoPrefixForStep stage then message else $"{inFlight.prefix} {message}"
                    FAIL()
                    printError stage line
                    evidence |> StepEvidence.addFailure { Index = inFlight.index; Label = inFlight.label; Cause = FailureCause.TimedOut }
                    if not stage.ContinueStageOnFailure then stepExns.Add(Exception line)
                | _ -> ()

                if not isSuccess && retriesLeft > 0 && not cts.IsCancellationRequested && not ct.IsCancellationRequested then
                    $"%s{getNamePath stage |> Markup.escape} failed. Retrying, {retriesLeft} attempt(s) left."
                    |> Markup.yellow
                    |> nprintn stage
                    retriesLeft <- retriesLeft - 1
                    attempting <- true

                Logging.stageCondition.finished stage stageSw isSuccess index

        finally // finished stage run; cleanup/report/post
            pipeline |> Option.iter _.RunAfterEachStage(stage)

            outcome <- Internal.getOutcome isActive isSuccess stepExns stepFailures

            timings
            |> Option.iter (Internal.handleTimings stage outcome stageSw)

        {
            Name = stage.Name
            Outcome = outcome
            Propagates = not (stage.ContinueStageOnFailure || isSuccess)
            Failures = List.ofSeq stepFailures
            Exceptions = List.ofSeq stepExns
            Nested = List.ofSeq nestedReports
        }
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
                // A cancelled `async` runs neither a handler nor a continuation,
                // so an operation ended by a token leaves its step behind here
                // and the attempt classifies it once the run has unwound.
                args.operationInFlight.Value <- ValueSome { index = args.i; label = label; prefix = args.escapedPrefix }
                let! outcome =
                    operation {
                        Stage = args.stepStage
                        StepIndex = LanguagePrimitives.Int32WithMeasure args.i
                    }
                args.operationInFlight.Value <- ValueNone

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
            ScopeReports.add report ctx.Reports
            stageExns.AddRange report.Exceptions
            hasError <- hasError || report.Propagates
            i <- i + 1
        hasError, stageExns

    let runStages (ctx: PipelineContext) (cancelToken: CancellationToken) (stages: StageContext seq) = runStagesWithFailFast ctx false cancelToken stages

    let rec run (this: PipelineContext) =
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
        use cts = new CancellationTokenSource(timeoutForPipeline)
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

