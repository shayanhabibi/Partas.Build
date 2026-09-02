[<AutoOpen>]
module Partas.Build.CommandBuilder

open System
open System.IO
open System.CommandLine
open System.CommandLine.Help
open System.CommandLine.Invocation
open Partas.Build
open Partas.Build.Internal
/// Names an unnamed pipeline after the command that runs it, and fills its unset settings from the command's
/// defaults. Applied once the whole command is built, not as each pipeline is yielded, so that a setting written
/// below a pipeline reaches it just as one written above it does.
let private injectCommandInfo (cmd: CommandSpec) (spec: InputSpec<PipelineContext>): InputSpec<PipelineContext> =
    { spec with
        Read =
            spec.Read
            >> (function { Name = null | "" } as pipeline -> { pipeline with Name = cmd.Name; Description = cmd.Description } | pipeline -> pipeline)
            >> PipelineContext.applyDefaults cmd.PipelineDefaults }

/// Appends a pipeline to the command being built.
let inline private addPipeline (spec: InputSpec<PipelineContext>): BuildCommand =
    fun cmd -> { cmd with Pipelines = cmd.Pipelines @ [ spec ] }

/// <summary>The stages a command yields directly, before they are folded into its implicit pipeline.</summary>
/// <remarks>
/// One element per yielded expression rather than one per stage, because a single expression can carry several
/// stages (<c>yield!</c> over a list, a <c>for</c> loop). Kept as a list so that consecutive stages land in
/// <em>one</em> pipeline instead of one pipeline each: a command's stages should share a run, its settings and
/// its <c>whenStage</c> cross-references.
/// </remarks>
type CommandStages = InputSpec<StageContext list> list

/// Folds yielded stages into the single implicit pipeline they describe.
let private pipelineOfCommandStages (stages: CommandStages): InputSpec<PipelineContext> =
    InputSpec.map (List.concat >> pipelineOfStages) (InputSpec.sequence stages)

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
    let spec = { spec with Pipelines = spec.Pipelines |> List.map (injectCommandInfo spec) }

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

/// Rewrites a root command's <c>--help</c> output to call it <paramref name="displayName"/> instead of
/// <paramref name="command"/>'s own <c>Name</c> (the host executable's, since <c>Command.Name</c> has no
/// setter in System.CommandLine 2.0.11). Runs the built-in <see cref="T:System.CommandLine.Help.HelpAction"/>
/// against a buffer, substitutes the command's own name for <paramref name="displayName"/> in the rendered
/// text, and only then writes it to the real output — the default action resolves its writer from
/// <c>ParseResult.InvocationConfiguration.Output</c> at invocation time, not from a fixed console handle, so
/// this has to intercept there rather than redirect <c>Console.Out</c>.
let private nameHelpOutput (command: Command) (displayName: string) =
    match command.Options |> Seq.tryFind (fun option -> option.Name = "--help") with
    | Some helpOption ->
        match helpOption.Action with
        | :? HelpAction as originalAction ->
            let originalName = command.Name
            helpOption.Action <-
                { new SynchronousCommandLineAction() with
                    member _.Invoke(parseResult: ParseResult) =
                        let configuration = parseResult.InvocationConfiguration
                        let realOutput = configuration.Output
                        use buffer = new StringWriter()
                        configuration.Output <- buffer
                        let exitCode =
                            try originalAction.Invoke parseResult
                            finally configuration.Output <- realOutput
                        realOutput.Write(buffer.ToString().Replace(originalName, displayName))
                        exitCode }
        | _ -> ()
    | None -> ()

/// <summary>The arguments a script was given, as distinct from the ones its host was given.</summary>
/// <remarks>
/// Not <c>[&lt;AutoOpen&gt;]</c>: <c>take</c>, <c>nameOf</c>, <c>afterScript</c> and <c>script</c> are common
/// enough one-word names that leaving this module qualified (<c>Args.take</c>, and so on) is worth it.
/// </remarks>
module Args =
    /// <summary>Everything after the first <c>--</c>.</summary>
    /// <remarks>
    /// A separator-based slice: correct wherever the whole command line survives to
    /// <c>Environment.GetCommandLineArgs()</c> with a literal <c>--</c> still in it. <c>dotnet fsi</c>'s own
    /// driver does not preserve one — see <c>afterScript</c>, which is what <c>Args.script</c> actually uses.
    /// </remarks>
    let take (argv: string array) =
        match argv |> Array.tryFindIndex (fun arg -> arg = "--") with
        | Some index -> argv[index + 1 ..]
        | None -> [||]

    /// <summary>The filename of the first <c>.fsx</c> among <paramref name="argv"/>.</summary>
    let nameOf (argv: string array) =
        argv
        |> Array.tryFind (fun arg -> arg.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase))
        |> function
            | Some path -> ValueSome (IO.Path.GetFileName path)
            | None -> ValueNone

    /// <summary>
    /// The arguments a script was given: everything after its own <c>.fsx</c> file among <paramref name="argv"/>,
    /// or — when none is present, the shape a compiled host produces (<c>dotnet run --project X -- test</c>) —
    /// everything after <c>argv[0]</c>. A literal <c>--</c> immediately at the front of the result is dropped.
    /// </summary>
    /// <remarks>
    /// <c>dotnet fsi build.fsx -- test --quick</c> does not reach the process as the whole command line: the
    /// <c>dotnet</c> driver consumes exactly one <c>--</c> before <c>fsi</c> ever sees
    /// <c>Environment.GetCommandLineArgs()</c>, so a separator-based slice (<c>take</c>) finds nothing and
    /// silently returns empty. Locating the script's own filename instead does not depend on a separator
    /// surviving that driver at all.
    /// </remarks>
    let afterScript (argv: string array) =
        let rest =
            match argv |> Array.tryFindIndex (fun arg -> arg.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase)) with
            | Some index -> argv[index + 1 ..]
            | None -> if argv.Length = 0 then [||] else argv[1 ..]

        if rest.Length > 0 && rest[0] = "--" then rest[1 ..] else rest

    /// <summary>The running script's own arguments.</summary>
    let script () = afterScript (Environment.GetCommandLineArgs())

    /// <summary>The running script's filename, when it was launched as one.</summary>
    let scriptName () = nameOf (Environment.GetCommandLineArgs())

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
    /// <summary>Adds one more setting to the defaults the command hands every pipeline it runs.</summary>
    /// <remarks>
    /// The plumbing behind the pipeline-level custom operations below. <paramref name="fn"/> is applied to a
    /// pristine <c>PipelineContext</c>, never to a finished pipeline: <c>PipelineContext.applyDefaults</c> then
    /// copies across only those settings the pipeline itself left alone.
    /// </remarks>
    member inline _.MapPipelineDefault([<InlineIfLambda>] build: BuildCommand, [<InlineIfLambda>] fn: BuildPipeline): BuildCommand =
        build >> fun cmd -> { cmd with PipelineDefaults = cmd.PipelineDefaults >> fn }
    member inline _.Zero(): BuildCommand = id
    member inline _.Yield(_: unit): BuildCommand = id
    member inline _.Yield(pipeline: PipelineContext): BuildCommand = addPipeline (InputSpec.ret pipeline)
    member inline _.Yield(spec: InputSpec<PipelineContext>): BuildCommand = addPipeline spec
    member inline _.Yield(pipelines: PipelineContext seq): BuildCommand =
        fun cmd -> pipelines |> Seq.fold (fun cmd pipeline -> addPipeline (InputSpec.ret pipeline) cmd) cmd
    member inline _.Yield(specs: InputSpec<PipelineContext> seq): BuildCommand =
        fun cmd -> specs |> Seq.fold (fun cmd spec -> addPipeline spec cmd) cmd
    /// A subcommand yielded into its parent, rather than passed to <c>addCommand</c>.
    member inline _.Yield(subCommand: Command): BuildCommand = fun cmd -> { cmd with SubCommands = cmd.SubCommands @ [ subCommand ] }
    member inline _.Yield(subCommands: Command seq): BuildCommand = fun cmd -> { cmd with SubCommands = cmd.SubCommands @ List.ofSeq subCommands }
    /// An input yielded into a command, rather than passed to <c>addInput</c>. Registered even though no pipeline asks for it.
    member inline _.Yield(input: ActionInput): BuildCommand = fun cmd -> { cmd with ExtraInputs = cmd.ExtraInputs @ [ input ] }
    member inline _.Yield(inputs: ActionInput seq): BuildCommand = fun cmd -> { cmd with ExtraInputs = cmd.ExtraInputs @ List.ofSeq inputs }
    // A stage yielded straight into a command is shorthand for a command running one unnamed pipeline of stages.
    member inline _.Yield(stage: StageContext): CommandStages = [ InputSpec.ret [ stage ] ]
    member inline _.Yield(stages: StageContext seq): CommandStages = [ InputSpec.ret (List.ofSeq stages) ]
    member inline _.Yield(spec: InputSpec<StageContext>): CommandStages = [ InputSpec.map List.singleton spec ]
    member inline _.Yield(spec: InputSpec<StageContext seq>): CommandStages = [ InputSpec.map List.ofSeq spec ]
    member inline _.Yield(spec: InputSpec<StageContext list>): CommandStages = [ spec ]
    /// A list of ready-made blocks - `[ Blocks.restore; Blocks.build ]` - rather than one block yielding many stages.
    member inline _.Yield(specs: InputSpec<StageContext> seq): CommandStages = [ InputSpec.sequence specs ]

    member inline _.YieldFrom(pipelines: PipelineContext seq): BuildCommand =
        fun cmd -> pipelines |> Seq.fold (fun cmd pipeline -> addPipeline (InputSpec.ret pipeline) cmd) cmd
    member inline _.YieldFrom(specs: InputSpec<PipelineContext> seq): BuildCommand =
        fun cmd -> specs |> Seq.fold (fun cmd spec -> addPipeline spec cmd) cmd
    member inline _.YieldFrom(subCommands: Command seq): BuildCommand = fun cmd -> { cmd with SubCommands = cmd.SubCommands @ List.ofSeq subCommands }
    member inline _.YieldFrom(inputs: ActionInput seq): BuildCommand = fun cmd -> { cmd with ExtraInputs = cmd.ExtraInputs @ List.ofSeq inputs }
    member inline _.YieldFrom(stages: StageContext seq): CommandStages = [ InputSpec.ret (List.ofSeq stages) ]
    member inline _.YieldFrom(specs: InputSpec<StageContext> seq): CommandStages = [ InputSpec.sequence specs ]

    member inline _.Delay([<InlineIfLambda>] fn: unit -> BuildCommand): BuildCommand = fn()
    member inline _.Delay(fn: unit -> CommandStages): CommandStages = fn()

    member inline _.Combine([<InlineIfLambda>] build: BuildCommand, [<InlineIfLambda>] rest: BuildCommand): BuildCommand = build >> rest
    member inline _.Combine(stages: CommandStages, rest: CommandStages): CommandStages = stages @ rest
    member _.Combine(stages: CommandStages, rest: BuildCommand): BuildCommand =
        addPipeline (pipelineOfCommandStages stages) >> rest
    member _.Combine(build: BuildCommand, stages: CommandStages): BuildCommand =
        build >> addPipeline (pipelineOfCommandStages stages)

    member inline _.For([<InlineIfLambda>] build: BuildCommand, [<InlineIfLambda>] fn: unit -> BuildCommand): BuildCommand = build >> fn()
    member _.For(build: BuildCommand, fn: unit -> CommandStages): BuildCommand =
        build >> addPipeline (pipelineOfCommandStages (fn()))
    member inline _.For(stages: CommandStages, fn: unit -> CommandStages): CommandStages = stages @ fn()
    member _.For(stages: CommandStages, fn: unit -> BuildCommand): BuildCommand =
        addPipeline (pipelineOfCommandStages stages) >> fn()
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> BuildCommand): BuildCommand =
        fun cmd -> collection |> Seq.fold (fun cmd item -> fn item cmd) cmd
    member _.For(collection: 'Collection when 'Collection :> 'T seq, fn: 'T -> CommandStages): CommandStages =
        collection |> Seq.collect fn |> List.ofSeq
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> PipelineContext): BuildCommand =
        fun cmd -> collection |> Seq.fold (fun cmd item -> addPipeline (InputSpec.ret (fn item)) cmd) cmd
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> InputSpec<PipelineContext>): BuildCommand =
        fun cmd -> collection |> Seq.fold (fun cmd item -> addPipeline (fn item) cmd) cmd
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> Command): BuildCommand =
        fun cmd -> { cmd with SubCommands = cmd.SubCommands @ (collection |> Seq.map fn |> List.ofSeq) }
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> StageContext): CommandStages =
        [ InputSpec.ret (collection |> Seq.map fn |> List.ofSeq) ]
    member inline _.For(collection: 'Collection when 'Collection :> 'T seq, [<InlineIfLambda>] fn: 'T -> InputSpec<StageContext>): CommandStages =
        [ InputSpec.traverse fn collection ]

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
    /// <summary>Sets the total timeout every pipeline the command runs falls back to.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        timeout
        ([<InlineIfLambda>] build: BuildCommand, timeout: TimeSpan): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Timeout = ValueSome timeout })

    /// <summary>Sets the total timeout, in seconds, every pipeline the command runs falls back to.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        timeout
        ([<InlineIfLambda>] build: BuildCommand, seconds: int): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Timeout = ValueSome(TimeSpan.FromSeconds(float seconds)) })

    /// <summary>Sets the per-stage timeout every pipeline the command runs falls back to.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStage
        ([<InlineIfLambda>] build: BuildCommand, timeout: TimeSpan): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with TimeoutForStage = ValueSome timeout })

    /// <summary>Sets the per-stage timeout, in seconds, every pipeline the command runs falls back to.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStage
        ([<InlineIfLambda>] build: BuildCommand, seconds: int): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with TimeoutForStage = ValueSome(TimeSpan.FromSeconds(float seconds)) })

    /// <summary>Sets the per-step timeout every pipeline the command runs falls back to.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildCommand, timeout: TimeSpan): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with TimeoutForStep = ValueSome timeout })

    /// <summary>Sets the per-step timeout, in seconds, every pipeline the command runs falls back to.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        timeoutForStep
        ([<InlineIfLambda>] build: BuildCommand, seconds: int): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with TimeoutForStep = ValueSome(TimeSpan.FromSeconds(float seconds)) })

    /// <summary>Adds environment variables to every pipeline the command runs.</summary>
    /// <remarks>Per variable: a pipeline that sets one of these keys itself keeps its own value, the rest still apply.</remarks>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        envVars
        ([<InlineIfLambda>] build: BuildCommand, kvs: (string * string) seq): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with EnvVars = kvs |> Seq.fold (fun state (k, v) -> Map.add k v state) ctx.EnvVars })

    /// <summary>Sets which process exit codes count as success, for every pipeline the command runs.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        acceptExitCodes
        ([<InlineIfLambda>] build: BuildCommand, codes: int seq): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with AcceptableExitCodes = set codes })

    /// <summary>Sets the directory commands run in, for every pipeline the command runs.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        workingDir
        ([<InlineIfLambda>] build: BuildCommand, dir: string): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with WorkingDir = ValueSome dir })

    /// <summary>Sets the directory commands run in, for every pipeline the command runs.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        workingDir
        ([<InlineIfLambda>] build: BuildCommand, dir: IO.DirectoryInfo): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with WorkingDir = ValueSome dir.FullName })

    /// <summary>Sends the output of every pipeline the command runs somewhere other than the console.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        outputTo
        ([<InlineIfLambda>] build: BuildCommand, output: StageOutput): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Output = ValueSome output })

    /// <summary>Drops the output of every pipeline the command runs.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        silentOutput
        ([<InlineIfLambda>] build: BuildCommand): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Output = ValueSome StageOutput.Silent })

    /// <summary>Holds step output back, lifting it into the error message when a step fails.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        captureOutput
        ([<InlineIfLambda>] build: BuildCommand, ?capture: OutputCapture): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Output = ValueSome(StageOutput.Captured(defaultArg capture (OutputCapture()))) })

    /// <summary>Hands each line of step output to <paramref name="writer"/> as it arrives.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        redirectOutput
        ([<InlineIfLambda>] build: BuildCommand, [<InlineIfLambda>] writer: StdStream -> string -> unit): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Output = ValueSome(StageOutput.Redirect writer) })

    /// <summary>Stops each step prefixing its console output with the stage and step index.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        noPrefixForStep
        ([<InlineIfLambda>] build: BuildCommand, ?flag: bool): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with NoPrefixForStep = defaultArg flag true })

    /// <summary>Stops redirecting child process stdout/stderr, letting them write to the console directly.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        noStdRedirectForStep
        ([<InlineIfLambda>] build: BuildCommand, ?flag: bool): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with NoStdRedirectForStep = defaultArg flag true })

    /// <summary>Runs a function immediately before each stage of every pipeline the command runs.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        runBeforeEachStage
        ([<InlineIfLambda>] build: BuildCommand, [<InlineIfLambda>] fn: StageContext -> unit): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with RunBeforeEachStage = fn })

    /// <summary>Runs a function immediately after each stage of every pipeline the command runs.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        runAfterEachStage
        ([<InlineIfLambda>] build: BuildCommand, [<InlineIfLambda>] fn: StageContext -> unit): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with RunAfterEachStage = fn })

    /// <summary>Sets the stages that run after the main stages, whether or not the pipeline succeeded.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        post
        ([<InlineIfLambda>] build: BuildCommand, stages: StageContext list): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with PostStages = stages })

    /// <summary>Sets how much every pipeline the command runs prints.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        verbosity
        ([<InlineIfLambda>] build: BuildCommand, verbosity: Verbosity): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Verbosity = ValueSome verbosity })

    /// <summary>Prints the full trace of every pipeline the command runs.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        verbose
        ([<InlineIfLambda>] build: BuildCommand): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Verbose })

    /// <summary>Prints as little as possible from every pipeline the command runs.</summary>
    /// <include file="../xmldoc/command.xml" path="/command/pipelineDefault/*"/>
    [<CustomOperation>] member inline this.
        quiet
        ([<InlineIfLambda>] build: BuildCommand): BuildCommand
        = this.MapPipelineDefault(build, fun ctx -> { ctx with Verbosity = ValueSome Verbosity.Quiet })


/// <summary>Builds a named subcommand from the pipelines it runs.</summary>
type CommandBuilder(name: string) =
    inherit CommandBuilderBase()

    // `Run` deliberately not `inline`: it applies the `BuildCommand` function, and inlining an
    // application of a plain function type defeats the optimiser (`FS1118`) in Release builds only.
    member _.Run(build: BuildCommand): Command =
        let spec = CommandSpec.create name |> build
        applyTo (Command spec.Name) spec

    member this.Run(stages: CommandStages): Command = this.Run(addPipeline (pipelineOfCommandStages stages))

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

    /// <summary>Sets the name the root command calls itself in help and usage text.</summary>
    /// <remarks>
    /// Available only on <c>rootCommand</c>. Without it the name is the host executable's, so a script run
    /// as <c>dotnet fsi build.fsx</c> ships help telling a new contributor to run <c>fsi build</c>.
    /// Defaults to the script's filename when the process was launched with one.
    /// </remarks>
    [<CustomOperation>] member inline _.
        name
        ([<InlineIfLambda>] build: BuildCommand, name: string): BuildCommand
        = build >> fun cmd -> { cmd with DisplayName = ValueSome name }

    member this.Run(stages: CommandStages): int = this.Run(addPipeline (pipelineOfCommandStages stages))

    member _.Run(build: BuildCommand): int =
        let spec = CommandSpec.create "" |> build
        let root = applyTo (RootCommand()) spec

        spec.DisplayName
        |> ValueOption.orElse (Args.scriptName ())
        |> ValueOption.iter (nameHelpOutput root)

        let parseResult =
            match spec.ParserConfiguration with
            | ValueSome config -> root.Parse(args, config)
            | ValueNone -> root.Parse args

        match spec.InvocationConfiguration with
        | ValueSome config -> parseResult.Invoke config
        | ValueNone -> parseResult.Invoke()

module Command =
    /// <summary>
    /// Only use within a <c>command</c> or <c>rootCommand</c>.
    /// </summary>
    let pipeline = PipelineBuilder(null)

let inline command name = CommandBuilder name
let inline rootCommand args = RootCommandBuilder args

/// <summary>The root command over the running script's own arguments.</summary>
/// <remarks><c>rootCommandOfScript { … }</c> is <c>rootCommand (Args.script ()) { … }</c>.</remarks>
let rootCommandOfScript = RootCommandBuilder(Args.script ())
