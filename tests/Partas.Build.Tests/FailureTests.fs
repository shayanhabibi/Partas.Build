/// <summary>What a scope reports about itself: its own outcome, the propagation decision taken from it, and
/// the causes its steps left.</summary>
/// <remarks>
/// Every assertion here reads <see cref="T:Partas.Build.ScopeReport"/>. A failure a caller has to recover by
/// reading a rendered line or a timing row is the defect these cover.
/// </remarks>
module Partas.Build.Tests.FailureTests

open System
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
