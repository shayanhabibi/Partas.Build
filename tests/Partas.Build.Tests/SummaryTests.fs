module Partas.Build.Tests.SummaryTests

open System
open System.IO
open Expecto
open Spectre.Console
open Partas.Build
open Partas.Build.Internal

/// <summary>Runs <paramref name="fn"/> with the console redirected, and answers its result alongside what it
/// printed.</summary>
/// <remarks>
/// Spectre's ambient console is redirected alongside <c>Console.Out</c> and restored with it. It binds to the
/// writer it was created against, so a pipeline run under a redirect that touched only <c>Console.Out</c>
/// leaves every later test writing into this test's disposed writer.
/// </remarks>
let private capturingOut (fn: unit -> 'T) =
    let original = Console.Out
    let originalAnsi = AnsiConsole.Console
    use writer = new StringWriter()
    Console.SetOut writer

    AnsiConsole.Console <-
        AnsiConsoleSettings (
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = AnsiConsoleOutput writer)
        |> AnsiConsole.Create

    try
        let result = fn ()
        result, writer.ToString()
    finally
        Console.SetOut original
        AnsiConsole.Console <- originalAnsi

[<Tests>]
let tests =
    testList "summary" [
        test "the summary lists each stage with its wall time" {
            let timings =
                [ { Name = "build"; Depth = 1; Elapsed = TimeSpan.FromSeconds 9.1; Outcome = StageOutcome.Succeeded }
                  { Name = "run gate"; Depth = 1; Elapsed = TimeSpan.FromSeconds 57.6; Outcome = StageOutcome.Failed "exit 1" } ]

            let text = Summary.render timings

            Expect.stringContains text "build" "every stage appears"
            Expect.stringContains text "9.1" "with its wall time"
            Expect.stringContains text "run gate" "including the one that failed"
            Expect.stringContains text "exit 1" "and what failing meant"
        }

        test "a skipped stage is shown as skipped rather than as zero seconds" {
            let timings = [ { Name = "restore"; Depth = 1; Elapsed = TimeSpan.Zero; Outcome = StageOutcome.Skipped } ]
            let text = Summary.render timings
            Expect.stringContains text "skipped" "a skipped stage is not a fast one"
        }

        test "a run prints the summary, and a failing one prints it too" {
            let built =
                command "fail" {
                    pipeline "fail" {
                        stage "compile" { run (fun (_: StageContext) -> ()) }
                        stage "sign" { when' false; run (fun (_: StageContext) -> ()) }
                        stage "explode" { run (fun (_: StageContext) -> failwith "detonated") }
                    }
                }

            let exitCode, text = capturingOut (fun () -> built.Parse("").Invoke())

            Expect.equal exitCode 1 "a failed pipeline should exit one"
            let summary = text.Substring (text.IndexOf Summary.title)
            Expect.stringContains summary "compile" "the stage that ran before the failure"
            Expect.stringContains summary "sign" "the stage that was skipped"
            Expect.stringContains summary "explode" "and the stage that failed"
        }
    ]
