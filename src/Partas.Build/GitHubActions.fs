namespace Partas.Build.Internal

open System

/// Runner protocol output is separate from stage output: captures and redirects must never swallow it.
module internal GitHubActions =
    let isEnabled envVars = WorkflowCommands.isEnabled envVars

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
                |> fun title -> WorkflowCommands.tryWrite $"::group::{title}")

        { new IDisposable with
            member _.Dispose() = if opened then WorkflowCommands.tryWrite "::endgroup::" |> ignore }
