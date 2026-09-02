namespace Partas.Build

open System
open System.IO
open Spectre.Console

/// <summary>What a finished run spent, stage by stage.</summary>
/// <remarks>
/// Every stage of the run in the order it started, nested stages under the stage containing them, each with
/// its wall time and how it ended.
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

    /// One row per stage, indented by <c>Depth</c>.
    let private table (timings: StageTiming list) =
        let table = Table()
        table.Title <- TableTitle title
        table.Border <- TableBorder.Rounded
        table.AddColumn "Stage" |> ignore
        table.AddColumn (TableColumn("Time").RightAligned()) |> ignore
        table.AddColumn "Outcome" |> ignore

        for timing in timings do
            let name = String.replicate timing.Depth "  " + timing.Name
            table.AddRow (Markup.Escape name, Markup.Escape (elapsed timing), Markup.Escape (outcome timing)) |> ignore

        table

    /// <summary>The table of <paramref name="timings"/>, as text.</summary>
    /// <remarks>Text only, without colour: rendering writes nothing anywhere. Lines wrap at the ambient console's width.</remarks>
    let render (timings: StageTiming list) =
        use writer = new StringWriter()

        let console =
            AnsiConsoleSettings (
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = AnsiConsoleOutput writer)
            |> AnsiConsole.Create

        console.Profile.Width <- AnsiConsole.Profile.Width
        console.Write (table timings)
        writer.ToString().TrimEnd()
