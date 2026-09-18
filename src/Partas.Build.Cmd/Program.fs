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
    open System.Diagnostics
    open System.Threading
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
    /// <param name="command"></param>
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


    module Internal =
        open System.IO
        open System.Runtime.InteropServices
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
    let toStartInfo (workingDir: string voption) (envVar: Map<string, string>) (cmd: Cmd) =
        let startInfo = ProcessStartInfo(Internal.resolveExecutable cmd.Executable, UseShellExecute = false)
        ProcessExecutor.Arguments.transport startInfo cmd.Arguments
        workingDir |> ValueOption.iter (fun dir -> startInfo.WorkingDirectory <- dir)
        envVar |> Map.iter (fun key value -> startInfo.Environment[key] <- value)
        startInfo

    /// <summary>Runs the command to completion and reports its exit code with its output as lines.</summary>
    /// <remarks>
    /// The lines are the child's text split on its own newlines, with empty ones dropped, so a caller after the
    /// raw text — blank lines and all — reaches <see cref="M:Partas.Build.ProcessExecutor.capture"/> instead.
    /// </remarks>
    let run (workingDir: string voption) (envVar: Map<string, string>) (cmd: Cmd) = task {
        let! result = ProcessExecutor.capture (toStartInfo workingDir envVar cmd) CancellationToken.None ignore

        let lines (text: string) =
            text.Split '\n'
            |> Array.map (fun line -> line.TrimEnd '\r')
            |> Array.filter (String.IsNullOrEmpty >> not)

        return
            struct
            {| exitCode = result.ExitCode
               output = lines result.Stdout
               error = lines result.Stderr |}
    }

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
