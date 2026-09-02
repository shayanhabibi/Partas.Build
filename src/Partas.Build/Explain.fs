namespace Partas.Build

open System
open System.CommandLine
open Partas.Build.Internal

/// <summary>The resolved stage tree of a set of pipelines, as text.</summary>
/// <remarks>
/// Every option a command registers is readable without a <c>ParseResult</c>, so a pipeline can be materialised
/// from one and inspected before any step runs. That is what <c>--explain</c> prints.
/// <para>
/// Rendering evaluates each stage's <c>IsActive</c>, and a condition may perform read-only IO to answer:
/// <c>whenBranch</c> starts <c>git</c>, and <c>whenStage</c> runs its condition stage in full, side effects
/// included. A skipped stage has its recorded conditions evaluated a second time to attribute the skip, so
/// those conditions run twice. Step functions are never invoked.
/// </para>
/// </remarks>
module Explain =
    /// <summary>Prints the resolved stage tree and exits, running nothing.</summary>
    /// <remarks>Registered on every command the library builds, so the name is reserved for it.</remarks>
    let option: ActionInput<bool> =
        Input.option<bool> "--explain"
        |> Input.desc "Print the resolved stage tree and exit, running nothing"
        |> Input.def false

    let [<Literal>] private branch = "├─ "
    let [<Literal>] private lastBranch = "└─ "
    let [<Literal>] private trunk = "│  "
    let [<Literal>] private gap = "   "

    /// The reason of the first recorded condition that is both false and carries one.
    let private skipReason (stage: StageContext) =
        stage.Conditions
        |> List.tryPick (fun condition ->
            match condition.Reason with
            | ValueSome reason when not (condition.Predicate stage) -> Some reason
            | _ -> None)

    let private status (stage: StageContext) =
        if stage.IsActive stage then ""
        else
            match skipReason stage with
            | Some reason -> $"  (skipped: %s{reason})"
            | None -> "  (skipped)"

    /// <summary>The tree of <paramref name="pipelines"/>, one line per stage and per step.</summary>
    /// <remarks>
    /// Each line is also written through <c>StageContext.writeLine</c> of the stage it describes, so a stage
    /// that routes its output somewhere other than the console routes its own explain lines there too.
    /// </remarks>
    let render (pipelines: PipelineContext list) =
        let lines = ResizeArray<string>()

        let emit (stage: StageContext voption) (line: string) =
            lines.Add line
            match stage with
            | ValueSome stage -> StageContext.writeLine stage StdStream.Out line
            | ValueNone -> Console.Out.WriteLine line

        let rec renderStage (prefix: string) (isLast: bool) (stage: StageContext) =
            emit (ValueSome stage) $"""%s{prefix}%s{if isLast then lastBranch else branch}%s{stage.Name}%s{status stage}"""

            let childPrefix = prefix + (if isLast then gap else trunk)
            let lastIndex = stage.Steps.Length - 1

            stage.Steps
            |> List.iteri (fun index step ->
                let isLast = index = lastIndex
                match step with
                | Step.StepOfStage subStage ->
                    renderStage childPrefix isLast { subStage with ParentContext = ValueSome(StageParent.Stage stage) }
                | Step.StepFn(label, _) ->
                    let text =
                        match label with
                        | ValueSome label -> $"$ %s{label}"
                        | ValueNone -> $"step %i{index + 1}"
                    emit (ValueSome stage) $"""%s{childPrefix}%s{if isLast then lastBranch else branch}%s{text}""")

        // Stages are re-parented onto the pipeline as they are run, and settings resolve by walking that link,
        // so a condition reads the same working directory and env vars here as it would under `PipelineContext.run`.
        let adopt (pipeline: PipelineContext) (stages: StageContext list) =
            stages |> List.map (fun stage -> { stage with ParentContext = ValueSome(StageParent.Pipeline pipeline) })

        let renderStages (stages: StageContext list) =
            let lastIndex = stages.Length - 1
            stages |> List.iteri (fun index stage -> renderStage "" (index = lastIndex) stage)

        pipelines
        |> List.iteri (fun index pipeline ->
            if index > 0 then emit ValueNone ""
            emit ValueNone pipeline.Name
            renderStages (adopt pipeline pipeline.Stages)

            if not pipeline.PostStages.IsEmpty then
                emit ValueNone "post"
                renderStages (adopt pipeline pipeline.PostStages))

        String.Join(Environment.NewLine, lines)

    /// <summary>The invocation path of <paramref name="command"/> and of each of its subcommands, where the
    /// command carries no description.</summary>
    let undescribed (command: Command) =
        let rec walk (parentPath: string) (command: Command) = [
            let path = if String.IsNullOrEmpty parentPath then command.Name else $"%s{parentPath} %s{command.Name}"
            if String.IsNullOrWhiteSpace command.Description then path
            for subCommand in command.Subcommands do
                yield! walk path subCommand
        ]

        walk "" command
