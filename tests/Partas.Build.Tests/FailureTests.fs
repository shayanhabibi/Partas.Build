/// <summary>What a scope reports about itself: its own outcome, the propagation decision taken from it, and
/// the causes its steps left.</summary>
/// <remarks>
/// Every assertion here reads <see cref="T:Partas.Build.ScopeReport"/>. A failure a caller has to recover by
/// reading a rendered line or a timing row is the defect these cover.
/// </remarks>
module Partas.Build.Tests.FailureTests

open System
open System.Text.Json
open System.Threading
open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.Tests.Helpers

/// A stage whose steps write nowhere, so a command it runs leaves the test log alone.
let private silently (stage: StageContext) = { stage with Output = ValueSome StageOutput.Silent }

/// The report of the scope named <paramref name="name"/>, wherever it sits in <paramref name="reports"/>.
let private scope (reports: ScopeReports) name =
    match ScopeReports.all reports |> List.tryFind (fun report -> report.Name = name) with
    | Some report -> report
    | None -> failtestf "no scope named %s among %A" name [ for report in ScopeReports.all reports -> report.Name ]

/// The single cause <paramref name="report"/> recorded.
let private soleCause (report: ScopeReport) =
    match report.Failures with
    | [ failure ] -> failure
    | other -> failtestf "one failure should be recorded for %s; got %A" report.Name other

[<Tests>]
let tests =
    testList "failures" [
        test "a suppressed producer stays failed while independent work continues" {
            let log = ResizeArray<string>()
            let source: Producer<int> =
                Producer.define "compile" (InputSpec.ret ()) DependencySpec.empty (fun _ _ ->
                    Operation.ofAsync (async {
                        log.Add "compile"
                        return raise (Exception "the compiler failed")
                    }))

            let work =
                pipeline "work" {
                    quiet
                    Producer.stage source |> StageContext.setContinueStageOnFailure true
                    Stage.consuming "use" (DependencySpec.require source) (fun _ -> Operation.ofAsync (async { log.Add "use" }))
                    stage "package" { run (fun (_: StageContext) -> log.Add "package") }
                }

            let built = command "build" { work }
            Expect.equal (quietly (fun () -> built.Parse("").Invoke())) 0 "suppression lets the rest of the pipeline continue"
            Expect.sequenceEqual log [ "compile"; "package" ] "the producer ran, its consumer did not, and the independent stage did"

            let producer = scope work.Reports "compile"
            Expect.isTrue (ScopeReport.failed producer) "the producer's own outcome stays failed"
            Expect.isFalse producer.Propagates "and the decision taken from it stops the failure at the suppressing scope"

            match (soleCause producer).Cause with
            | FailureCause.Raised error -> Expect.equal error.Message "the compiler failed" "the exception itself is the evidence"
            | other -> failtestf "the producer should retain the exception it raised; got %A" other

            Expect.equal (scope work.Reports "use").Outcome StageOutcome.Skipped "a consumer of a failed producer stays blocked"
            Expect.isEmpty (ScopeReports.propagated work.Reports) "a suppressed cause reaches the pipeline nowhere"
        }

        test "a suppressed step keeps its exception on the report while the stage propagates nothing" {
            let built =
                stage "flaky" {
                    continueStageOnFailure
                    run (fun (_: StageContext) -> raise (InvalidOperationException "detonated"))
                }

            let report = quietly (fun () -> reportStage built)

            Expect.isEmpty report.Exceptions "the policy keeps the exception out of what the enclosing scope receives"
            Expect.isTrue (ScopeReport.continues report) "and lets that scope carry on"
            Expect.isTrue (ScopeReport.failed report) "while the stage's own outcome stays failed"

            match (soleCause report).Cause with
            | FailureCause.Raised error ->
                Expect.isTrue (error :? InvalidOperationException) $"the exception survives as itself; got {error.GetType().Name}"
                Expect.equal error.Message "detonated" "carrying the message it was raised with"
            | other -> failtestf "a suppressed step should retain its exception; got %A" other
        }

        test "an unacceptable exit code reports the command and the code, and reads nothing out of stderr" {
            let fixture = ProcessFixture.command [ "text"; "3" ]
            let report = quietly (fun () -> reportStage (silently (stage "release" { runOperation (execute fixture) })))

            Expect.isTrue report.Propagates "an exit code the stage rejects fails it"

            match (soleCause report).Cause with
            | FailureCause.Command (named, exitCode, captured) ->
                Expect.equal exitCode 3 "the code the process exited with is the evidence"
                Expect.stringContains named "ProcessFixture.dll" "alongside the command that produced it"
                Expect.equal captured ValueNone "a streamed command wrote its output as it arrived and holds none of it"
            | other -> failtestf "a rejected exit code should report itself as one; got %A" other
        }

        test "a checked capture reports the text it captured as the evidence of its exit code" {
            let fixture = ProcessFixture.command [ "text"; "3" ]
            let checkedCapture = executeCapture fixture |> Operation.map ignore
            let report = quietly (fun () -> reportStage (silently (stage "release" { runOperation checkedCapture })))

            match (soleCause report).Cause with
            | FailureCause.Command (_, exitCode, ValueSome captured) ->
                Expect.equal exitCode 3 "the code travels with the failure"
                Expect.equal captured.Stderr "err-one\n\nerr-two" "and the raw stderr the process wrote"
                Expect.equal captured.Stdout "alpha\n\nbeta\n" "alongside the stdout it wrote"
            | other -> failtestf "a checked capture should carry its capture; got %A" other
        }

        test "a parse failure reports the exception it raised rather than a message about it" {
            let parsed =
                executeCapture (ProcessFixture.command [ "text"; "0" ])
                |> Operation.map (fun result -> Int32.Parse result.Stdout |> ignore)

            let report = quietly (fun () -> reportStage (silently (stage "version" { runOperation parsed "read the version" })))

            let failure = soleCause report
            Expect.equal failure.Label (ValueSome "read the version") "the step is named by the label it was declared with"
            Expect.equal failure.Index 0 "and by its position among the stage's steps"

            match failure.Cause with
            | FailureCause.Raised error ->
                Expect.isTrue (error :? FormatException) $"the parse exception survives as itself; got {error.GetType().Name}"
            | other -> failtestf "a parse failure should retain its exception; got %A" other
        }

        test "a nested stage's report sits under the stage that ran it" {
            let built =
                stage "outer" {
                    stage "inner" { run (fun (_: StageContext) -> Error "inner said no") }
                }

            let report = quietly (fun () -> reportStage built)

            Expect.isTrue report.Propagates "the failure of a nested stage fails the stage containing it"
            Expect.isEmpty report.Failures "the outer stage produced no cause of its own"

            match report.Nested with
            | [ inner ] ->
                Expect.equal inner.Name "inner" "the nested report names the stage it covers"
                Expect.equal (soleCause inner).Cause (FailureCause.Reported "inner said no") "carrying what that stage reported"
            | other -> failtestf "one nested report should be recorded; got %A" other

            Expect.equal
                (ScopeReport.failures report |> List.map _.Cause)
                [ FailureCause.Reported "inner said no" ]
                "and the whole tree's causes read in pre-order"
        }

        test "a stage that fails its own guard is reported alongside its timing" {
            let work =
                pipeline "guarded" {
                    quiet
                    stage "required" { when' false; failIfIgnored }
                }

            quietly (fun () ->
                try PipelineContext.run work
                with :? PipelineFailedException -> ())

            Expect.equal
                [ for timing in StageTimings.ordered work.Timings -> timing.Name ] [ "required" ]
                "the stage records a timing row"
            Expect.equal
                [ for report in ScopeReports.all work.Reports -> report.Name ] [ "required" ]
                "and reports the same scope, so a reader of one finds it in the other"
        }

        test "the timing row of a stage an operation failed names the cause" {
            let work =
                pipeline "release" {
                    quiet
                    stage "sign" { runOperation (execute (ProcessFixture.command [ "text"; "3" ])) } |> silently
                }

            quietly (fun () ->
                try PipelineContext.run work
                with :? PipelineFailedException -> ())

            match StageTimings.ordered work.Timings with
            | [ timing ] ->
                match timing.Outcome with
                | StageOutcome.Failed error ->
                    Expect.stringContains error "exited with 3" "a step that failed without raising still names what failed"
                    Expect.isFalse (error.Contains "\n") "the row carries one line"
                | other -> failtestf "the stage should be recorded as failed; got %A" other
            | other -> failtestf "one timing row should be recorded; got %A" [ for timing in other -> timing.Name ]
        }

        test "the pipeline reads its cause off the evidence rather than off the line it printed" {
            let work =
                pipeline "release" {
                    quiet
                    stage "sign" { runOperation (execute (ProcessFixture.command [ "text"; "3" ])) } |> silently
                }

            let raised =
                quietly (fun () ->
                    try
                        PipelineContext.run work
                        None
                    with :? PipelineFailedException as ex -> Some ex)

            match raised with
            | None -> failtest "a rejected exit code should fail the pipeline"
            | Some ex ->
                match ex.InnerException with
                | :? OperationFailedException as failed ->
                    match failed.Cause with
                    | FailureCause.Command (_, exitCode, _) -> Expect.equal exitCode 3 "the pipeline's cause is the code the process exited with"
                    | other -> failtestf "the cause should be the command's; got %A" other
                | other -> failtestf "the pipeline should carry the structured cause; got %A" other
        }
    ]

/// <summary>What a scope's own failure handlers observe, and when they run at all.</summary>
/// <remarks>
/// A handler is the structured counterpart of reading the log: every assertion here reads a
/// <see cref="T:Partas.Build.FailureContext"/>.
/// </remarks>
[<Tests>]
let handlers =
    /// A step that sleeps past any timeout a test gives its stage.
    let sleeping = fun (_: StageContext) -> async { do! Async.Sleep 30000 }

    testList "handlers" [
        test "a handler runs once, after the stage has exhausted its retries" {
            let attempts = ResizeArray<int>()
            let handled = ResizeArray<FailureContext>()

            let built =
                stage "flaky" {
                    retry 2
                    onFailure handled.Add
                    run (fun (_: StageContext) ->
                        attempts.Add attempts.Count
                        Error "no")
                }

            let report = quietly (fun () -> reportStage built)

            Expect.equal attempts.Count 3 "a retry of two gives three attempts"
            Expect.equal handled.Count 1 "the handler runs once for the failed execution rather than once per attempt"
            Expect.equal handled[0].Scope "flaky" "the handler is told which scope failed"
            Expect.equal handled[0].Address.Names [ "flaky" ] "and where that scope sits in the run"
            Expect.equal handled[0].Outcome report.Outcome "and what the scope reported"

            match handled[0].Primary with
            | ValueSome failure -> Expect.equal failure.Cause (FailureCause.Reported "no") "the primary cause is the last attempt's"
            | ValueNone -> failtest "a failed scope hands its handler the cause it failed with"

            Expect.isEmpty handled[0].Secondary "one failing step leaves one cause"
        }

        test "a stage a retry recovers runs no handler" {
            let mutable attempts = 0
            let mutable handled = 0

            let built =
                stage "flaky" {
                    retry 1
                    onFailure (fun _ -> handled <- handled + 1)
                    run (fun (_: StageContext) ->
                        attempts <- attempts + 1
                        if attempts = 1 then Error "not yet" else Ok())
                }

            let report = quietly (fun () -> reportStage built)

            Expect.equal attempts 2 "the second attempt ran"
            Expect.isFalse (ScopeReport.failed report) "and succeeded"
            Expect.equal handled 0 "a scope that ends successfully runs no handler"
        }

        test "handlers run inner before outer, each naming its own scope" {
            let order = ResizeArray<string>()

            let built =
                stage "outer" {
                    onFailure (fun context -> order.Add $"outer:%s{ScopeAddress.text context.Address}")
                    stage "inner" {
                        onFailure (fun context -> order.Add $"inner:%s{ScopeAddress.text context.Address}")
                        run (fun (_: StageContext) -> Error "inner said no")
                    }
                }

            quietly (fun () -> reportStage built) |> ignore

            Expect.sequenceEqual order [ "inner:outer/inner"; "outer:outer" ]
                "the scope nearest the failure reports first, and each address names the scopes enclosing it"
        }

        test "an inner handler runs during an outer attempt the retry then recovers" {
            let mutable attempts = 0
            let mutable inner = 0
            let mutable outer = 0

            let built =
                stage "outer" {
                    retry 1
                    onFailure (fun _ -> outer <- outer + 1)
                    stage "inner" {
                        onFailure (fun _ -> inner <- inner + 1)
                        run (fun (_: StageContext) ->
                            attempts <- attempts + 1
                            if attempts = 1 then Error "not yet" else Ok())
                    }
                }

            let report = quietly (fun () -> reportStage built)

            Expect.isFalse (ScopeReport.failed report) "the second attempt of the enclosing stage succeeded"
            Expect.equal inner 1 "the inner scope failed once and reported it once"
            Expect.equal outer 0 "the scope the retry recovered reports nothing"
        }

        test "a handler that raises leaves one more cause and keeps the original primary" {
            let mutable entered = 0

            let built =
                stage "release" {
                    onFailure (fun _ ->
                        entered <- entered + 1
                        raise (InvalidOperationException "the reporter is down"))
                    run (fun (_: StageContext) -> Error "unsigned")
                }

            let report = quietly (fun () -> reportStage built)

            Expect.equal entered 1 "a handler is not entered again for its own failure"

            match report.Failures with
            | [ primary; secondary ] ->
                Expect.equal primary.Cause (FailureCause.Reported "unsigned") "the scope's own cause stays primary"
                Expect.equal secondary.Index StepFailure.NoStep "a handler is no step of its scope"
                Expect.equal secondary.Label (ValueSome FailureContext.HandlerLabel) "and says so"

                match secondary.Cause with
                | FailureCause.Raised error -> Expect.equal error.Message "the reporter is down" "the handler's exception is the evidence"
                | other -> failtestf "a handler failure should retain its exception; got %A" other
            | other -> failtestf "the scope's cause and the handler's should both be recorded; got %A" other

            match report.Outcome with
            | StageOutcome.Failed error -> Expect.equal error "unsigned" "the outcome still reads the original failure"
            | other -> failtestf "the stage should be recorded as failed; got %A" other
        }

        test "a pipeline handler runs after the handlers of its stages and reads the pipeline's own failure" {
            let order = ResizeArray<string>()
            let observed = ResizeArray<FailureContext>()

            let work =
                pipeline "release" {
                    quiet
                    onFailure (fun context ->
                        order.Add "pipeline"
                        observed.Add context)
                    stage "sign" {
                        onFailure (fun _ -> order.Add "sign")
                        run (fun (_: StageContext) -> Error "unsigned")
                    }
                }

            quietly (fun () ->
                try PipelineContext.run work
                with :? PipelineFailedException -> ())

            Expect.sequenceEqual order [ "sign"; "pipeline" ] "the pipeline reports after every stage of the run"

            match observed |> List.ofSeq with
            | [ context ] ->
                Expect.equal context.Scope "release" "the pipeline handler names the pipeline"

                match context.Primary with
                | ValueSome failure -> Expect.equal failure.Cause (FailureCause.Reported "unsigned") "and reads the failure the run ended with"
                | ValueNone -> failtest "the pipeline's failure should reach its handler"

                Expect.equal [ for report in context.Nested -> report.Name ] [ "sign" ] "alongside the reports of the stages it ran"
            | other -> failtestf "one pipeline handler invocation should be recorded; got %i" other.Length
        }

        test "a cancellation reaching a stage from the invocation runs no handler" {
            let mutable handled = 0
            use cancellation = new CancellationTokenSource 500

            let built =
                stage "slow" {
                    onFailure (fun _ -> handled <- handled + 1)
                    run sleeping
                }

            let report = quietly (fun () -> StageContext.run built (StageIndex.Stage 0) cancellation.Token)

            Expect.isTrue (ScopeReport.failed report) "a cancelled stage did not succeed"
            Expect.equal handled 0 "the token that fired belongs to the invocation, which is no failure of the stage"
        }

        test "a stage's own timeout is its failure, and reaches its handler as one" {
            let handled = ResizeArray<FailureContext>()

            let built =
                stage "slow" {
                    timeout 1.0
                    onFailure handled.Add
                    run sleeping
                }

            quietly (fun () -> reportStage built) |> ignore

            match handled |> List.ofSeq with
            | [ context ] ->
                match context.Primary with
                | ValueSome failure -> Expect.equal failure.Cause FailureCause.TimedOut "the stage's own budget expired"
                | ValueNone -> failtest "a timed-out stage should hand its handler the timeout"
            | other -> failtestf "one handler invocation should be recorded; got %i" other.Length
        }

        test "a step timeout of the stage's own is its failure, and reaches its handler as one" {
            let handled = ResizeArray<FailureContext>()

            let built =
                stage "slow" {
                    timeoutForStep 1.0
                    onFailure handled.Add
                    run sleeping
                }

            quietly (fun () -> reportStage built) |> ignore

            match handled |> List.ofSeq with
            | [ context ] ->
                match context.Primary with
                | ValueSome failure -> Expect.equal failure.Cause FailureCause.TimedOut "the step budget the stage set expired"
                | ValueNone -> failtest "a stage whose step budget expired should hand its handler the timeout"
            | other -> failtestf "one handler invocation should be recorded; got %i" other.Length
        }

        test "a pipeline timeout cancels its stages and runs no handler" {
            let mutable stageHandled = 0
            let mutable pipelineHandled = 0

            let work =
                pipeline "release" {
                    quiet
                    timeout 1.0
                    onFailure (fun _ -> pipelineHandled <- pipelineHandled + 1)
                    stage "slow" {
                        onFailure (fun _ -> stageHandled <- stageHandled + 1)
                        run sleeping
                    }
                }

            quietly (fun () ->
                try PipelineContext.run work
                with :? PipelineCancelledException -> ())

            Expect.equal stageHandled 0 "the pipeline's budget is no failure of the stage it cancels"
            Expect.equal pipelineHandled 0 "and a cancelled run is no failure of the pipeline"
        }
    ]

/// The document the fixture writes, as the producer reads it.
type private Manifest = { Package: string; Version: string }

/// <summary>One run of the migration use case, and the counters its parts increment.</summary>
/// <remarks>Every side effect of the slice is a counter here: a test asserts what ran by reading them.</remarks>
type private Slice = {
    Command: System.CommandLine.Command
    Work: PipelineContext
    Manifest: Producer<Manifest>
    /// What the producer's captured document said, once per execution of the producer.
    Produced: ResizeArray<string>
    /// The version the consumer read, once per attempt.
    Attempts: ResizeArray<string>
    /// What the consumer's own handler was told.
    Handled: ResizeArray<FailureContext>
    /// What the pipeline's handler was told.
    Reported: ResizeArray<FailureContext>
}

/// <summary>The slice: a CLI option read by a producer that captures a document from a child process, a
/// consumer retrying over the typed value, and the handlers of both scopes.</summary>
let private slice () =
    let produced = ResizeArray<string>()
    let attempts = ResizeArray<string>()
    let handled = ResizeArray<FailureContext>()
    let reported = ResizeArray<FailureContext>()

    let tag = Input.option<string> "--tag" |> Input.def "v0.0.0"

    let manifest: Producer<Manifest> =
        Producer.define "manifest" (InputSpec.ofInput tag) DependencySpec.empty (fun tag _ ->
            ProcessFixture.command [ "echo"; $"""{{"package":"partas","version":"%s{tag}"}}""" ]
            |> executeCapture
            |> Operation.map (fun result ->
                produced.Add result.Stdout
                let document = JsonDocument.Parse result.Stdout
                {
                    Package = document.RootElement.GetProperty("package").GetString()
                    Version = document.RootElement.GetProperty("version").GetString()
                }))

    let work =
        pipeline "release" {
            quiet
            onFailure reported.Add
            stage "publish" {
                retry 2
                silentOutput
                onFailure handled.Add
                consumes (DependencySpec.require manifest) (fun manifest ->
                    Operation.ofAsync (async { attempts.Add manifest.Version })
                    |> Operation.bind (fun () -> execute (ProcessFixture.command [ "text"; "3" ])))
            }
        }

    {
        Command = command "release" { work }
        Work = work
        Manifest = manifest
        Produced = produced
        Attempts = attempts
        Handled = handled
        Reported = reported
    }

[<Tests>]
let vertical =
    testList "slice" [
        test "a CLI input reaches a producer, whose value outlives the retries of the consumer that failed on it" {
            let slice = slice ()

            Expect.equal (quietly (fun () -> slice.Command.Parse("--tag v9.9.9").Invoke())) 1
                "an exit code the consumer rejects fails the invocation"

            Expect.equal slice.Produced.Count 1 "the producer ran once, outside the scope the retries repeat"
            Expect.sequenceEqual slice.Attempts [ "v9.9.9"; "v9.9.9"; "v9.9.9" ]
                "each of the three attempts read the value the option asked the producer for"

            match slice.Handled |> List.ofSeq with
            | [ context ] ->
                Expect.equal context.Scope "publish" "the consumer's handler names the consumer"

                match context.Primary with
                | ValueSome failure ->
                    match failure.Cause with
                    | FailureCause.Command (_, exitCode, _) -> Expect.equal exitCode 3 "and carries the code the command exited with"
                    | other -> failtestf "the consumer failed on an exit code; got %A" other
                | ValueNone -> failtest "the consumer's failure should reach its handler"

                Expect.equal (context.TryGetOutput slice.Manifest) (ValueSome { Package = "partas"; Version = "v9.9.9" })
                    "the handler reads the typed value the producer published"
            | other -> failtestf "the consumer should report once; got %i" other.Length

            match slice.Reported |> List.ofSeq with
            | [ context ] ->
                Expect.equal context.Scope "release" "the pipeline reports under its own name"
                Expect.equal [ for report in context.Nested -> report.Name ] [ "manifest"; "publish" ]
                    "carrying the scopes the run executed, the producer's own stage among them"
            | other -> failtestf "the pipeline should report once; got %i" other.Length

            let recorded = ScopeReports.all slice.Work.Reports

            Expect.equal [ for report in recorded -> report.Name, report.Outcome ]
                [ "manifest", StageOutcome.Succeeded
                  "publish", StageOutcome.Failed (FailureCause.summarise (FailureCause.Command (Cmd.toLogString (ProcessFixture.command [ "text"; "3" ]), 3, ValueNone))) ]
                "the run leaves a report per scope, each carrying what that scope did"
        }

        test "the same command under --help and --explain runs no producer, no process and no handler" {
            let slice = slice ()
            // `--help` belongs to the root command, which is where a command built this way is invoked from.
            let root = System.CommandLine.RootCommand "build"
            root.Subcommands.Add slice.Command

            Expect.equal (quietly (fun () -> root.Parse("release --help").Invoke())) 0 "help succeeds"
            Expect.equal (quietly (fun () -> root.Parse("release --explain").Invoke())) 0 "explain succeeds"

            Expect.isEmpty slice.Produced "neither path executes the producer, so neither starts its process"
            Expect.isEmpty slice.Attempts "nor the consumer that reads it"
            Expect.isEmpty slice.Handled "and a run that never failed reports to nobody"
            Expect.isEmpty slice.Reported "at either scope"
        }
    ]
