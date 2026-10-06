namespace Partas.Build.Internal

open System
open System.IO
open Spectre.Console

/// Runner protocol output is separate from stage output: captures and redirects must never swallow it.
module internal GitHubActions =
    let private tryWriteCommand (command: string) =
        try
            AnsiConsole.WriteLine command
            true
        with
        | :? IOException
        | :? ObjectDisposedException -> false

    let isEnabled (envVars: Map<string, string>) =
        envVars
        |> Map.tryFind "GITHUB_ACTIONS"
        |> Option.exists (fun value -> String.Equals(value, "true", StringComparison.OrdinalIgnoreCase))

    /// One group per top-level stage. Nested and parallel work stays inside that group's live log.
    let stageGroup (stage: StageContext) (index: StageIndex) isActive =
        let enabled =
            isActive
            && (match index, stage.ParentContext with
                | StageIndex.Stage _, ValueSome(StageParent.Pipeline _) -> true
                | _ -> false)
            && not (StageContext.getVerbosity stage).IsQuiet
            && isEnabled (StageContext.buildEnvVars stage)

        let opened =
            enabled
            && (StageContext.getNamePath stage
                |> StageContext.encodeWorkflowData
                |> fun title -> tryWriteCommand $"::group::{title}")

        { new IDisposable with
            member _.Dispose() = if opened then tryWriteCommand "::endgroup::" |> ignore }
