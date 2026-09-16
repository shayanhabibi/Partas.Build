namespace Partas.Build


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
    /// The working directory and environment variables come from walking `ParentContext` upward.
    let toStartInfo (ctx: StageContext) (cmd: Cmd) =
        Cmd.toStartInfo
            (StageContext.getWorkingDir ctx)
            (StageContext.buildEnvVars ctx)
            cmd

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
    /// <summary>Runs cmd and maps its exit code through the stage's acceptable exit codes.</summary>
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
