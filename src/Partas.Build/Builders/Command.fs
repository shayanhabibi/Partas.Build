[<AutoOpen>]
module Partas.Build.CommandBuilder

open System
open System.CommandLine
open Partas.Build
open Partas.Build.Internal

/// Appends a pipeline to the command being built.
let inline private addPipeline (spec: InputSpec<PipelineContext>): BuildCommand =
    fun cmd -> { cmd with Pipelines = cmd.Pipelines @ [ spec ] }

/// Registers one input on a command. Context and injected inputs have nothing to register:
/// they are supplied at invocation time rather than parsed from the command line.
let private register (command: Command) (input: ActionInput) =
    match input.Source with
    | ParsedOption option -> command.Options.Add option
    | ParsedArgument argument -> command.Arguments.Add argument
    | Context | Injection _ -> ()

/// Reads each pipeline out of the parse result and runs it, in declaration order.
/// The runner has already reported the failure by the time it raises, so this only maps it to an exit code.
let private invoke (spec: CommandSpec) (parseResult: ParseResult) =
    try
        for pipeline in spec.Pipelines do
            pipeline.Read parseResult |> PipelineContext.run

        0
    with
    | :? PipelineFailedException -> 1
    | :? PipelineCancelledException -> 130

/// Applies a finished spec to a command, registering the options its pipelines declared.
let private applyTo (command: Command) (spec: CommandSpec) =
    spec.Description |> ValueOption.iter (fun description -> command.Description <- description)

    for alias in spec.Aliases do
        command.Aliases.Add alias

    command.Hidden <- spec.Hidden

    for input in CommandSpec.inputs spec do
        register command input

    for subCommand in spec.SubCommands do
        command.Subcommands.Add subCommand

    // A command with no pipelines is a grouping node for its subcommands. Leaving it without an action
    // lets System.CommandLine report the missing subcommand and print help, rather than succeeding silently.
    if not spec.Pipelines.IsEmpty then
        command.SetAction (Func<ParseResult, int>(invoke spec))

    command

/// <summary>
/// The members shared by <c>command</c> and <c>rootCommand</c>.
/// </summary>
/// <remarks>
/// A command yields pipelines, not options: the options it registers are harvested from the
/// <c>InputSpec</c> of each pipeline it runs, so declaring a stage that reads <c>--configuration</c> is
/// what puts <c>--configuration</c> in <c>--help</c>. <c>addInput</c> is for the remainder — flags no
/// pipeline asks for, which a root command may still want to expose.
/// </remarks>
type CommandBuilderBase() =
    member inline _.Zero(): BuildCommand = id
    member inline _.Yield(_: unit): BuildCommand = id
    member inline _.Yield(pipeline: PipelineContext): BuildCommand = addPipeline (InputSpec.ret pipeline)
    member inline _.Yield(spec: InputSpec<PipelineContext>): BuildCommand = addPipeline spec
    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildCommand): BuildCommand = fn()
    member inline _.Combine([<InlineIfLambda>] build: BuildCommand, [<InlineIfLambda>] rest: BuildCommand): BuildCommand = build >> rest
    member inline _.For([<InlineIfLambda>] build: BuildCommand, [<InlineIfLambda>] fn: unit -> BuildCommand): BuildCommand = build >> fn()

    /// <summary>Sets the command's description text shown in help.</summary>
    [<CustomOperation>] member inline _.
        description
        ([<InlineIfLambda>] build: BuildCommand, desc: string): BuildCommand
        = build >> fun cmd -> { cmd with Description = ValueSome desc }

    /// <summary>Adds a single alias for the command.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/aliasRemark/*"/>
    [<CustomOperation>] member inline _.
        alias
        ([<InlineIfLambda>] build: BuildCommand, alias: string): BuildCommand
        = build >> fun cmd -> { cmd with Aliases = cmd.Aliases @ [ alias ] }

    /// <summary>Adds multiple aliases for the command.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/aliasRemark/*"/>
    [<CustomOperation>] member inline _.
        aliases
        ([<InlineIfLambda>] build: BuildCommand, aliases: string seq): BuildCommand
        = build >> fun cmd -> { cmd with Aliases = cmd.Aliases @ List.ofSeq aliases }

    /// <summary>Hides the command from help output.</summary>
    /// <remarks>Defaults to <c>true</c> when no argument is provided. Pass <c>false</c> to show a previously hidden command.</remarks>
    [<CustomOperation>] member inline _.
        hidden
        ([<InlineIfLambda>] build: BuildCommand, ?flag: bool): BuildCommand
        = build >> fun cmd -> { cmd with Hidden = defaultArg flag true }

    /// <summary>Registers an extra input on the command.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/addInputRemark/*"/>
    [<CustomOperation>] member inline _.
        addInput
        ([<InlineIfLambda>] build: BuildCommand, input: ActionInput<'T>): BuildCommand
        = build >> fun cmd -> { cmd with ExtraInputs = cmd.ExtraInputs @ [ input :> ActionInput ] }

    /// <summary>Registers multiple extra inputs on the command.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/addInputRemark/*"/>
    [<CustomOperation>] member inline _.
        addInputs
        ([<InlineIfLambda>] build: BuildCommand, inputs: ActionInput seq): BuildCommand
        = build >> fun cmd -> { cmd with ExtraInputs = cmd.ExtraInputs @ List.ofSeq inputs }

    /// <summary>Adds a subcommand.</summary>
    /// <remarks>Subcommands create nested command hierarchies.</remarks>
    [<CustomOperation>] member inline _.
        addCommand
        ([<InlineIfLambda>] build: BuildCommand, subCommand: Command): BuildCommand
        = build >> fun cmd -> { cmd with SubCommands = cmd.SubCommands @ [ subCommand ] }

    /// <summary>Adds multiple subcommands.</summary>
    /// <remarks>Subcommands create nested command hierarchies.</remarks>
    [<CustomOperation>] member inline _.
        addCommands
        ([<InlineIfLambda>] build: BuildCommand, subCommands: Command seq): BuildCommand
        = build >> fun cmd -> { cmd with SubCommands = cmd.SubCommands @ List.ofSeq subCommands }

/// <summary>Builds a named subcommand from the pipelines it runs.</summary>
type CommandBuilder(name: string) =
    inherit CommandBuilderBase()

    // `Run` deliberately not `inline`: it applies the `BuildCommand` function, and inlining an
    // application of a plain function type defeats the optimiser (`FS1118`) in Release builds only.
    member _.Run(build: BuildCommand): Command =
        let spec = CommandSpec.create name |> build
        applyTo (Command spec.Name) spec

/// <summary>Builds the root command and runs it against <c>args</c>, returning the process exit code.</summary>
type RootCommandBuilder(args: string array) =
    inherit CommandBuilderBase()

    /// <summary>Configures System.CommandLine's parser behavior.</summary>
    /// <remarks>Available only on <c>rootCommand</c>.</remarks>
    [<CustomOperation>] member inline _.
        parserConfiguration
        ([<InlineIfLambda>] build: BuildCommand, config: ParserConfiguration): BuildCommand
        = build >> fun cmd -> { cmd with ParserConfiguration = ValueSome config }

    /// <summary>Configures System.CommandLine's invocation behavior.</summary>
    /// <remarks>Available only on <c>rootCommand</c>.</remarks>
    [<CustomOperation>] member inline _.
        invocationConfiguration
        ([<InlineIfLambda>] build: BuildCommand, config: InvocationConfiguration): BuildCommand
        = build >> fun cmd -> { cmd with InvocationConfiguration = ValueSome config }

    member _.Run(build: BuildCommand): int =
        let spec = CommandSpec.create "" |> build
        let root = applyTo (RootCommand()) spec

        let parseResult =
            match spec.ParserConfiguration with
            | ValueSome config -> root.Parse(args, config)
            | ValueNone -> root.Parse args

        match spec.InvocationConfiguration with
        | ValueSome config -> parseResult.Invoke config
        | ValueNone -> parseResult.Invoke()

let inline command name = CommandBuilder name
let inline rootCommand args = RootCommandBuilder args
