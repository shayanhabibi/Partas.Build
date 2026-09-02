namespace Partas.Build

open System
open System.IO
open Spectre.Console

/// <summary>What a finished run spent, stage by stage.</summary>
/// <remarks>
/// Every stage of the run in pre-order, each sub-stage under the stage containing it, with its wall time and
/// how it ended. One row per stage at any console width: text too wide for its column is elided.
/// </remarks>
module Summary =
    /// The heading printed above the table.
    [<Literal>]
    let title = "Stage timings"

    /// The wall time of <paramref name="timing"/>, or <c>-</c> for a stage that was skipped.
    let private elapsed (timing: StageTiming) =
        match timing.Outcome with
        | StageOutcome.Skipped -> "-"
        | _ ->
            let elapsed = timing.Elapsed
            if elapsed.TotalHours >= 1. then $"%i{int elapsed.TotalHours}h %02i{elapsed.Minutes}m %02i{elapsed.Seconds}s"
            elif elapsed.TotalMinutes >= 1. then $"%i{int elapsed.TotalMinutes}m %04.1f{float elapsed.Seconds + float elapsed.Milliseconds / 1000.}s"
            else $"%.1f{elapsed.TotalSeconds}s"

    let private outcome (timing: StageTiming) =
        match timing.Outcome with
        | StageOutcome.Succeeded -> "ok"
        | StageOutcome.Skipped -> "skipped"
        | StageOutcome.Failed error when String.IsNullOrWhiteSpace error -> "failed"
        | StageOutcome.Failed error -> $"failed: %s{error}"

    /// The name of the stage <paramref name="timing"/> covers, indented by its <c>Depth</c>.
    let private name (timing: StageTiming) = String.replicate timing.Depth "  " + timing.Name

    /// <summary><paramref name="text"/> shortened to <paramref name="width"/> characters, with <c>…</c>
    /// standing for what was dropped out of its middle.</summary>
    /// <remarks>Keeps both ends, so a row shows the depth indent it starts with and the file name it ends with.</remarks>
    let private elideMiddle (width: int) (text: string) =
        if text.Length <= width then text
        elif width <= 1 then text.Substring (text.Length - max width 0)
        else
            let tail = (width - 1) * 2 / 3
            text.Substring (0, width - 1 - tail) + "…" + text.Substring (text.Length - tail)

    /// <paramref name="text"/> shortened to <paramref name="width"/> characters, with <c>…</c> standing for the
    /// dropped end.
    let private elideEnd (width: int) (text: string) =
        if text.Length <= width then text
        elif width <= 1 then text.Substring (0, max width 0)
        else text.Substring (0, width - 1) + "…"

    /// <summary>The widths of the stage, time and outcome columns of a table drawn across
    /// <paramref name="width"/> characters.</summary>
    /// <remarks>
    /// The time column is always whole. Of the other two, one that fits within its half of the remainder keeps
    /// the width it needs and gives the rest to the other; otherwise the remainder is split evenly.
    /// </remarks>
    let private widths (width: int) (timings: StageTiming list) =
        let widest header cell = timings |> Seq.fold (fun widest timing -> max widest (String.length (cell timing))) (String.length header)
        let time = widest "Time" elapsed
        // Rounded borders and the padding of three cells cost ten characters of any row.
        let available = max (width - 10 - time) 6
        let stage = widest "Stage" name
        let outcome = widest "Outcome" outcome
        let half = available / 2

        if stage + outcome <= available then stage, time, outcome
        elif stage <= outcome && stage <= half then stage, time, available - stage
        elif outcome < stage && outcome <= half then available - outcome, time, outcome
        else half, time, available - half

    /// One row per stage, indented by <c>Depth</c> and sized to <paramref name="width"/> characters.
    let private table (width: int) (timings: StageTiming list) =
        let stageWidth, timeWidth, outcomeWidth = widths width timings
        let table = Table()
        table.Title <- TableTitle title
        table.Border <- TableBorder.Rounded
        table.AddColumn (TableColumn("Stage", Width = stageWidth, NoWrap = true)) |> ignore
        table.AddColumn (TableColumn("Time", Width = timeWidth, NoWrap = true).RightAligned()) |> ignore
        table.AddColumn (TableColumn("Outcome", Width = outcomeWidth, NoWrap = true)) |> ignore

        for timing in timings do
            table.AddRow (
                Markup.Escape (elideMiddle stageWidth (name timing)),
                Markup.Escape (elapsed timing),
                Markup.Escape (elideEnd outcomeWidth (outcome timing)))
            |> ignore

        table

    /// <summary>The table of <paramref name="timings"/>, as text.</summary>
    /// <remarks>Text only, without colour: rendering writes nothing anywhere. Columns are sized to the ambient
    /// console's width.</remarks>
    let render (timings: StageTiming list) =
        use writer = new StringWriter()

        let console =
            AnsiConsoleSettings (
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = AnsiConsoleOutput writer)
            |> AnsiConsole.Create

        let width = AnsiConsole.Profile.Width
        console.Profile.Width <- width
        console.Write (table width timings)
        writer.ToString().TrimEnd()
