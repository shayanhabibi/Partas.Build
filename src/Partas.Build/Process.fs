namespace Partas.Build

open System
open System.Text

/// <summary>An executable and its arguments, kept apart so that neither is ever re-split or re-escaped.</summary>
/// <remarks>
/// Fun.Build splits a command string on the first space and hands the remainder to
/// <c>ProcessStartInfo.Arguments</c> as one opaque blob, which makes quoting the caller's problem and
/// platform-dependent. Here the arguments reach <c>ProcessStartInfo.ArgumentList</c>, which escapes them for
/// the running platform. <c>Secrets</c> holds indices into <c>Arguments</c> whose value must never be printed —
/// for a <c>runSensitive</c> command those are exactly the interpolation holes.
/// </remarks>
type Cmd = {
    Executable: string
    Arguments: string list
    Secrets: Set<int>
}

module Cmd =
    /// <summary>
    /// Marks a string as containing sensitive information, so it is never printed when
    /// processed in a <see cref="T:Partas.Build.Cmd"/>.
    /// </summary>
    /// <remarks>If the secret contains spaces, then it is also quoted as a literal.</remarks>
    /// <param name="input"></param>
    let secret (input: string) =
#if NETSTANDARD2_0
        if input.Contains(" ")
#else
        if input.Contains(' ')
#endif
        then input.Insert(0, "\u001f\"") + "\""
        else input.Insert(0, "\u001F")
    /// <summary>
    /// Marks a string as containing sensitive information, so it is never printed when
    /// processed in a <see cref="T:Partas.Build.Cmd"/>.
    /// </summary>
    /// <remarks>If the secret contains spaces, then it is also quoted as a literal.</remarks>
    /// <param name="input"></param>
    let inline sensitive (input: string) = secret input
    [<Literal>]
    let private mask = "***"

    /// <summary>Accumulates tokens across literal fragments and interpolated values.</summary>
    /// <remarks>
    /// A token ends at unquoted whitespace and nowhere else, so a value glued to literal text
    /// (<c>--config={cfg}</c>) stays one argument, and a value never contributes whitespace of its own.
    /// </remarks>
    type private Accumulator() =
        let tokens = ResizeArray<string>()
        let secrets = ResizeArray<int>()
        let current = StringBuilder()
        let mutable started = false
        let mutable isSecret = false
        let mutable quote = '\000'
        let mutable secretDelimiter = '\u001F'

        member private _.Flush() =
            if started then
                if current.Length > 0 && current[0] = secretDelimiter then
                    tokens.Add(current.Remove(0, 1).ToString())
                    secrets.Add (tokens.Count - 1)
                else
                tokens.Add (current.ToString())
                if isSecret then secrets.Add (tokens.Count - 1)
            current.Clear() |> ignore
            started <- false
            isSecret <- false

        /// Literal text: whitespace separates tokens, `"` and `'` suppress that.
        member this.AddLiteral(text: string) =
            for ch in text do
                if quote <> '\000' then
                    if ch = quote then quote <- '\000' else current.Append ch |> ignore
                elif ch = '"' || ch = '\'' then
                    quote <- ch
                    started <- true
                elif Char.IsWhiteSpace ch then
                    this.Flush()
                else
                    current.Append ch |> ignore
                    started <- true

        /// An interpolated value: appended whole, whatever it contains. An empty value on its own yields no token.
        member _.AddValue(value: string, secret: bool) =
            if not (String.IsNullOrEmpty value) then
                current.Append value |> ignore
                started <- true
                if secret then isSecret <- true

        /// The executable is the first token; an empty command line yields an empty executable.
        member this.ToCmd() =
            this.Flush()
            if tokens.Count = 0 then
                { Executable = ""; Arguments = []; Secrets = Set.empty }
            else {
                Executable = tokens[0]
                Arguments = List.ofSeq (Seq.skip 1 tokens)
                // Shifted to index the arguments; a secret executable makes no sense and is dropped.
                Secrets = secrets |> Seq.filter (fun i -> i > 0) |> Seq.map (fun i -> i - 1) |> Set.ofSeq
            }

    /// Splits a whole command line — executable included — honouring `"` and `'` quoting.
    let ofString (commandLine: string) =
        let acc = Accumulator()
        acc.AddLiteral commandLine
        acc.ToCmd()

    /// An executable that is taken as given, plus an argument string split the same way as `ofString`.
    let create (executable: string) (args: string) =
        let acc = Accumulator()
        acc.AddLiteral args
        let parsed = acc.ToCmd()
        {
            Executable = executable
            Arguments = if String.IsNullOrEmpty parsed.Executable then [] else parsed.Executable :: parsed.Arguments
            Secrets = parsed.Secrets |> Set.map ((+) 1)
        }

    /// An executable and arguments that are both taken exactly as given.
    let ofList (executable: string) (args: string list) = { Executable = executable; Arguments = args; Secrets = Set.empty }

    /// <summary>Appends one argument, taken exactly as given.</summary>
    let arg (value: string) (cmd: Cmd) = { cmd with Arguments = cmd.Arguments @ [ value ] }

    /// <summary>Appends arguments, taken exactly as given.</summary>
    let args (values: string list) (cmd: Cmd) = { cmd with Arguments = cmd.Arguments @ values }

    /// <summary>Appends arguments only when the condition holds.</summary>
    /// <remarks>
    /// Adding a flag conditionally is the most common edit anyone makes to a command line, and today is
    /// only expressible by duplicating the whole line — which is why anyone building a command abandons
    /// <c>cmd</c> and unquoted strings, losing the exact quoting <c>cmd</c> exists to provide. Three
    /// optional flags become eight branches. This fixes that.
    /// </remarks>
    let argIf (condition: bool) (values: string list) (cmd: Cmd) = if condition then args values cmd else cmd

    /// <summary>Appends arguments rendered from the given value, only when it is <c>Some</c>.</summary>
    /// <remarks>
    /// Parallels <c>Option</c> patterns like <c>iter</c> and <c>map</c>, avoiding the need to lift
    /// <c>Option.iter</c> around the whole command line.
    /// </remarks>
    let argWhenSome (value: 'a option) (render: 'a -> string list) (cmd: Cmd) =
        match value with
        | Some value -> args (render value) cmd
        | None -> cmd

    /// <summary>Appends one argument whose value must never be printed.</summary>
    let secretArg (value: string) (cmd: Cmd) =
        { cmd with
            Arguments = cmd.Arguments @ [ value ]
            Secrets = cmd.Secrets |> Set.add cmd.Arguments.Length }

    /// <summary>Appends a flag and value, masking the value everywhere the command is printed.</summary>
    /// <remarks>The flag stays visible: <c>-k ***</c> says more in a log than <c>***</c> does.</remarks>
    let secretOption (flag: string) (value: string) (cmd: Cmd) = cmd |> arg flag |> secretArg value

    /// <summary>Appends a masked flag and value only when a value exists, appending nothing otherwise.</summary>
    /// <remarks>
    /// The shape a publish step wants: no <c>.Value</c> under a <c>when'</c> that happens to guard it.
    /// </remarks>
    let secretOptionWhenSome (flag: string) (value: string option) (cmd: Cmd) =
        match value with
        | Some value -> secretOption flag value cmd
        | None -> cmd

    /// <summary>Reads an interpolated string, taking each hole as part of exactly one argument.</summary>
    /// <param name="secret">Marks every hole as unprintable, which is what <c>runSensitive</c> wants.</param>
    let ofFormattable (secret: bool) (command: FormattableString) =
        let values = command.GetArguments()
        let format = command.Format
        let acc = Accumulator()
        let mutable index = 0

        while index < format.Length do
            match format[index] with
            | '{' when index + 1 < format.Length && format[index + 1] = '{' ->
                acc.AddLiteral "{"
                index <- index + 2
            | '}' when index + 1 < format.Length && format[index + 1] = '}' ->
                acc.AddLiteral "}"
                index <- index + 2
            | '{' ->
                let close = format.IndexOf('}', index)
                if close < 0 then
                    // Unbalanced: nothing sensible to interpolate, so keep it as literal text.
                    acc.AddLiteral (format.Substring index)
                    index <- format.Length
                else
                    // The hole is `index[,alignment][:format]`; anything after the number is a format spec.
                    let hole = format.Substring(index + 1, close - index - 1)
                    let digits = hole |> Seq.takeWhile Char.IsDigit |> Seq.toArray |> String
                    let spec = hole.Substring digits.Length
                    let value =
                        match Int32.TryParse digits with
                        | true, i when i < values.Length -> String.Format("{0" + spec + "}", values[i])
                        | _ -> "{" + hole + "}"
                    acc.AddValue (value, secret)
                    index <- close + 1
            | ch ->
                acc.AddLiteral (string ch)
                index <- index + 1

        acc.ToCmd()

    /// How the command is printed: secrets masked, and anything containing whitespace quoted so the
    /// printed form can be pasted into a shell.
    let toLogString (cmd: Cmd) =
        let quoteIfNeeded (value: string) =
            if String.IsNullOrEmpty value then "\"\""
            elif value |> Seq.exists Char.IsWhiteSpace then "\"" + value + "\""
            else value

        cmd.Arguments
        |> List.mapi (fun i arg -> if cmd.Secrets.Contains i then mask else quoteIfNeeded arg)
        |> List.append [ quoteIfNeeded cmd.Executable ]
        |> String.concat " "

[<AutoOpen>]
module CmdHelpers =
    /// <summary>Builds a <see cref="T:Partas.Build.Cmd"/> from an interpolated command line, taking each hole as
    /// exactly one argument: <c>run (cmd $"dotnet build {project}")</c>.</summary>
    /// <remarks>
    /// This exists because <c>run $"..."</c> cannot reach a <c>FormattableString</c> overload. An interpolated
    /// string is a <c>string</c> unless the expected type says otherwise, and <c>run</c> has a <c>string</c>
    /// overload, so overload resolution takes it and the holes are gone before <c>run</c> ever sees them.
    /// <c>cmd</c> has no such competition. <c>runSensitive</c> has none either, which is why it takes the
    /// interpolated string directly.
    /// </remarks>
    let inline cmd (command: FormattableString) = Cmd.ofFormattable false command

namespace Partas.Build.Internal

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Spectre.Console
open Partas.Build

/// Runs a <see cref="T:Partas.Build.Cmd"/> as a step of a stage.
module CmdRunner =
    let private windowsExeExtensions = [ "exe"; "cmd"; "bat" ]

    let private windowsPaths =
        lazy
            (match Environment.GetEnvironmentVariable "PATH" with
             | null | "" -> []
             | path -> path.Split Path.PathSeparator |> List.ofArray)

    /// <summary>Finds what Windows would have run had a shell been involved.</summary>
    /// <remarks>
    /// <c>UseShellExecute</c> is false — it has to be, for environment variables and redirection — so Windows
    /// will not find <c>npm.cmd</c> from <c>npm</c>. Everywhere else the OS resolves the name itself.
    /// </remarks>
    let resolveExecutable (executable: string) =
        if
            not (RuntimeInformation.IsOSPlatform OSPlatform.Windows)
            || Path.IsPathRooted executable
            || not (String.IsNullOrWhiteSpace (Path.GetExtension executable))
        then
            executable
        else
            Directory.GetCurrentDirectory() :: windowsPaths.Value
            |> List.tryPick (fun directory ->
                windowsExeExtensions
                |> List.tryPick (fun extension ->
                    let file = Path.ChangeExtension(Path.Combine(directory, executable), extension)
                    if File.Exists file then Some file else None))
            |> Option.defaultValue executable

    /// The working directory and environment variables come from walking `ParentContext` upward.
    let toStartInfo (ctx: StageContext) (cmd: Cmd) =
        let startInfo = ProcessStartInfo(resolveExecutable cmd.Executable, UseShellExecute = false)
#if NETSTANDARD2_0
        for arg in cmd.Arguments do startInfo.Arguments <- startInfo.Arguments + " " + arg
#else
        for arg in cmd.Arguments do startInfo.ArgumentList.Add arg
#endif
        StageContext.getWorkingDir ctx |> ValueOption.iter (fun dir -> startInfo.WorkingDirectory <- dir)
        StageContext.buildEnvVars ctx |> Map.iter (fun key value -> startInfo.Environment[key] <- value)
        startInfo

    /// <summary>Kills the process and everything it started.</summary>
    /// <remarks>
    /// Fun.Build asks for a graceful exit first — <c>CloseMainWindow</c> on Windows, <c>SIGTERM</c> through a
    /// P/Invoke elsewhere — but neither reaches a console child's own children. Killing the tree is both
    /// simpler and more thorough; a build step that needs a graceful shutdown should own that itself.
    /// </remarks>
    let private kill (proc: Process) =
        try
#if NETSTANDARD2_0
            if not proc.HasExited then proc.Kill()
#else
            if not proc.HasExited then proc.Kill true
#endif
        with _ ->
            try proc.Kill() with _ -> ()
    open SpectreConsoleExt
    /// <summary>Runs <paramref name="cmd"/> and maps its exit code through the stage's acceptable exit codes.</summary>
    /// <remarks>A cancelled command succeeds: the runner that cancelled it is the one reporting why.</remarks>
    let run (ctx: StageContext) (index: StepIndex) (cancellationToken: CancellationToken) (cmd: Cmd) = async {
        let noPrefix = StageContext.getNoPrefixForStep ctx
        let escapedPrefix = if noPrefix then "" else StageContext.buildStepPrefix ctx index |> Markup.escape

        if not noPrefix then escapedPrefix |> Markup.green |> print
        Cmd.toLogString cmd
        |> vprintn ctx

        let output = StageContext.getOutput ctx
        let stepBuffer = StageContext.getStepBuffer ctx

        let toConsole =
            match output with
            | ValueNone | ValueSome StageOutput.Console -> true
            | _ -> false

        // Redirection costs the child's colours, so it is only worth it when the output has to be prefixed --
        // or when the stage has said it goes somewhere that is not the console, which cannot be done without it --
        // or when a step buffer is in play, since buffering a line is impossible without first receiving it here.
        // `noStdRedirectForStep` is the explicit opt out and wins over all three: it makes capture impossible, by
        // design.
        let redirect =
            (not noPrefix || not toConsole || stepBuffer.IsSome) && not (StageContext.getNoStdRedirectForStep ctx)
        let startInfo = toStartInfo ctx cmd

        if redirect then
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.StandardOutputEncoding <- Encoding.UTF8
            startInfo.StandardErrorEncoding <- Encoding.UTF8

        use proc = Process.Start startInfo

        if redirect then
            // Both streams are read, always: a child whose stderr is redirected and never drained blocks on a
            // full pipe once it has written a few kilobytes there, and waits for a reader that never comes.
            let onData (stream: StdStream) (ev: DataReceivedEventArgs) =
                if not (String.IsNullOrEmpty ev.Data) then
                    StageContext.writeLine ctx stream (if noPrefix then ev.Data else escapedPrefix + " " + ev.Data)
            proc.OutputDataReceived.Add (onData StdStream.Out)
            proc.ErrorDataReceived.Add (onData StdStream.Err)
            proc.BeginOutputReadLine()
            proc.BeginErrorReadLine()

        // Killing the process is what ends the wait below, whichever token asked for it: the ambient one carries
        // the stage's timeout, the parameter one belongs to whoever wrote the step.
        //
        // The kill must happen from a token registration and not from `Async.OnCancel`: a tree kill issued from a
        // cancellation continuation reaches the child but silently leaves its grandchildren alive, so
        // `cmd /c ping` would go on pinging long after the stage had given up on it.
        let killed = ref 0

        let killOnce () =
            if Interlocked.Exchange(&killed.contents, 1) = 0 then
                $"{escapedPrefix} is cancelled or timed out; the process will be killed."
                |> Markup.yellow
                |> printn
                kill proc

        let! ambientToken = Async.CancellationToken
        use _ambient = ambientToken.Register killOnce
        use _registration = cancellationToken.Register killOnce
#if NETSTANDARD2_0
        do proc.WaitForExit()
#else
        do! proc.WaitForExitAsync() |> Async.AwaitTask
#endif

        return
            if cancellationToken.IsCancellationRequested then Ok()
            else
                match StageContext.mapExitCodeToResult ctx proc.ExitCode with
                | Ok () -> Ok()
                // The point of holding the output back: nothing was printed, so the reason has to travel in the
                // error instead, which is what reaches `printError` and the GitHub Actions annotation.
                | Error message ->
                    match output with
                    | ValueSome(StageOutput.Captured capture) ->
                        // A step buffer in play holds this step's own lines; the author's capture only sees them
                        // once flushed, by which point a concurrent sibling's lines may already be in it. Lifting
                        // from the buffer when there is one keeps the annotation to this step's own output.
                        let failureText =
                            match stepBuffer with
                            | ValueSome buffer when not buffer.IsEmpty -> ValueSome buffer.FailureText
                            | ValueSome _ -> ValueNone
                            | ValueNone when not capture.IsEmpty -> ValueSome capture.FailureText
                            | ValueNone -> ValueNone
                        match failureText with
                        | ValueSome text -> Error $"%s{message}%s{Environment.NewLine}%s{text}"
                        | ValueNone -> Error message
                    | _ -> Error message
    }

    /// The step function for a command that is only known once the stage is running.
    let step (buildCmd: StageContext -> Async<Cmd>) (cancellationToken: CancellationToken): StageContext -> StepIndex -> Async<Result<unit, string>> =
        fun ctx index -> async {
            let! cmd = buildCmd ctx
            return! run ctx index cancellationToken cmd
        }

    let stepOption (buildCmd: StageContext -> Async<Cmd option>) (cancellationToken: CancellationToken): StageContext -> StepIndex -> Async<Result<unit, string>> =
        fun ctx index -> async {
            match! buildCmd ctx with
            | Some cmd -> return! run ctx index cancellationToken cmd
            | None -> return Ok()
        }

    let stepResult (buildCmd: StageContext -> Async<Result<Cmd, string>>) (cancellationToken: CancellationToken): StageContext -> StepIndex -> Async<Result<unit, string>> =
        fun ctx index -> async {
            match! buildCmd ctx with
            | Ok cmd -> return! run ctx index cancellationToken cmd
            | Error message -> return Error message
        }

    let stepResultOption (buildCmd: StageContext -> Async<Result<Cmd option, string>>) (cancellationToken: CancellationToken): StageContext -> StepIndex -> Async<Result<unit, string>> =
        fun ctx index -> async {
            match! buildCmd ctx with
            | Ok (Some cmd) -> return! run ctx index cancellationToken cmd
            | Ok None -> return Ok()
            | Error message -> return Error message
        }

[<AutoOpen>]
module StageContextRunExts =
    module StageContext =
        open FsToolkit.ErrorHandling
        module Steps =
            let inline addCmd (ct: CancellationToken voption) (cmd: Cmd) (ctx: StageContext) =
                let ct = defaultValueArg ct CancellationToken.None
                StageContext.addLabelledStepFn (Cmd.toLogString cmd) (CmdRunner.step (fun _ -> Async.singleton cmd) ct) ctx
            let inline addCmdString (ct: CancellationToken voption) (command: string) (ctx: StageContext) = addCmd ct (Cmd.ofString command) ctx
            let inline addCmdFormattable (ct: CancellationToken voption) (command: FormattableString) (ctx: StageContext) = addCmd ct (Cmd.ofFormattable false command) ctx
            let inline addCmdList (ct: CancellationToken voption) (executable: string) (args: string list) (ctx: StageContext) = addCmd ct (Cmd.ofList executable args) ctx
            let inline addHttpHealthCheck (ct: CancellationToken voption) ([<InlineIfLambda>] configRequest: HttpRequestMessage -> unit) (url: string) (ctx: StageContext) =
                let ct = defaultValueArg ct CancellationToken.None
                StageContext.addStepFn (fun ctx _ -> StageContext.runHttpHealthCheckCancelableWithConfigRequest ctx ct configRequest url) ctx
    module BuildStage =
        module Steps =
            let inline addCmd (ct: CancellationToken voption) (cmd: Cmd) ([<InlineIfLambda>] build: BuildStage) =
                build >> StageContext.Steps.addCmd ct cmd
            let inline addCmdString ct command ([<InlineIfLambda>] build: BuildStage) = build >> StageContext.Steps.addCmdString ct command
            let inline addCmdFormattable ct command ([<InlineIfLambda>] build: BuildStage) = build >> StageContext.Steps.addCmdFormattable ct command
            let inline addCmdList ct executable args ([<InlineIfLambda>] build: BuildStage) = build >> StageContext.Steps.addCmdList ct executable args
            let inline addHttpHealthCheck ct configRequest url ([<InlineIfLambda>] build: BuildStage) = build >> StageContext.Steps.addHttpHealthCheck ct configRequest url
