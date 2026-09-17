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
