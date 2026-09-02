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

/// One printed row of the table. <c>Indent</c> is the depth indent the name carries, in characters.
type private Row = { Name: string; Indent: int; Time: string; Outcome: string }

/// <summary>The stage rows of a rendered table, in the order they were printed.</summary>
/// <remarks>
/// A row is the only line the vertical rule splits into five; the border lines are drawn with junction
/// characters. A cell that wrapped therefore arrives as a row of its own, carrying an empty time.
/// </remarks>
let private rowsOf (text: string) = [
    for line in text.Split '\n' do
        let cells = line.Split '│'

        if cells.Length = 5 && cells[1].Trim() <> "Stage" then
            let name = cells[1]
            // Spectre pads every cell with one space of its own, which is not part of the depth indent.
            { Name = name.Trim()
              Indent = name.TrimEnd().Length - name.Trim().Length - 1
              Time = cells[2].Trim()
              Outcome = cells[3].Trim() }
]

/// The rendered table of <paramref name="timings"/> at a console of <paramref name="width"/> columns.
let private renderAt (width: int) (timings: StageTiming list) =
    let original = AnsiConsole.Console
    use writer = new StringWriter()

    let console =
        AnsiConsoleSettings (
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = AnsiConsoleOutput writer)
        |> AnsiConsole.Create

    console.Profile.Width <- width
    AnsiConsole.Console <- console

    try Summary.render timings
    finally AnsiConsole.Console <- original

/// The index of the row of the stage named <paramref name="name"/>.
let private indexOf (rows: Row list) (name: string) =
    rows
    |> List.tryFindIndex (fun row -> row.Name = name)
    |> function
        | Some index -> index
        | None -> failtestf "no row for stage %s in %A" name [ for row in rows -> row.Name ]

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
            let rows = rowsOf (text.Substring (text.IndexOf Summary.title))

            Expect.equal [ for row in rows -> row.Name ] [ "compile"; "sign"; "explode" ] "the rows read in declaration order"

            let row name = rows[indexOf rows name]
            Expect.equal (row "compile").Outcome "ok" "the stage that ran before the failure"
            Expect.equal (row "sign").Outcome "skipped" "the stage a condition turned off says so"
            Expect.equal (row "sign").Time "-" "and reports no time rather than a fast one"
            Expect.stringContains (row "explode").Outcome "failed" "the stage that raised"
            Expect.stringContains (row "explode").Outcome "detonated" "carries the message it raised with"
        }

        test "a stage's rows sit under its own parent when siblings run in parallel" {
            let step (_: StageContext) = Threading.Thread.Sleep 20

            let built =
                command "fan" {
                    pipeline "fan" {
                        stage "fan" {
                            parallel' true

                            stage "alpha" {
                                stage "alpha-1" { run step }
                                stage "alpha-2" { run step }
                            }

                            stage "beta" {
                                stage "beta-1" { run step }
                                stage "beta-2" { run step }
                            }
                        }
                    }
                }

            let exitCode, text = capturingOut (fun () -> built.Parse("").Invoke())

            Expect.equal exitCode 0 "the pipeline should succeed"
            let rows = rowsOf (text.Substring (text.IndexOf Summary.title))
            let index = indexOf rows

            Expect.equal (index "fan") 0 "the parent of both branches leads the table"
            Expect.equal (index "alpha-1") (index "alpha" + 1) "alpha's first child follows alpha"
            Expect.equal (index "alpha-2") (index "alpha" + 2) "alpha's second child follows its first"
            Expect.equal (index "beta-1") (index "beta" + 1) "beta's first child follows beta"
            Expect.equal (index "beta-2") (index "beta" + 2) "beta's second child follows its first"
            Expect.equal rows[index "beta-1"].Indent 4 "a grandchild is indented twice"
        }

        test "a name too long for the console keeps its row and its indent" {
            let long = @"build C:\Users\someone\source\Some.Very.Long.Solution\tests\Some.Very.Long.Project.Tests.fsproj"

            let timings =
                [ { Name = "build"; Depth = 0; Elapsed = TimeSpan.FromSeconds 8.; Outcome = StageOutcome.Succeeded }
                  { Name = long; Depth = 1; Elapsed = TimeSpan.FromSeconds 3.; Outcome = StageOutcome.Succeeded } ]

            let rows = rowsOf (renderAt 80 timings)

            Expect.equal rows.Length 2 "one row per stage, whatever the width"
            Expect.equal rows[1].Indent 2 "the nested stage keeps the indent that shows whose child it is"
            Expect.equal rows[1].Time "3.0s" "and its time stays on the row"
            Expect.stringContains rows[1].Name "Some.Very.Long.Project.Tests.fsproj" "the end of the name, which is what distinguishes it"
        }
    ]
