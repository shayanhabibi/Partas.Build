namespace Partas.Build

open System
open System.CommandLine
open System.CommandLine.Invocation
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
    /// Text only: rendering writes nothing anywhere. A stage's output sink governs that stage's execution
    /// output, and a description of what the stage would do is not that.
    /// </remarks>
    let render (pipelines: PipelineContext list) =
        let lines = ResizeArray<string>()

        let rec renderStage (prefix: string) (isLast: bool) (stage: StageContext) =
            lines.Add $"""%s{prefix}%s{if isLast then lastBranch else branch}%s{stage.Name}%s{status stage}"""

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
                    lines.Add $"""%s{childPrefix}%s{if isLast then lastBranch else branch}%s{text}""")

        // Stages are re-parented onto the pipeline as they are run, and settings resolve by walking that link,
        // so a condition reads the same working directory and env vars here as it would under `PipelineContext.run`.
        let adopt (pipeline: PipelineContext) (stages: StageContext list) =
            stages |> List.map (fun stage -> { stage with ParentContext = ValueSome(StageParent.Pipeline pipeline) })

        let renderStages (stages: StageContext list) =
            let lastIndex = stages.Length - 1
            stages |> List.iteri (fun index stage -> renderStage "" (index = lastIndex) stage)

        pipelines
        |> List.iteri (fun index pipeline ->
            if index > 0 then lines.Add ""
            lines.Add pipeline.Name
            renderStages (adopt pipeline pipeline.Stages)

            if not pipeline.PostStages.IsEmpty then
                lines.Add "post"
                renderStages (adopt pipeline pipeline.PostStages))

        String.Join(Environment.NewLine, lines)

    /// <summary>The immediate subcommands of <paramref name="command"/> with their descriptions, as text.</summary>
    /// <remarks>What a command that runs no pipeline of its own would do: dispatch to one of these.</remarks>
    let subcommands (command: Command) =
        let lines = ResizeArray<string>()
        lines.Add command.Name

        let lastIndex = command.Subcommands.Count - 1
        let width = command.Subcommands |> Seq.fold (fun width subCommand -> max width subCommand.Name.Length) 0

        command.Subcommands
        |> Seq.iteri (fun index subCommand ->
            let name =
                if String.IsNullOrWhiteSpace subCommand.Description then subCommand.Name
                else $"""%s{subCommand.Name.PadRight width}  %s{subCommand.Description}"""

            lines.Add $"""%s{if index = lastIndex then lastBranch else branch}%s{name}""")

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

    /// <paramref name="body"/> followed by the invocation paths under <paramref name="command"/> that still
    /// carry no description.
    let private report (command: Command) (body: string) =
        let missing = undescribed command

        [
            body
            if not missing.IsEmpty then
                ""
                "Commands with no description:"

                for path in missing do
                    $"  %s{path}"
        ]
        |> String.concat Environment.NewLine

    /// What <c>--explain</c> prints for a command that runs <paramref name="pipelines"/>.
    let ofPipelines (command: Command) (pipelines: PipelineContext list) = render pipelines |> report command

    /// What <c>--explain</c> prints for a command that only dispatches to its subcommands.
    let ofSubcommands (command: Command) = subcommands command |> report command

    let private declare () =
        Input.option<bool> "--explain"
        |> Input.desc "Print the resolved stage tree and exit, running nothing"
        |> Input.def false

    /// <summary>The flag as registered on a command that runs pipelines, whose own action reads it.</summary>
    let option: ActionInput<bool> = declare ()

    /// <summary>The flag as registered on a command that only dispatches to its subcommands.</summary>
    /// <remarks>
    /// Such a command has no action of its own — System.CommandLine reports the missing subcommand instead — so
    /// the rendering hangs off the option, which leaves that report in place for an invocation without the flag.
    /// </remarks>
    let groupingOption: ActionInput<bool> =
        declare ()
        |> Input.editOption (fun option ->
            option.Action <-
                { new SynchronousCommandLineAction() with
                    member _.Invoke(parseResult: ParseResult) =
                        Console.Out.WriteLine (ofSubcommands parseResult.CommandResult.Command)
                        0 })
