namespace Partas.Build


namespace Partas.Build.Internal

open System
open System.Net.Http
open System.Threading
open Partas.Build

/// Runs a <see cref="T:Partas.Build.Cmd"/> as a step of a stage.
module CmdRunner =
    /// The working directory and environment variables come from walking `ParentContext` upward.
    let toStartInfo (ctx: StageContext) (cmd: Cmd) =
        Cmd.toStartInfo
            (StageContext.getWorkingDir ctx)
            (StageContext.buildEnvVars ctx)
            cmd

    open Output

    /// <summary>The prefix a step's lines carry, escaped for Spectre, and empty where the stage prints none.</summary>
    let stepPrefix (ctx: StageContext) (index: StepIndex) =
        if StageContext.getNoPrefixForStep ctx then "" else StageContext.buildStepPrefix ctx index |> Markup.escape

    /// <summary>Announces the step and logs the command line it is about to run.</summary>
    /// <remarks>The command line is the log form, with secrets masked, and reaches a verbose stage alone.</remarks>
    let logCommand (ctx: StageContext) (escapedPrefix: string) (cmd: Cmd) =
        if not (String.IsNullOrEmpty escapedPrefix) then escapedPrefix |> Markup.green |> print
        Cmd.toLogString cmd |> vprintn ctx

    /// <summary>Where a streamed step's lines go, given the stage's output settings.</summary>
    /// <remarks>
    /// Redirection costs the child's colours, so it is only worth it when the output has to be prefixed —
    /// or when the stage has said it goes somewhere that is not the console, which cannot be done without it —
    /// or when a step buffer is in play, since buffering a line is impossible without first receiving it here.
    /// <c>noStdRedirectForStep</c> is the explicit opt out and wins over all three: it makes capture impossible, by
    /// design.
    /// </remarks>
    let outputPolicy (ctx: StageContext) (escapedPrefix: string) =
        let toConsole =
            match StageContext.getOutput ctx with
            | ValueNone | ValueSome StageOutput.Console -> true
            | _ -> false

        let noPrefix = String.IsNullOrEmpty escapedPrefix
        let redirect =
            (not noPrefix || not toConsole || (StageContext.getStepBuffer ctx).IsSome)
            && not (StageContext.getNoStdRedirectForStep ctx)

        if not redirect then OutputPolicy.Inherit
        else
            // An empty line adds nothing to a prefixed log. `ProcessExecutor.capture` is where the raw
            // text, blank lines and all, is available.
            let write (stream: StdStream) (line: string) =
                if not (String.IsNullOrEmpty line) then
                    StageContext.writeLine ctx stream (if noPrefix then line else escapedPrefix + " " + line)
            OutputPolicy.Lines(write StdStream.Out, write StdStream.Err)

    /// <summary>Says that the step's process is about to be killed.</summary>
    /// <remarks>Passed to the executor, which runs it once, before the kill.</remarks>
    let announceKill (escapedPrefix: string) () =
        $"{escapedPrefix} is cancelled or timed out; the process will be killed."
        |> Markup.yellow
        |> printn

    /// <summary>Runs cmd and maps its exit code through the stage's acceptable exit codes.</summary>
    /// <remarks>A cancelled command succeeds: the runner that cancelled it is the one reporting why.</remarks>
    let run (ctx: StageContext) (index: StepIndex) (cancellationToken: CancellationToken) (cmd: Cmd) = async {
        let escapedPrefix = stepPrefix ctx index
        logCommand ctx escapedPrefix cmd

        let output = StageContext.getOutput ctx
        let stepBuffer = StageContext.getStepBuffer ctx
        let startInfo = toStartInfo ctx cmd
        let policy = outputPolicy ctx escapedPrefix

        // Killing the process is what ends the wait, whichever token asked for it: the ambient one carries the
        // stage's timeout, the parameter one belongs to whoever wrote the step. Linking them gives the executor
        // one token to register on and leaves the two here, where telling them apart is what decides the result.
        let! ambientToken = Async.CancellationToken
        use linked = CancellationTokenSource.CreateLinkedTokenSource(ambientToken, cancellationToken)

        let announce = announceKill escapedPrefix

        // A completed run rather than a raised cancellation: a stage timeout has to reach
        // `mapExitCodeToResult` as the exit code of the kill, and a caller-token cancellation the `Ok()` below.
        let! exitCode = ProcessExecutor.streamToExit startInfo policy linked.Token announce |> Async.AwaitTask

        return
            if cancellationToken.IsCancellationRequested then Ok()
            else
                match StageContext.mapExitCodeToResult ctx exitCode with
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
                            | ValueSome buffer when not (OutputCapture.isEmpty buffer) -> ValueSome (OutputCapture.failureText buffer)
                            | ValueSome _ -> ValueNone
                            | ValueNone when not (OutputCapture.isEmpty capture) -> ValueSome (OutputCapture.failureText capture)
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
