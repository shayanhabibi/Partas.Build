/// <summary>
/// Where a stage's step output goes: the console, nowhere, a capture, or a function.
/// </summary>
/// <remarks>
/// The runner is driven directly where the assertion is about the value a step returns, because the
/// error a captured failure lifts never reaches the console — lifting it into the <c>Result</c> is
/// the whole point, and printing is what the stage does with it afterwards.
/// </remarks>
module Partas.Build.Tests.OutputTests

open System.IO
open System.Threading
open Spectre.Console
open Expecto
open Partas.Build
open Partas.Build.Internal

/// Writes a line to stdout and exits zero, wherever the tests can run at all.
let private quiet = Cmd.ofString "dotnet --version"

/// <summary>Writes to both streams and exits one.</summary>
/// <remarks>
/// Both, deliberately: `dotnet` puts "Could not execute ..." on stderr and its explanation on stdout,
/// which is what tells a lifted stderr apart from a lifted everything.
/// </remarks>
let private loud = Cmd.ofString "dotnet --definitely-not-a-flag"

let private runStep (ctx: StageContext) (command: Cmd) =
    CmdRunner.run ctx 0<stepIndex> CancellationToken.None command |> Async.RunSynchronously

let private stageWith output = { StageContext.create "output" with Output = ValueSome output }

let private runs (built: PipelineContext) =
    try
        PipelineContext.run built
        true
    with :? PipelineFailedException ->
        false

[<Tests>]
let tests =
    testList "output" [
        test "a capture holds what the command wrote, tagged with the stream it came from" {
            let capture = OutputCapture()
            let result = runStep (stageWith (StageOutput.Captured capture)) loud

            Expect.isError result "the command exits one"
            Expect.isTrue (capture.Errors |> List.exists _.Contains("Could not execute")) $"stderr should be tagged as such: {capture.Errors}"
            Expect.isTrue (capture.Lines |> List.exists _.Contains("Possible reasons")) $"stdout should be captured too: {capture.Lines}"
            Expect.isFalse (capture.ErrorText.Contains "Possible reasons") "stdout should not have leaked into the error text"
        }

        test "a failing step lifts what it captured into its error" {
            let capture = OutputCapture()

            match runStep (stageWith (StageOutput.Captured capture)) loud with
            | Ok () -> failtest "the command exits one"
            | Error message ->
                Expect.stringContains message "Could not execute" "the reason has to travel in the error, since nothing was printed"
                Expect.isFalse (message.Contains "Possible reasons") "stderr was used, so only stderr is lifted"
        }

        test "everything is lifted when the command failed without using stderr" {
            let capture = OutputCapture()
            capture.Add (StdStream.Out, "assertion failed somewhere in the noise")

            Expect.equal capture.FailureText "assertion failed somewhere in the noise" "a runner reporting failures on stdout must still lift something"
        }

        test "a successful step lifts nothing" {
            let capture = OutputCapture()
            let result = runStep (stageWith (StageOutput.Captured capture)) quiet

            Expect.equal result (Ok()) "the command exits zero"
            Expect.isNonEmpty capture.Lines "the version it printed is still captured"
        }

        test "silent output is dropped, and a failure says only that it failed" {
            match runStep (stageWith StageOutput.Silent) loud with
            | Ok () -> failtest "the command exits one"
            | Error message -> Expect.isFalse (message.Contains "Could not execute") "nothing was held, so there is nothing to lift"
        }

        test "a step buffer forces redirection even under the console sink" {
            let buffer = OutputCapture()
            let ctx = { StageContext.create "output" with StepBuffer = ValueSome buffer }

            let result = runStep ctx quiet

            Expect.equal result (Ok()) "the command exits zero"
            Expect.isNonEmpty buffer.Lines "a step buffer in play must redirect the child, not let it write the console handle directly"
        }

        test "a failing step under a step buffer lifts its own lines, not a sibling's already-flushed ones" {
            let capture = OutputCapture()
            capture.Add (StdStream.Out, "sibling-1")
            capture.Add (StdStream.Out, "sibling-2")
            let buffer = OutputCapture()
            let ctx = { stageWith (StageOutput.Captured capture) with StepBuffer = ValueSome buffer }

            match runStep ctx loud with
            | Ok () -> failtest "the command exits one"
            | Error message ->
                Expect.stringContains message "Could not execute" "its own stderr should still be lifted"
                Expect.isFalse (message.Contains "sibling") "a sibling's already-flushed lines must not be lifted as this step's own"
        }

        test "redirect hands over each line as it arrives" {
            let lines = ResizeArray()
            let ctx = stageWith (StageOutput.Redirect (fun stream line -> lock lines (fun () -> lines.Add (stream, line))))

            runStep ctx loud |> ignore

            let received = List.ofSeq lines
            Expect.isTrue (received |> List.exists (fun (stream, line) -> stream = StdStream.Err && line.Contains "Could not execute")) $"stderr: {received}"
            Expect.isTrue (received |> List.exists (fun (stream, line) -> stream = StdStream.Out && line.Contains "Possible reasons")) $"stdout: {received}"
        }

        test "a sub-stage writes into the capture its parent declared" {
            let capture = OutputCapture()

            let built =
                pipeline "capture" {
                    stage "parent" {
                        captureOutput capture

                        stage "child" {
                            echo "from the child"
                        }
                    }
                }

            Expect.isTrue (runs built) "the pipeline should succeed"
            Expect.equal capture.Lines [ "from the child" ] "the child inherits the sink rather than printing"
        }

        test "a sub-stage overrides what it inherited" {
            let outer = OutputCapture()
            let inner = OutputCapture()

            let built =
                pipeline "capture" {
                    stage "parent" {
                        captureOutput outer
                        echo "from the parent"

                        stage "child" {
                            captureOutput inner
                            echo "from the child"
                        }
                    }
                }

            Expect.isTrue (runs built) "the pipeline should succeed"
            Expect.equal outer.Lines [ "from the parent" ] "the parent keeps its own"
            Expect.equal inner.Lines [ "from the child" ] "the child takes the nearer declaration"
        }

        test "a pipeline sets the default its stages inherit" {
            let capture = OutputCapture()

            let built =
                pipeline "capture" {
                    captureOutput capture
                    stage "one" { echo "one" }
                    stage "two" { echo "two" }
                }

            Expect.isTrue (runs built) "the pipeline should succeed"
            Expect.equal capture.Lines [ "one"; "two" ] "both stages write into it"
        }

        test "running the same pipeline twice does not accumulate" {
            let capture = OutputCapture()
            let built = pipeline "capture" { stage "one" { captureOutput capture; echo "one" } }

            Expect.isTrue (runs built) "the first run"
            Expect.isTrue (runs built) "the second run"
            Expect.equal capture.Lines [ "one" ] "the stage clears the capture it declared before it starts"
        }

        test "a lifted message survives being made into a GitHub Actions annotation" {
            // Escaped rather than written across two lines: a newline in the source is whatever the checkout
            // made it, and git hands a Linux runner LF where it hands Windows CRLF.
            let encoded = StageContext.encodeWorkflowData "100% failed\r\nassertion at line 3"

            // A workflow command ends at the first newline, so an unencoded lift would annotate "100% failed"
            // and drop the only line that says why.
            Expect.equal encoded "100%25 failed%0D%0Aassertion at line 3" "every newline and percent has to be encoded"
        }

        // Guards the printing side, which no other test here touches: a step's error was once matched with an
        // inverted guard, so every failure with something to say was the one thing that went unreported.
        testSequenced <| test "a failing step says why on the console, lifted capture and all" {
            let recorded = new StringWriter()
            let previous = AnsiConsole.Console
            let console = AnsiConsole.Create (AnsiConsoleSettings (Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = AnsiConsoleOutput recorded))
            // Otherwise the console wraps at 80 columns and the message being looked for is broken across lines.
            console.Profile.Width <- 1000
            AnsiConsole.Console <- console

            try
                let built = pipeline "failing" { stage "one" { captureOutput (OutputCapture()); run loud } }
                Expect.isFalse (runs built) "the pipeline should fail"
            finally
                AnsiConsole.Console <- previous

            Expect.stringContains (recorded.ToString()) "Could not execute" "what the step captured has to reach the console when it fails"
        }

        test "noStdRedirectForStep wins, because there is nothing to route without redirection" {
            let capture = OutputCapture()
            let ctx = { stageWith (StageOutput.Captured capture) with NoStdRedirectForStep = true }

            runStep ctx loud |> ignore

            Expect.isTrue capture.IsEmpty "the child wrote straight to the console"
        }
    ]
