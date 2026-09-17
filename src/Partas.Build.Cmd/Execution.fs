namespace Partas.Build

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks

/// <summary>A finished process: the code it exited with and the text it wrote.</summary>
/// <remarks>
/// <c>Stdout</c> and <c>Stderr</c> are the child's own bytes decoded as UTF-8, blank lines and newlines
/// included, before any prefix or other display formatting. They are application data: a command is free to
/// write a secret there, so printing a successful result is the caller's decision and never the executor's.
/// </remarks>
type CommandResult = {
    ExitCode: int
    Stdout: string
    Stderr: string
}

/// <summary>Where a streamed process's output goes.</summary>
/// <remarks>A streamed line lives for the duration of its callback, and the exit code is the whole of what a
/// streamed command reports.</remarks>
[<RequireQualifiedAccess>]
type OutputPolicy =
    /// The child writes to the parent's own console handles and keeps its colours. Routing a line requires
    /// redirecting it, which this policy declines.
    | Inherit
    /// <summary>Each line reaches the callback for the stream it arrived on, as it arrives.</summary>
    /// <remarks>A line arrives stripped of its newline, empty ones included.</remarks>
    | Lines of onStdout: (string -> unit) * onStderr: (string -> unit)

/// <summary>Process lifecycle: argument transport, start, draining, completion and cancellation.</summary>
/// <remarks>
/// The whole of it is stage-independent. Working directory, environment, acceptable exit codes, labels,
/// prefixes and diagnostics are the caller's, and reach the executor as a <see cref="T:System.Diagnostics.ProcessStartInfo"/>
/// it starts unmodified apart from the redirection its output policy requires.
/// </remarks>
module ProcessExecutor =

    /// Puts an argument list on a <see cref="T:System.Diagnostics.ProcessStartInfo"/> with the boundaries the
    /// child will read back.
    module Arguments =
        /// <summary>Quotes one argument the way <c>ArgumentList</c> does: the MSVCRT rules a C runtime parses
        /// a command line by.</summary>
        /// <remarks>
        /// A value free of whitespace and quotes is returned as it is. Everything else is wrapped in quotes,
        /// with each embedded quote escaped and each backslash run that precedes a quote doubled, so the child
        /// reads back the string that went in. An empty argument becomes <c>""</c> and survives as one argument.
        /// </remarks>
        let quote (value: string) =
            let isDelimiter ch = ch = ' ' || ch = '\t' || ch = '\n' || ch = '' || ch = '"'

            if not (String.IsNullOrEmpty value) && not (Seq.exists isDelimiter value) then value
            else
                let quoted = StringBuilder()
                quoted.Append '"' |> ignore
                let mutable index = 0

                while index < value.Length do
                    let mutable backslashes = 0

                    while index < value.Length && value[index] = '\\' do
                        index <- index + 1
                        backslashes <- backslashes + 1

                    if index = value.Length then
                        // The run ends the argument, and the closing quote follows it: double it so the child
                        // reads backslashes rather than an escaped quote.
                        quoted.Append ('\\', backslashes * 2) |> ignore
                    elif value[index] = '"' then
                        quoted.Append('\\', backslashes * 2 + 1).Append '"' |> ignore
                        index <- index + 1
                    else
                        quoted.Append('\\', backslashes).Append value[index] |> ignore
                        index <- index + 1

                quoted.Append('"').ToString()

        /// <summary>Sets the arguments, through <c>ArgumentList</c> where the target has one and through a
        /// quoted <c>Arguments</c> string otherwise.</summary>
        /// <remarks>Both transports preserve the same argument boundaries; the <c>netstandard2.0</c> build pays
        /// for it with <see cref="M:quote"/>.</remarks>
        let transport (startInfo: ProcessStartInfo) (arguments: string seq) =
#if NETSTANDARD2_0
            startInfo.Arguments <- arguments |> Seq.map quote |> String.concat " "
#else
            for argument in arguments do
                startInfo.ArgumentList.Add argument
#endif

#if NETSTANDARD2_0
    /// <summary>The tree-killing <c>Process.Kill(bool)</c>, where the running framework has one.</summary>
    /// <remarks>
    /// The <c>netstandard2.0</c> surface stops at <c>Kill()</c>, which reaches the child alone. A host of
    /// .NET Core 3.0 or later carries the overload that reaches the whole tree, and this finds it there. On an
    /// older host a grandchild outlives the kill.
    /// </remarks>
    let private treeKill = lazy typeof<Process>.GetMethod ("Kill", [| typeof<bool> |])
#endif

    /// <summary>Kills the process and everything it started.</summary>
    /// <remarks>
    /// Killing the tree is what reaches a console child's own children; <c>CloseMainWindow</c> and a signal to
    /// the child stop at the child. A step that needs a graceful shutdown owns that itself.
    /// </remarks>
    let private kill (proc: Process) =
        try
            if not proc.HasExited then
#if NETSTANDARD2_0
                match treeKill.Value with
                | null -> proc.Kill()
                | overload -> overload.Invoke (proc, [| box true |]) |> ignore
#else
                proc.Kill true
#endif
        with _ ->
            try proc.Kill() with _ -> ()

    /// <summary>Starts the process, drains it, and reports the exit code alongside whatever draining produced.</summary>
    /// <remarks>
    /// Draining starts before the wait: a child whose redirected stream is read only afterwards blocks on a full
    /// pipe and waits for a reader that never comes.
    ///
    /// The kill is issued from a cancellation registration and never from a continuation of the wait. A tree kill
    /// from a cancellation continuation reaches the child and silently leaves its grandchildren running, so
    /// <c>cmd /c ping</c> goes on pinging long after the caller has given up on it.
    /// </remarks>
    let private execute
        (startInfo: ProcessStartInfo)
        (token: CancellationToken)
        (onCancelled: unit -> unit)
        (drain: Process -> Task<'T>)
        : Task<struct (int * 'T)>
        =
        task {
            use proc = new Process(StartInfo = startInfo)
            if not (proc.Start()) then failwith $"No process was started for '%s{startInfo.FileName}'."
            let killed = ref 0

            use _registration =
                token.Register (fun () ->
                    if Interlocked.Exchange (&killed.contents, 1) = 0 then
                        // A diagnostic stands between cancellation and the kill only for as long as it succeeds.
                        (try onCancelled () with _ -> ())
                        kill proc)

            let draining = drain proc
#if NETSTANDARD2_0
            do! Task.Run (fun () -> proc.WaitForExit())
#else
            do! proc.WaitForExitAsync()
#endif
            let! drained = draining
            return struct (proc.ExitCode, drained)
        }

    let private redirect (startInfo: ProcessStartInfo) =
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.StandardOutputEncoding <- Encoding.UTF8
        startInfo.StandardErrorEncoding <- Encoding.UTF8

    /// <summary>Runs the process under <paramref name="policy"/> and reports its exit code.</summary>
    /// <remarks>
    /// A process the token killed reports the exit code of that kill, which is what a caller mapping exit codes
    /// to its own outcome wants. <see cref="M:stream"/> raises instead.
    /// </remarks>
    /// <param name="startInfo"></param>
    /// <param name="policy"></param>
    /// <param name="token">Kills the process and everything it started.</param>
    /// <param name="onCancelled">Runs once, before the kill, for a caller with something to report.</param>
    let streamToExit (startInfo: ProcessStartInfo) (policy: OutputPolicy) (token: CancellationToken) (onCancelled: unit -> unit) =
        task {
            let drain =
                match policy with
                | OutputPolicy.Inherit -> fun (_: Process) -> Task.FromResult ()
                | OutputPolicy.Lines(onStdout, onStderr) ->
                    redirect startInfo

                    fun (proc: Process) ->
                        // Both streams are read, always: a child whose stderr is redirected and never drained
                        // blocks once it has written a few kilobytes there.
                        let receive (onLine: string -> unit) (ev: DataReceivedEventArgs) = if not (isNull ev.Data) then onLine ev.Data
                        proc.OutputDataReceived.Add (receive onStdout)
                        proc.ErrorDataReceived.Add (receive onStderr)
                        proc.BeginOutputReadLine()
                        proc.BeginErrorReadLine()
                        Task.FromResult ()

            let! struct (exitCode, ()) = execute startInfo token onCancelled drain
            return exitCode
        }

    /// <summary>Runs the process and returns everything it wrote, raw.</summary>
    /// <remarks>
    /// The two streams are captured apart and read concurrently, so a child writing to both fills neither pipe.
    /// A process the token killed reports the exit code of that kill together with the text it had written by
    /// then; <see cref="M:capture"/> raises instead.
    /// </remarks>
    /// <param name="startInfo"></param>
    /// <param name="token">Kills the process and everything it started.</param>
    /// <param name="onCancelled">Runs once, before the kill, for a caller with something to report.</param>
    let captureToExit (startInfo: ProcessStartInfo) (token: CancellationToken) (onCancelled: unit -> unit) =
        task {
            redirect startInfo

            let drain (proc: Process) =
                task {
                    let readingOut = proc.StandardOutput.ReadToEndAsync()
                    let readingErr = proc.StandardError.ReadToEndAsync()
                    let! stdout = readingOut
                    let! stderr = readingErr
                    return struct (stdout, stderr)
                }

            let! struct (exitCode, struct (stdout, stderr)) = execute startInfo token onCancelled drain
            return { ExitCode = exitCode; Stdout = stdout; Stderr = stderr }
        }

    /// <summary>Runs the process under <paramref name="policy"/>, raising
    /// <see cref="T:System.OperationCanceledException"/> when <paramref name="token"/> cancelled it.</summary>
    /// <remarks>A cancelled command has no exit code worth reading: the token that killed it decided the outcome.</remarks>
    /// <param name="startInfo"></param>
    /// <param name="policy"></param>
    /// <param name="token">Kills the process and everything it started.</param>
    /// <param name="onCancelled">Runs once, before the kill, for a caller with something to report.</param>
    let stream (startInfo: ProcessStartInfo) (policy: OutputPolicy) (token: CancellationToken) (onCancelled: unit -> unit) =
        task {
            let! exitCode = streamToExit startInfo policy token onCancelled
            token.ThrowIfCancellationRequested()
            return exitCode
        }

    /// <summary>Captures the process, raising <see cref="T:System.OperationCanceledException"/> when
    /// <paramref name="token"/> cancelled it.</summary>
    /// <remarks>A cancelled command yields no <see cref="T:Partas.Build.CommandResult"/>: partial text is evidence
    /// of a kill, not of what the command had to say.</remarks>
    /// <param name="startInfo"></param>
    /// <param name="token">Kills the process and everything it started.</param>
    /// <param name="onCancelled">Runs once, before the kill, for a caller with something to report.</param>
    let capture (startInfo: ProcessStartInfo) (token: CancellationToken) (onCancelled: unit -> unit) =
        task {
            let! result = captureToExit startInfo token onCancelled
            token.ThrowIfCancellationRequested()
            return result
        }
