/// <summary>
/// The shared process executor: argument transport, draining, completion and cancellation.
/// </summary>
/// <remarks>
/// Everything here runs <c>tests/Fixtures/ProcessFixture</c>, whose output, exit code and lifetime are
/// dictated by its arguments, so an assertion is against known bytes rather than against whatever the
/// installed tooling prints this week.
/// </remarks>
module Partas.Build.Tests.ExecutionTests

open System
open System.ComponentModel
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Expecto
open Partas.Build
open Partas.Build.Tests.Helpers

let private startInfo (arguments: string list) = Cmd.toStartInfo ValueNone Map.empty (ProcessFixture.command arguments)

let private wait (work: Task<'T>) = work.GetAwaiter().GetResult()

/// Runs <paramref name="work"/> expecting it to cancel; a returned value is the failure.
let private expectCancelled (work: unit -> 'T) message =
    try
        work () |> ignore
        failtestf "%s: the call returned a value instead of cancelling" message
    with :? OperationCanceledException ->
        ()

/// The arguments any transport has to deliver unchanged.
let private awkward = [ "a b"; "plain"; "say \"hi\""; "trailing\\"; "back\\\\slash"; "" ]

[<Tests>]
let tests =
    testList "execution" [
        test "a captured command returns its stdout and stderr exactly as written" {
            let result = wait (ProcessExecutor.capture (startInfo [ "text"; "3" ]) CancellationToken.None ignore)

            Expect.equal result.Stdout "alpha\n\nbeta\n" "stdout should arrive as the child wrote it, blank line and trailing newline included"
            Expect.equal result.Stderr "err-one\n\nerr-two" "stderr should arrive as the child wrote it, with no newline added to the end"
            Expect.equal result.ExitCode 3 "the child's exit code should be reported"
            Expect.isFalse (result.Stdout.Contains "[") "raw capture precedes every display prefix"
        }

        test "a large simultaneous stdout and stderr both drain completely" {
            let lines = 5000
            let result = wait (ProcessExecutor.capture (startInfo [ "flood"; string lines ]) CancellationToken.None ignore)

            let count (text: string) = text.Split '\n' |> Array.filter (String.IsNullOrEmpty >> not) |> Array.length

            Expect.equal result.ExitCode 0 "the child should finish rather than block on a full pipe"
            Expect.equal (count result.Stdout) lines "every stdout line should be drained"
            Expect.equal (count result.Stderr) lines "every stderr line should be drained"
        }

        test "an argument containing whitespace and quotes reaches the child intact" {
            let result = wait (ProcessExecutor.capture (startInfo ("args" :: awkward)) CancellationToken.None ignore)

            // The child prefixes each argument with its length, so a re-split argument shows up as an extra line
            // and a mangled one as a different length.
            let delivered = [ for line in result.Stdout.Split '\n' do if line <> "" then yield line.Substring (line.IndexOf ':' + 1) ]

            Expect.equal result.ExitCode 0 "the child should accept its arguments"
            Expect.equal delivered awkward "each argument should reach the child as one argument, unchanged"
        }

        test "quoting one argument follows the MSVCRT rules" {
            Expect.equal (ProcessExecutor.Arguments.quote "plain") "plain" "a value with nothing to escape should be left alone"
            Expect.equal (ProcessExecutor.Arguments.quote "a b") "\"a b\"" "whitespace should be quoted"
            Expect.equal (ProcessExecutor.Arguments.quote "say \"hi\"") "\"say \\\"hi\\\"\"" "an embedded quote should be escaped"
            Expect.equal (ProcessExecutor.Arguments.quote "a b\\") "\"a b\\\\\"" "a trailing backslash run should be doubled before the closing quote"
            Expect.equal (ProcessExecutor.Arguments.quote "trailing\\") "trailing\\" "a backslash reaching no quote needs no quoting at all"
            Expect.equal (ProcessExecutor.Arguments.quote "a\\\\b") "a\\\\b" "a backslash run away from a quote should be left alone"
            Expect.equal (ProcessExecutor.Arguments.quote "") "\"\"" "an empty argument should survive as an empty argument"
        }

        test "the working directory and environment reach the child" {
            let directory = IO.Path.GetTempPath().TrimEnd (IO.Path.DirectorySeparatorChar)
            let command = ProcessFixture.command [ "env"; "PARTAS_FIXTURE" ]
            let result = wait (ProcessExecutor.capture (Cmd.toStartInfo (ValueSome directory) (Map [ "PARTAS_FIXTURE", "set" ]) command) CancellationToken.None ignore)

            Expect.equal (result.Stdout.Split '\n' |> Array.item 1) "set" "the environment variable should reach the child"
            Expect.stringContains (result.Stdout.Split '\n' |> Array.item 0) (IO.Path.GetFileName directory) "the working directory should be the one asked for"
        }

        test "a process that cannot be started raises rather than reporting an exit code" {
            let missing = Cmd.toStartInfo ValueNone Map.empty (Cmd.ofList "partas-build-no-such-executable" [ "argument" ])

            Expect.throwsT<Win32Exception>
                (fun () -> wait (ProcessExecutor.capture missing CancellationToken.None ignore) |> ignore)
                "a failure to start is not an exit code and must not be reported as one"
        }

        test "a cancelled capture raises rather than returning a result" {
            use cancellation = new CancellationTokenSource 300
            let watch = Stopwatch.StartNew()

            expectCancelled
                (fun () -> wait (ProcessExecutor.capture (startInfo [ "sleep"; "30000" ]) cancellation.Token ignore))
                "a cancelled capture"

            watch.Stop()
            Expect.isLessThan watch.ElapsedMilliseconds 20000L "the token should kill the process rather than wait it out"
        }

        test "a cancelled stream raises rather than returning an exit code" {
            use cancellation = new CancellationTokenSource 300
            let announced = ref 0

            expectCancelled
                (fun () ->
                    wait (ProcessExecutor.stream (startInfo [ "sleep"; "30000" ]) OutputPolicy.Inherit cancellation.Token (fun () -> incr announced)))
                "a cancelled stream"

            Expect.equal announced.Value 1 "the caller should be told once, before the kill"
        }

        test "a killed process reports its exit code to a caller that asked for completion" {
            use cancellation = new CancellationTokenSource 300
            let watch = Stopwatch.StartNew()
            let result = wait (ProcessExecutor.captureToExit (startInfo [ "sleep"; "30000" ]) cancellation.Token ignore)
            watch.Stop()

            Expect.notEqual result.ExitCode 0 "the exit code of a killed process is the evidence the stage runner maps"
            Expect.isLessThan watch.ElapsedMilliseconds 20000L "the token should kill the process rather than wait it out"
        }

        test "a streamed command hands over each line and retains no text" {
            let received = ResizeArray<string * string>()
            let policy = OutputPolicy.Lines((fun line -> received.Add ("out", line)), (fun line -> received.Add ("err", line)))
            let exitCode = wait (ProcessExecutor.stream (startInfo [ "text"; "0" ]) policy CancellationToken.None ignore)

            Expect.equal exitCode 0 "the exit code is all a streamed command reports"
            Expect.contains received ("out", "alpha") "stdout should arrive line by line"
            Expect.contains received ("err", "err-two") "stderr should arrive line by line"
            Expect.contains received ("out", "") "a blank line is a line; dropping it is the caller's policy"

            // What keeps an ordinary command from acquiring a hidden full-output buffer is the type: there is
            // nowhere for the text to go once a line has been handed over.
            let executor = typeof<CommandResult>.Assembly.GetType "Partas.Build.ProcessExecutor"
            Expect.equal (executor.GetMethod "stream").ReturnType typeof<Task<int>> "streaming yields an exit code and nothing else"
            Expect.equal (executor.GetMethod "streamToExit").ReturnType typeof<Task<int>> "streaming to completion yields an exit code and nothing else"
        }
    ]

// Deferred command operations: their failure policy, the evidence a failure carries, and what cancellation
// does to them. An operation is driven through `Operation.toStepOutcome` wherever the assertion is about what
// a step reports, because a rendered failure goes to the console while the structure is what the runner reads.

open Partas.Build.Internal

/// A stage whose steps write nowhere, so a streamed command leaves the test log alone.
let private silent (configure: StageContext -> StageContext) =
    { StageContext.create "operations" with Output = ValueSome StageOutput.Silent } |> configure

let private runtime (stage: StageContext) = { Stage = stage; StepIndex = 0<stepIndex> }

/// Runs an operation as a step of its own and answers the value it produced.
let private perform (stage: StageContext) (operation: Operation<'T>) =
    Async.RunSynchronously (operation.Execute (runtime stage))

/// Runs an operation as a step of its own and answers the outcome the runner would read.
let private outcomeOf (stage: StageContext) (operation: Operation<unit>) =
    Async.RunSynchronously (Operation.toStepOutcome operation (runtime stage))

/// Runs <paramref name="work"/> under a token that cancels, expecting cancellation rather than a value.
let private expectCancellation (token: CancellationToken) (work: Async<'T>) message =
    try
        Async.RunSynchronously (work, cancellationToken = token) |> ignore
        failtestf "%s: the operation produced a value instead of cancelling" message
    with :? OperationCanceledException ->
        ()

let private missing = Cmd.ofList "partas-build-no-such-executable" [ "argument" ]

/// Long enough that finishing on its own would be indistinguishable from a hang, and a grandchild of the
/// process the runner starts on Windows: `cmd` is what gets killed, `ping` is what has to die with it.
let private sleeps =
    if Runtime.InteropServices.RuntimeInformation.IsOSPlatform Runtime.InteropServices.OSPlatform.Windows
    then Cmd.ofString "cmd /c ping -n 30 127.0.0.1"
    else Cmd.ofList "sh" [ "-c"; "sleep 30" ]

let private sleepProcessName =
    if Runtime.InteropServices.RuntimeInformation.IsOSPlatform Runtime.InteropServices.OSPlatform.Windows then "PING" else "sleep"

let private sleepsAlive () = Diagnostics.Process.GetProcessesByName sleepProcessName |> Array.length

[<Tests>]
let operations =
    testList "operations" [
        test "an attempted capture answers an unacceptable exit code as a result" {
            let result = perform (silent id) (attemptCapture (ProcessFixture.command [ "text"; "3" ]))

            Expect.equal result.ExitCode 3 "a rejected exit code is still a result the caller branches on"
            Expect.equal result.Stdout "alpha\n\nbeta\n" "the raw text keeps its blank line and its trailing newline"
            Expect.equal result.Stderr "err-one\n\nerr-two" "stderr arrives apart from stdout, as the child wrote it"
        }

        test "a checked capture fails on an unacceptable exit code and keeps the captured text" {
            let checkedCapture = executeCapture (ProcessFixture.command [ "text"; "3" ]) |> Operation.map ignore

            match outcomeOf (silent id) checkedCapture with
            | StepOutcome.Failed (FailureCause.Command (command, exitCode, ValueSome captured)) ->
                Expect.equal exitCode 3 "the exit code travels with the failure"
                Expect.stringContains command "ProcessFixture.dll" "the command that failed is named"
                Expect.equal captured.Stderr "err-one\n\nerr-two" "the capture is the failure's evidence"
                Expect.stringContains (FailureCause.describe (FailureCause.Command (command, exitCode, ValueSome captured))) "err-one"
                    "the print site lifts the captured text into the line it renders"
            | other -> failtestf "a rejected exit code should fail a checked capture; got %A" other
        }

        test "a streamed command fails on an unacceptable exit code and carries no capture" {
            match outcomeOf (silent id) (execute (ProcessFixture.command [ "text"; "3" ])) with
            | StepOutcome.Failed (FailureCause.Command (_, exitCode, captured)) ->
                Expect.equal exitCode 3 "the exit code travels with the failure"
                Expect.equal captured ValueNone "an ordinary command acquires no hidden full-output buffer"
            | other -> failtestf "a rejected exit code should fail a streamed command; got %A" other
        }

        test "a stage that accepts the exit code lets both checked forms succeed" {
            let accepting = silent (StageContext.setAcceptableExitCodes [ 0; 3 ])

            Expect.equal (perform accepting (executeCapture (ProcessFixture.command [ "text"; "3" ]))).ExitCode 3
                "an accepted exit code answers the result rather than failing"
            Expect.equal (outcomeOf accepting (execute (ProcessFixture.command [ "text"; "3" ]))) StepOutcome.Completed
                "the acceptable set is the stage's, resolved where the operation runs"
        }

        test "a parsing failure inside an operation fails the step and keeps the exception" {
            let parsed =
                executeCapture (ProcessFixture.command [ "text"; "0" ])
                |> Operation.map (fun result -> Int32.Parse result.Stdout |> ignore)

            match outcomeOf (silent id) parsed with
            | StepOutcome.Failed (FailureCause.Raised error) ->
                Expect.isTrue (error :? FormatException) $"the exception itself survives; got {error.GetType().Name}"
            | other -> failtestf "a parsing failure should fail the step; got %A" other
        }

        test "a command that cannot be started names the executable and yields no result" {
            let startFailure outcome =
                match outcome with
                | StepOutcome.Failed (FailureCause.Start (executable, error)) ->
                    Expect.equal executable "partas-build-no-such-executable" "the failure names the executable"
                    Expect.isNotNull (box error) "the exception the platform raised is retained"
                | other -> failtestf "a failure to start is neither an exit code nor a result; got %A" other

            startFailure (outcomeOf (silent id) (execute missing))
            startFailure (outcomeOf (silent id) (executeCapture missing |> Operation.map ignore))
            startFailure (outcomeOf (silent id) (attemptCapture missing |> Operation.map ignore))
        }

        test "cancelling an attempted capture surfaces as cancellation, never as a result" {
            use cancellation = new CancellationTokenSource 300
            let attempted = attemptCapture (ProcessFixture.command [ "sleep"; "30000" ])

            expectCancellation cancellation.Token (attempted.Execute (runtime (silent id))) "a cancelled attempt"
        }

        test "cancellation cannot fire an attempted capture's fallback" {
            use cancellation = new CancellationTokenSource 300
            let recovered = ref 0

            let work =
                attemptCapture (ProcessFixture.command [ "sleep"; "30000" ])
                |> Operation.bind (fun _ -> Operation.ofAsync (async { recovered.Value <- recovered.Value + 1 }))

            expectCancellation cancellation.Token (work.Execute (runtime (silent id))) "a cancelled attempt with a fallback"
            Expect.equal recovered.Value 0 "a killed process is no exit code, so nothing downstream of one runs"
        }

        test "a cancelled operation is a cancellation rather than a step failure" {
            use cancellation = new CancellationTokenSource 300
            let work = execute (ProcessFixture.command [ "sleep"; "30000" ])

            expectCancellation cancellation.Token (Operation.toStepOutcome work (runtime (silent id))) "a cancelled step"
        }

        test "a stage's own timeout reports a timed-out operation and kills the process tree" {
            let before = sleepsAlive ()
            let watch = Diagnostics.Stopwatch.StartNew()
            let outcome = runStage (stage "slow" { timeout 2.0; runOperation (execute sleeps) })
            watch.Stop()

            match outcome with
            | Ok () -> failtest "a stage that ran out of its own timeout should fail"
            | Error failures ->
                let timedOut = FailureCause.describe FailureCause.TimedOut
                Expect.isTrue
                    (failures |> List.exists (fun failure -> failure.Message.Contains timedOut))
                    $"the stage's own timeout is a failure of that stage; got {failures |> List.map _.Message}"

            Expect.isLessThan watch.ElapsedMilliseconds 20000L "the timeout should kill the process rather than wait it out"

            // The kill is asynchronous, and it is the grandchild that used to survive it.
            Thread.Sleep 1500
            Expect.equal (sleepsAlive ()) before "the whole process tree should be gone"
        }

        test "a cancellation reaching a stage from above is no timeout of its own" {
            let before = sleepsAlive ()
            use cancellation = new CancellationTokenSource 500

            let report = StageContext.run (stage "slow" { runOperation (execute sleeps) }) (StageIndex.Stage 0) cancellation.Token
            if ScopeReport.continues report then failtest "a cancelled stage should not report success"

            Expect.isFalse
                (report.Failures |> List.exists (fun failure -> failure.Cause = FailureCause.TimedOut))
                "the token that fired belongs to the caller, so the stage reports cancellation rather than a timeout"

            Thread.Sleep 1500
            Expect.equal (sleepsAlive ()) before "a cancelled stage still kills the whole process tree"
        }

        test "a successful capture is never printed by the operation that took it" {
            let capture = OutputCapture.create()
            let stage = { StageContext.create "operations" with Output = ValueSome (StageOutput.Captured capture) }
            let result = perform stage (attemptCapture (ProcessFixture.command [ "text"; "0" ]))

            Expect.equal result.Stdout "alpha\n\nbeta\n" "the caller receives the text"
            Expect.isEmpty (OutputCapture.lines capture) "captured output is application data and can hold secrets; printing it is the caller's decision"
            Expect.isEmpty (OutputCapture.errors capture) "the same holds for what the command wrote to stderr"
        }

        test "an operation runs under the stage's working directory and environment" {
            let directory = IO.Path.GetTempPath().TrimEnd IO.Path.DirectorySeparatorChar

            let stage =
                silent (fun ctx ->
                    { ctx with WorkingDir = ValueSome directory } |> StageContext.addEnvVars [ "PARTAS_FIXTURE", "inherited" ])

            let result = perform stage (attemptCapture (ProcessFixture.command [ "env"; "PARTAS_FIXTURE" ]))
            let lines = result.Stdout.Split '\n'

            Expect.equal lines[1] "inherited" "the stage's environment reaches the child"
            Expect.stringContains lines[0] (IO.Path.GetFileName directory) "the stage's working directory reaches the child"
        }

        test "declaring and materializing an operation invokes neither the work nor the task factory" {
            let started = ref 0
            let deferred = Operation.ofAsync (async { started.Value <- started.Value + 1 })
            let fromFactory = Operation.ofTaskFactory (fun () -> started.Value <- started.Value + 1; Task.FromResult ())

            let plain = stage "deferred" { runOperation deferred }
            let viaFactory = stage "deferred" { runOperation fromFactory }

            Expect.equal started.Value 0 "building a stage around an operation starts nothing"
            Expect.equal (List.length plain.Steps) 1 "the operation is one step of the stage"

            Expect.equal (runStage plain) (Ok ()) "running the stage runs the operation"
            Expect.equal started.Value 1 "the work runs once, when the step does"

            Expect.equal (runStage viaFactory) (Ok ()) "a task factory is applied by the step"
            Expect.equal started.Value 2 "the factory runs once, when the step does"
        }

        test "an executing stage consumes clean command data and branches on an attempted exit" {
            let clean = ref ""
            let branched = ref 0

            let work =
                executeCapture (ProcessFixture.command [ "text"; "0" ])
                |> Operation.bind (fun captured ->
                    clean.Value <- captured.Stdout
                    attemptCapture (ProcessFixture.command [ "text"; "3" ]))
                |> Operation.map (fun attempted -> if attempted.ExitCode = 3 then branched.Value <- 1)

            Expect.equal (runStage (stage "release" { runOperation work "release data" })) (Ok ())
                "a stage branching on an attempted exit code succeeds"
            Expect.equal clean.Value "alpha\n\nbeta\n" "the checked capture handed over the child's raw text"
            Expect.equal branched.Value 1 "the attempted exit code reached the branch"
        }

        test "a failing operation fails its stage and names the command in the rendered line" {
            Expect.equal (runStage (stage "release" { runOperation (execute missing) })) (Error [])
                "an operation that failed fails the stage"
            Expect.stringContains
                (FailureCause.describe (FailureCause.Start ("partas-build-no-such-executable", exn "not found")))
                "partas-build-no-such-executable"
                "the rendered line names the executable"
        }

        test "explain renders an operation step by the label it was given" {
            let labelled = pipeline "release" { stage "publish" { runOperation (Operation.ret ()) "publish the packages" } }
            let unlabelled = pipeline "release" { stage "publish" { runOperation (Operation.ret ()) } }

            Expect.stringContains (Explain.render [ labelled ]) "$ publish the packages" "a labelled operation is described by its label"
            Expect.stringContains (Explain.render [ unlabelled ]) "step 1" "an unlabelled operation is described by its index"
        }
    ]
    // SageFs live testing classifies a test whose full name contains "integration" as Integration, run on demand.
    |> testLabel "integration"
