module Partas.Build.Tests.SummaryTests

open System
open System.IO
open Expecto
open Spectre.Console
open Partas.Build
open Partas.Build.Internal
open Partas.Build.Tests.Helpers

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
        test "GitHub summary appends outcomes on failure even when the console is quiet" {
            let path = Path.GetTempFileName()
            try
                File.WriteAllText(path, "Earlier step content\n")
                let built =
                    command "ci" {
                        pipeline "build" {
                            quiet
                            envVars [ "GITHUB_ACTIONS", "true"; "GITHUB_STEP_SUMMARY", path ]
                            stage "compile|<target>" { run (fun (_: StageContext) -> ()) }
                            stage "sign" { when' false; echo "unused" }
                            stage "explode" { run (fun (_: StageContext) -> failwith "first\nsecond|<bad>") }
                        }
                    }
                let exitCode = quietly (fun () -> built.Parse("").Invoke())
                let text = File.ReadAllText path
                Expect.equal exitCode 1 "reporting preserves the build failure"
                Expect.stringStarts text "Earlier step content\n" "summary appends rather than replacing other content"
                Expect.stringContains text "build" "the pipeline name appears"
                Expect.stringContains text "compile\\|&lt;target&gt;" "names cannot break the Markdown table"
                Expect.stringContains text "skipped" "the skipped stage is recorded"
                Expect.stringContains text "first<br>second\\|&lt;bad&gt;" "failure details remain in one safe table cell"
                Expect.stringContains text "failed" "the failure is visible outside the logs"
                Expect.stringContains text "ok" "the successful stage is recorded"
                Expect.stringContains text "s |" "stages include their elapsed time"
            finally File.Delete path
        }

        test "GitHub summary includes a single stage and appends subsequent pipeline runs" {
            let path = Path.GetTempFileName()
            try
                let built =
                    command "ci" {
                        pipeline "one" {
                            envVars [ "GITHUB_ACTIONS", "true"; "GITHUB_STEP_SUMMARY", path ]
                            stage "first" { echo "hello" }
                        }
                        pipeline "two" {
                            envVars [ "GITHUB_ACTIONS", "true"; "GITHUB_STEP_SUMMARY", path ]
                            stage "second" { echo "world" }
                        }
                    }
                Expect.equal (quietly (fun () -> built.Parse("").Invoke())) 0 "both pipelines run"
                let text = File.ReadAllText path
                Expect.stringContains text "first" "single-stage runs are included"
                Expect.stringContains text "second" "later pipelines append their own stages"
            finally File.Delete path
        }

        test "summary files are untouched outside GitHub Actions" {
            let path = Path.GetTempFileName()
            try
                let built =
                    command "local" {
                        pipeline "local" {
                            envVars [ "GITHUB_ACTIONS", "false"; "GITHUB_STEP_SUMMARY", path ]
                            stage "first" { echo "hello" }
                        }
                    }
                quietly (fun () -> built.Parse("").Invoke()) |> ignore
                Expect.equal (File.ReadAllText path) "" "a path alone does not enable CI reporting"
            finally File.Delete path
        }

        test "summary reporting errors preserve success and failure exit codes" {
            for shouldFail in [ false; true ] do
                let built = command "ci" {
                    pipeline "ci" {
                        envVars [ "GITHUB_ACTIONS", "true"; "GITHUB_STEP_SUMMARY", Path.GetTempPath() ]
                        stage "work" { run (fun (_: StageContext) -> if shouldFail then failwith "original failure") }
                    }
                }
                let result = quietly (fun () -> built.Parse("").Invoke())
                Expect.equal result (if shouldFail then 1 else 0) "an unwritable summary must not determine the build result"
        }

        test "summaries do not exceed GitHub's per-step file size limit" {
            let path = Path.GetTempFileName()
            try
                let earlier = String.replicate (1024 * 1024 - 10) "x"
                File.WriteAllText(path, earlier)
                let built = command "ci" {
                    pipeline "ci" {
                        envVars [ "GITHUB_ACTIONS", "true"; "GITHUB_STEP_SUMMARY", path ]
                        stage "work" { echo "hello" }
                    }
                }
                Expect.equal (quietly (fun () -> built.Parse("").Invoke())) 0 "summary limits do not fail the build"
                Expect.isTrue (File.ReadAllText path = earlier) "earlier summaries remain uploadable when no space remains"
            finally File.Delete path
        }

        test "local runs do not emit GitHub group commands" {
            let built = pipeline "local" {
                envVars [ "GITHUB_ACTIONS", "false" ]
                stage "work" { echo "hello" }
            }
            let _, text = capturingOut (fun () -> PipelineContext.run built)
            Expect.isFalse (text.Contains "::group::") "local logs keep their existing presentation"
        }

        test "group framing I/O failures do not prevent work or change its exit code" {
            for shouldFail, blocked in [ false, "::group::"; true, "::group::"; false, "::endgroup::"; true, "::endgroup::" ] do
                let mutable ran = false
                let built = command "ci" {
                    pipeline "ci" {
                        envVars [ "GITHUB_ACTIONS", "true" ]
                        stage "work" {
                            run (fun (_: StageContext) ->
                                ran <- true
                                if shouldFail then failwith "original failure")
                        }
                    }
                }
                let result, _ = capturingOut (fun () ->
                    use writer =
                        { new StringWriter() with
                            override _.Write(text: string) =
                                if not (isNull text) && text.Contains blocked then
                                    raise (IOException "group output unavailable")
                            override _.WriteLine(text: string) =
                                if not (isNull text) && text.Contains blocked then
                                    raise (IOException "group output unavailable") }
                    Console.SetOut writer
                    AnsiConsole.Console <-
                        AnsiConsoleSettings (Ansi = AnsiSupport.No, Out = AnsiConsoleOutput writer)
                        |> AnsiConsole.Create
                    built.Parse("").Invoke())
                Expect.isTrue ran "a framing failure cannot prevent the stage from running"
                Expect.equal result (if shouldFail then 1 else 0) "framing does not replace the stage result"
        }

        test "GitHub groups close when a stage is cancelled" {
            use cancellation = new Threading.CancellationTokenSource()
            let built = stage "cancelled" {
                run (async { cancellation.Cancel(); do! Async.Sleep 100 })
            }
            let parent = pipeline "ci" { envVars [ "GITHUB_ACTIONS", "true" ] }
            let built = { built with ParentContext = ValueSome(StageParent.Pipeline parent) }
            let _, text = capturingOut (fun () -> StageContext.run built (StageIndex.Stage 0) cancellation.Token)
            Expect.isTrue cancellation.IsCancellationRequested "the stage cancelled its ancestor token"
            Expect.stringContains text "::group::" "the stage opened a group before cancellation"
            Expect.stringContains text "::endgroup::" "cancellation closes the group"
        }

        test "GitHub framing bypasses stage captures while summaries keep nested stages" {
            let path = Path.GetTempFileName()
            let capture = OutputCapture.create()
            try
                let built = command "ci" {
                    pipeline "ci" {
                        envVars [ "GITHUB_ACTIONS", "true"; "GITHUB_STEP_SUMMARY", path ]
                        stage "parent" {
                            captureOutput capture
                            stage "child" { echo "captured child text" }
                        }
                    }
                }
                let result, text = capturingOut (fun () -> built.Parse("").Invoke())
                Expect.equal result 0 "capturing output does not change execution"
                Expect.stringContains text "::group::parent" "group framing reaches the runner console"
                Expect.isFalse (text.Contains "captured child text") "step output still respects the capture"
                let captured = OutputCapture.text capture
                Expect.stringContains captured "captured child text" "the capture retains step output"
                Expect.isFalse (captured.Contains "::group::") "captures contain no runner protocol"
                let summary = File.ReadAllText path
                Expect.isTrue (summary.IndexOf "parent" < summary.IndexOf "child") "the parent precedes its nested stage"
                Expect.stringContains summary "  child" "the child keeps its tree depth"
            finally File.Delete path
        }

        test "GitHub groups contain nested parallel output without overlapping groups" {
            use barrier = new Threading.Barrier(2)
            let worker name = stage name {
                run (fun ctx ->
                    StageContext.writeLine ctx StdStream.Out name
                    if not (barrier.SignalAndWait(TimeSpan.FromSeconds 5.)) then failwith "workers did not overlap")
            }
            let built = pipeline "ci" {
                envVars [ "GITHUB_ACTIONS", "true" ]
                stage "workers\n::error::injected" {
                    parallel' 2
                    worker "alpha"
                    worker "beta"
                }
                stage "next" { echo "done" }
            }
            let _, text = capturingOut (fun () -> PipelineContext.run built)
            let lines = text.Replace("\r", "").Split '\n'
            let commands = lines |> Array.filter (fun line -> line.StartsWith "::group::" || line = "::endgroup::")
            Expect.equal commands.Length 4 "only the two top-level stages create groups"
            Expect.isTrue (commands[0].Contains "%0A") "titles cannot inject workflow commands"
            Expect.equal commands[1] "::endgroup::" "the first group closes before the next opens"
            Expect.isTrue (commands[2].StartsWith "::group::") "the next stage gets its own group"
            Expect.equal commands[3] "::endgroup::" "the last group closes"
            let close = text.IndexOf "::endgroup::"
            for name in [ "alpha"; "beta" ] do
                Expect.isTrue (text.IndexOf(name) > text.IndexOf("::group::") && text.IndexOf(name) < close) "parallel output belongs to the enclosing group"
        }

        test "GitHub groups close on failures and do not group inactive or quiet stages" {
            let built = command "ci" {
                pipeline "ci" {
                    envVars [ "GITHUB_ACTIONS", "true" ]
                    stage "skipped" { when' false; echo "unused" }
                    stage "failure" { run (fun (_: StageContext) -> failwith "boom") }
                }
            }
            let exitCode, text = capturingOut (fun () -> built.Parse("").Invoke())
            Expect.equal exitCode 1 "grouping preserves the failure exit code"
            let commands = text.Split '\n' |> Array.filter (fun line -> line.StartsWith "::group::" || line.TrimEnd() = "::endgroup::")
            Expect.equal commands.Length 2 "only the active stage is grouped"
            Expect.equal (commands[1].TrimEnd()) "::endgroup::" "failure closes the group"
            let quietPipeline = pipeline "quiet" {
                quiet
                envVars [ "GITHUB_ACTIONS", "true" ]
                stage "work" { echo "hello" }
            }
            let _, quietText = capturingOut (fun () -> PipelineContext.run quietPipeline)
            Expect.isFalse (quietText.Contains "::group::") "quiet runs emit no empty group framing"
        }

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
