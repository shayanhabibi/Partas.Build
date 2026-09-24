namespace Partas.Build

open System
open System.CommandLine
open System.CommandLine.Invocation
open System.Text.Json
open Partas.Build.Internal

/// <summary>How <c>--explain</c> answers a condition that performs IO or runs work to answer.</summary>
[<RequireQualifiedAccess; Struct>]
type ExplainMode =
    /// Every condition is called, <c>whenBranch</c> starting <c>git</c> and <c>whenStage</c> running its stage.
    | Evaluated
    /// A condition marked <c>Conditions.effectful</c> is reported unevaluated and left uncalled; every other
    /// condition is called.
    | Static

/// <summary>The answer <c>--explain</c> records for one condition of a stage.</summary>
[<RequireQualifiedAccess>]
type ExplainedConditionState =
    | Held
    | Failed
    /// Marked <c>effectful</c> and left uncalled by a static explanation.
    | Unevaluated
    /// The condition raised an exception.
    | Threw of message: string
    /// An earlier condition failed or threw, and the stage's <c>IsActive</c> stops at it.
    | NotReached

/// <summary>One condition of a stage, as <c>--explain</c> answered it.</summary>
type ExplainedCondition = {
    /// The text attributing a skip to the condition.
    Reason: string voption
    /// The description the condition is marked <c>effectful</c> with.
    Effect: string voption
    State: ExplainedConditionState
}

/// <summary>Whether a stage would run, as its conditions answered.</summary>
[<RequireQualifiedAccess>]
type ExplainedStatus =
    | Active
    /// A condition failed; <c>reason</c> is that condition's own.
    | Skipped of reason: string voption
    /// Every evaluated condition held, and the conditions listed were left uncalled.
    | Unevaluated of effects: string list
    /// A condition raised an exception with this message.
    | Errored of message: string

/// <summary>A stage as <c>--explain</c> describes it.</summary>
type ExplainedStage = {
    Name: string
    /// The producer the stage declares.
    Produces: string voption
    /// The producers the stage requires.
    Needs: string list
    Status: ExplainedStatus
    Conditions: ExplainedCondition list
    Steps: ExplainedStep list
}

/// <summary>A step as <c>--explain</c> describes it; <c>index</c> counts from zero, as a run result's <c>step</c> and
/// <c>path</c> do.</summary>
/// <remarks>A label is the step's log form, with every secret masked.</remarks>
and [<RequireQualifiedAccess>] ExplainedStep =
    | Step of index: int * label: string voption
    | Operation of index: int * label: string voption
    | Stage of index: int * stage: ExplainedStage

/// <summary>A pipeline as <c>--explain</c> describes it.</summary>
type ExplainedPipeline = {
    Name: string
    Description: string voption
    Stages: ExplainedStage list
    Post: ExplainedStage list
}

/// <summary>The resolved stage tree of a set of pipelines, as text or JSON.</summary>
/// <remarks>
/// Every option a command registers is readable without a <c>ParseResult</c>, so a pipeline can be materialised
/// from one and inspected before any step runs. That is what <c>--explain</c> prints.
/// <para>
/// A stage's conditions are called in declaration order, each at most once, and stop at the first that fails or
/// throws, as the stage's <c>IsActive</c> does; a condition that throws is reported with its message.
/// Under <c>ExplainMode.Evaluated</c> a condition may perform IO to answer: <c>whenBranch</c> starts <c>git</c>,
/// and <c>whenStage</c> runs its condition stage in full, side effects included. <c>ExplainMode.Static</c> leaves
/// those uncalled. Step functions and producer callbacks are never invoked: a producer appears by name, read
/// off the declaration.
/// </para>
/// </remarks>
module Explain =
    /// <summary>The Partas.Build version this script is pinned to.</summary>
    let libraryVersion =
        let assembly = Reflection.Assembly.GetExecutingAssembly()
        assembly.GetCustomAttributes(typeof<Reflection.AssemblyInformationalVersionAttribute>, false)
        |> Array.tryHead
        |> function
            | Some attr -> (attr :?> Reflection.AssemblyInformationalVersionAttribute).InformationalVersion
            | None -> assembly.GetName().Version |> string

    let [<Literal>] private branch = "├─ "
    let [<Literal>] private lastBranch = "└─ "
    let [<Literal>] private trunk = "│  "
    let [<Literal>] private gap = "   "

    let private exceptionMessage (error: exn) =
        match error with
        | :? Reflection.TargetInvocationException as error when not (isNull error.InnerException) -> error.InnerException.Message
        | error -> error.Message

    /// The answer of each condition of <paramref name="stage"/>, and the status they give the stage.
    let private answer (mode: ExplainMode) (stage: StageContext) =
        let mutable stopped = false

        let conditions =
            stage.Conditions
            |> List.map (fun condition ->
                let effect = Conditions.tryEffect condition.Predicate

                let state =
                    if stopped then ExplainedConditionState.NotReached
                    else
                        match mode, effect with
                        | ExplainMode.Static, ValueSome _ -> ExplainedConditionState.Unevaluated
                        | _ ->
                            try
                                if condition.Predicate stage then ExplainedConditionState.Held
                                else
                                    stopped <- true
                                    ExplainedConditionState.Failed
                            with error ->
                                stopped <- true
                                ExplainedConditionState.Threw(exceptionMessage error)

                { Reason = condition.Reason; Effect = effect; State = state })

        let status =
            conditions
            |> List.tryPick (fun condition ->
                match condition.State with
                | ExplainedConditionState.Failed -> Some(ExplainedStatus.Skipped condition.Reason)
                | ExplainedConditionState.Threw message -> Some(ExplainedStatus.Errored message)
                | _ -> None)
            |> Option.defaultWith (fun () ->
                match
                    conditions
                    |> List.filter (fun condition -> condition.State = ExplainedConditionState.Unevaluated)
                    |> List.choose (fun condition -> ValueOption.toOption condition.Effect)
                with
                | [] -> ExplainedStatus.Active
                | effects -> ExplainedStatus.Unevaluated effects)

        conditions, status

    let rec private explainStage (mode: ExplainMode) (stage: StageContext) : ExplainedStage =
        let conditions, status = answer mode stage

        {
            Name = stage.Name
            Produces = stage.Producer |> ValueOption.map _.Name
            Needs = stage.Requires |> List.map _.Name
            Status = status
            Conditions = conditions
            Steps =
                stage.Steps
                |> List.mapi (fun index step ->
                    match step with
                    | Step.StepOfStage subStage ->
                        ExplainedStep.Stage(index, explainStage mode { subStage with ParentContext = ValueSome(StageParent.Stage stage) })
                    | Step.StepFn(label, _) -> ExplainedStep.Step(index, label)
                    | Step.Operation(label, _) -> ExplainedStep.Operation(index, label))
        }

    /// <summary>The tree of <paramref name="pipelines"/>, with each stage's conditions answered under
    /// <paramref name="mode"/>.</summary>
    let explain (mode: ExplainMode) (pipelines: PipelineContext list) : ExplainedPipeline list =
        // Stages are re-parented onto the pipeline as they are run, and settings resolve by walking that link,
        // so a condition reads the same working directory and env vars here as it would under `PipelineContext.run`.
        let stages (pipeline: PipelineContext) (stages: StageContext list) =
            stages |> List.map (fun stage -> explainStage mode { stage with ParentContext = ValueSome(StageParent.Pipeline pipeline) })

        pipelines
        |> List.map (fun pipeline -> {
            Name = pipeline.Name
            Description = pipeline.Description
            Stages = stages pipeline pipeline.Stages
            Post = stages pipeline pipeline.PostStages
        })

    /// The parenthesised suffix naming the producer a stage declares and the producers it requires.
    let private dependencies (stage: ExplainedStage) =
        [
            match stage.Produces with
            | ValueSome producer -> $"produces %s{producer}"
            | ValueNone -> ()

            match stage.Needs with
            | [] -> ()
            | required -> required |> String.concat ", " |> sprintf "needs %s"
        ]
        |> function
            | [] -> ""
            | parts -> parts |> String.concat "; " |> sprintf "  (%s)"

    let private status (stage: ExplainedStage) =
        match stage.Status with
        | ExplainedStatus.Active -> ""
        | ExplainedStatus.Skipped(ValueSome reason) -> $"  (skipped: %s{reason})"
        | ExplainedStatus.Skipped ValueNone -> "  (skipped)"
        | ExplainedStatus.Unevaluated effects -> $"""  (unevaluated: %s{String.Join("; ", effects)})"""
        | ExplainedStatus.Errored message -> $"  (condition failed: %s{message})"

    /// <summary>The tree of <paramref name="pipelines"/>, one line per stage and per step.</summary>
    /// <remarks>
    /// Text only: rendering writes nothing anywhere. A stage's output sink governs that stage's execution
    /// output, and a description of what the stage would do is not that.
    /// </remarks>
    let renderWith (mode: ExplainMode) (pipelines: PipelineContext list) =
        let lines = ResizeArray<string>()

        let rec renderStage (prefix: string) (isLast: bool) (stage: ExplainedStage) =
            lines.Add $"""%s{prefix}%s{if isLast then lastBranch else branch}%s{stage.Name}%s{dependencies stage}%s{status stage}"""

            let childPrefix = prefix + (if isLast then gap else trunk)
            let lastIndex = stage.Steps.Length - 1

            stage.Steps
            |> List.iteri (fun position step ->
                let isLast = position = lastIndex
                match step with
                | ExplainedStep.Stage(_, subStage) -> renderStage childPrefix isLast subStage
                | ExplainedStep.Step(index, label)
                | ExplainedStep.Operation(index, label) ->
                    let text =
                        match label with
                        | ValueSome label -> $"$ %s{label}"
                        | ValueNone -> $"step %i{index + 1}"
                    lines.Add $"""%s{childPrefix}%s{if isLast then lastBranch else branch}%s{text}""")

        let renderStages (stages: ExplainedStage list) =
            let lastIndex = stages.Length - 1
            stages |> List.iteri (fun index stage -> renderStage "" (index = lastIndex) stage)

        explain mode pipelines
        |> List.iteri (fun index pipeline ->
            if index > 0 then lines.Add ""
            lines.Add pipeline.Name
            renderStages pipeline.Stages

            if not pipeline.Post.IsEmpty then
                lines.Add "post"
                renderStages pipeline.Post)

        String.Join(Environment.NewLine, lines)

    /// <summary>The tree of <paramref name="pipelines"/>, one line per stage and per step, with every condition
    /// evaluated.</summary>
    let render (pipelines: PipelineContext list) = renderWith ExplainMode.Evaluated pipelines

    let private modeName (mode: ExplainMode) =
        match mode with
        | ExplainMode.Evaluated -> "evaluated"
        | ExplainMode.Static -> "static"

    let private writeCondition (writer: Utf8JsonWriter) (condition: ExplainedCondition) =
        writer.WriteStartObject()

        let state, error =
            match condition.State with
            | ExplainedConditionState.Held -> "held", ValueNone
            | ExplainedConditionState.Failed -> "failed", ValueNone
            | ExplainedConditionState.Unevaluated -> "unevaluated", ValueNone
            | ExplainedConditionState.Threw message -> "threw", ValueSome message
            | ExplainedConditionState.NotReached -> "notReached", ValueNone

        writer.WriteString("state", state)
        MachineOutput.writeOptionalString writer "reason" condition.Reason
        MachineOutput.writeOptionalString writer "effect" condition.Effect
        MachineOutput.writeOptionalString writer "error" error
        writer.WriteEndObject()

    let rec private writeStage (writer: Utf8JsonWriter) (stage: ExplainedStage) =
        writer.WriteStartObject()
        writer.WriteString("name", stage.Name)

        match stage.Status with
        | ExplainedStatus.Active ->
            writer.WriteString("status", "active")
            writer.WriteNull "reason"
        | ExplainedStatus.Skipped reason ->
            writer.WriteString("status", "skipped")
            MachineOutput.writeOptionalString writer "reason" reason
        | ExplainedStatus.Unevaluated effects ->
            writer.WriteString("status", "unevaluated")
            writer.WriteString("reason", String.Join("; ", effects))
        | ExplainedStatus.Errored message ->
            writer.WriteString("status", "error")
            writer.WriteString("reason", message)

        MachineOutput.writeOptionalString writer "produces" stage.Produces
        MachineOutput.writeStrings writer "needs" stage.Needs

        writer.WriteStartArray "conditions"
        for condition in stage.Conditions do
            writeCondition writer condition
        writer.WriteEndArray()

        writer.WriteStartArray "steps"
        for step in stage.Steps do
            writer.WriteStartObject()

            match step with
            | ExplainedStep.Step(index, label) ->
                writer.WriteNumber("index", index)
                writer.WriteString("kind", "step")
                MachineOutput.writeOptionalString writer "label" label
            | ExplainedStep.Operation(index, label) ->
                writer.WriteNumber("index", index)
                writer.WriteString("kind", "operation")
                MachineOutput.writeOptionalString writer "label" label
            | ExplainedStep.Stage(index, subStage) ->
                writer.WriteNumber("index", index)
                writer.WriteString("kind", "stage")
                writer.WriteNull "label"
                writer.WritePropertyName "stage"
                writeStage writer subStage

            writer.WriteEndObject()
        writer.WriteEndArray()

        writer.WriteEndObject()

    let private writeStages (writer: Utf8JsonWriter) (name: string) (stages: ExplainedStage list) =
        writer.WriteStartArray name
        for stage in stages do
            writeStage writer stage
        writer.WriteEndArray()

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

    /// <summary>The tree of <paramref name="pipelines"/>, run by <paramref name="command"/>, as an indented JSON
    /// document.</summary>
    let toJson (mode: ExplainMode) (command: Command) (pipelines: PipelineContext list) =
        let explained = explain mode pipelines

        MachineOutput.document true (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", MachineOutput.FormatVersion)
            writer.WriteString("command", command.Name)
            writer.WriteString("mode", modeName mode)

            writer.WriteStartArray "pipelines"
            for pipeline in explained do
                writer.WriteStartObject()
                writer.WriteString("name", pipeline.Name)
                MachineOutput.writeOptionalString writer "description" pipeline.Description
                writeStages writer "stages" pipeline.Stages
                writeStages writer "post" pipeline.Post
                writer.WriteEndObject()
            writer.WriteEndArray()

            MachineOutput.writeStrings writer "undescribedCommands" (undescribed command)
            writer.WriteEndObject())

    /// <summary>The immediate subcommands of <paramref name="command"/>, as an indented JSON document.</summary>
    let subcommandsToJson (command: Command) =
        MachineOutput.document true (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", MachineOutput.FormatVersion)
            writer.WriteString("command", command.Name)
            writer.WriteStartArray "pipelines"
            writer.WriteEndArray()

            writer.WriteStartArray "subcommands"
            for subCommand in command.Subcommands do
                writer.WriteStartObject()
                writer.WriteString("name", subCommand.Name)
                MachineOutput.writeStrings writer "aliases" subCommand.Aliases
                MachineOutput.writeOptionalString writer "description" (
                    if String.IsNullOrWhiteSpace subCommand.Description then ValueNone else ValueSome subCommand.Description)
                writer.WriteEndObject()
            writer.WriteEndArray()

            MachineOutput.writeStrings writer "undescribedCommands" (undescribed command)
            writer.WriteEndObject())

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
                        let command = parseResult.CommandResult.Command
                        let text = if MachineOutput.isSet MachineOutput.json parseResult then subcommandsToJson command else ofSubcommands command
                        parseResult.InvocationConfiguration.Output.WriteLine text
                        0 })
