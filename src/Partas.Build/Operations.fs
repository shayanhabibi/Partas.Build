namespace Partas.Build

open System
open System.ComponentModel
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Partas.Build.Internal

/// <summary>What an awaited task handed over.</summary>
module private Awaited =
    /// <summary>The exception the work raised, with the aggregate an await wrapped it in removed.</summary>
    let rec unwrap (error: exn) =
        match error with
        | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 -> unwrap aggregate.InnerExceptions[0]
        | _ -> error

    /// <summary>Raises <paramref name="error"/> again with the stack trace it already carries.</summary>
    /// <remarks>Use it where <c>reraise</c> is unavailable: inside an <c>async</c> handler.</remarks>
    let rethrow (error: exn) : 'T =
        ExceptionDispatchInfo.Capture(error).Throw()
        Unchecked.defaultof<'T>

/// <summary>Work deferred until a stage executes it.</summary>
/// <remarks>
/// <c>Execute</c> is a reader over the executing step's runtime context: it runs when the step it belongs to
/// runs, and building one is pure.
/// <para>A failure travels as an <see cref="T:Partas.Build.Internal.OperationFailedException"/> carrying a
/// <see cref="T:Partas.Build.Internal.FailureCause"/>, which <c>Operation.toStepOutcome</c> reads as a
/// <see cref="T:Partas.Build.Internal.StepOutcome"/>.</para>
/// </remarks>
[<Struct>]
type Operation<'T> = { Execute: RuntimeContext -> Async<'T> }

module Operation =
    /// <summary>An operation answering <paramref name="value"/> as it stands.</summary>
    let ret (value: 'T) = { Execute = fun _ -> async.Return value }

    /// <summary>An operation executing <paramref name="work"/> under the stage's runtime context.</summary>
    let ofAsync (work: Async<'T>) = { Execute = fun _ -> work }

    /// <summary>An operation executing the task <paramref name="factory"/> builds.</summary>
    /// <remarks>A definition carries the factory itself, and the step that runs applies it.</remarks>
    let ofTaskFactory (factory: unit -> Task<'T>) = {
        Execute = fun _ -> async { return! factory () |> Async.AwaitTask }
    }

    /// <summary>The same work, answering <paramref name="fn"/> applied to its value.</summary>
    let map (fn: 'T -> 'U) (operation: Operation<'T>) = {
        Execute = fun context -> async {
            let! value = operation.Execute context
            return fn value
        }
    }

    /// <summary>The operation <paramref name="fn"/> answers for the first one's value, run after it.</summary>
    /// <remarks>Sequencing is local to the executing scope: it orders work inside one step and declares no
    /// stage dependency.</remarks>
    let bind (fn: 'T -> Operation<'U>) (operation: Operation<'T>) = {
        Execute = fun context -> async {
            let! value = operation.Execute context
            return! (fn value).Execute context
        }
    }

    /// <summary>Raises <paramref name="cause"/> out of the operation running it.</summary>
    let fail (cause: FailureCause) : 'T = raise (OperationFailedException cause)

    /// <summary>Runs <paramref name="operation"/> as a step and classifies what escapes it.</summary>
    /// <remarks>
    /// A reported cause, and an exception the operation let through, both become
    /// <see cref="T:Partas.Build.Internal.StepOutcome"/>.<c>Failed</c>; a parsing failure arrives that way,
    /// holding the exception itself. Cancellation propagates, along with the pipeline and soft-cancellation
    /// exceptions, to the runner that classifies it: only the runner holds the tokens that say whose it was.
    /// </remarks>
    let toStepOutcome (operation: Operation<unit>) (context: RuntimeContext) = async {
        try
            do! operation.Execute context
            return StepOutcome.Completed
        with error ->
            match Awaited.unwrap error with
            | :? OperationFailedException as failed -> return StepOutcome.Failed failed.Cause
            | :? OperationCanceledException
            | :? PipelineCancelledException
            | :? PipelineFailedException
            | :? StepSoftCancelledException
            | :? StageSoftCancelledException -> return Awaited.rethrow error
            | raised -> return StepOutcome.Failed (FailureCause.Raised raised)
    }

/// <summary>Commands as operations: deferred, stage-configured, and explicit about their failure policy.</summary>
[<AutoOpen>]
module Operations =
    /// <summary>The start info the executing stage gives a command: its working directory and environment,
    /// resolved by walking <c>ParentContext</c> upward.</summary>
    let private startInfo (context: RuntimeContext) (command: Cmd) = CmdRunner.toStartInfo context.Stage command

    /// <summary>Reports a failure to start as the executable that could not run.</summary>
    /// <remarks>A process that never ran carries its executable name as the whole of its evidence.</remarks>
    let private overStart (command: Cmd) (work: Async<'T>) = async {
        try
            return! work
        with error ->
            match Awaited.unwrap error with
            | :? Win32Exception as unstarted -> return Operation.fail (FailureCause.Start(command.Executable, unstarted))
            | _ -> return Awaited.rethrow error
    }

    let private checkExitCode (context: RuntimeContext) (command: Cmd) (exitCode: int) (captured: CommandResult voption) =
        if StageContext.isAcceptableExitCode context.Stage exitCode then ()
        else Operation.fail (FailureCause.Command(Cmd.toLogString command, exitCode, captured))

    /// <summary>Runs <paramref name="command"/>, streaming its output the way the stage routes it.</summary>
    /// <remarks>
    /// The exit code is checked against the acceptable set the stage resolves, and an unacceptable one fails the
    /// operation naming the command and the code. The output went to the stage's sink as it arrived, so the
    /// failure carries the command and the code alone.
    /// </remarks>
    let execute (command: Cmd) : Operation<unit> = {
        Execute = fun context -> async {
            let stage = context.Stage
            let escapedPrefix = CmdRunner.stepPrefix stage context.StepIndex
            CmdRunner.logCommand stage escapedPrefix command

            let! token = Async.CancellationToken

            let! exitCode =
                ProcessExecutor.stream (startInfo context command) (CmdRunner.outputPolicy stage escapedPrefix) token
                    (CmdRunner.announceKill escapedPrefix)
                |> Async.AwaitTask
                |> overStart command

            return checkExitCode context command exitCode ValueNone
        }
    }

    /// <summary>Runs <paramref name="command"/> and answers its raw stdout and stderr.</summary>
    /// <remarks>
    /// An exit code the stage rejects fails the operation, and the captured result travels with the failure as
    /// its evidence. Raw text is application data and can hold secrets, so printing a successful capture is the
    /// caller's decision.
    /// </remarks>
    let executeCapture (command: Cmd) : Operation<CommandResult> = {
        Execute = fun context -> async {
            let stage = context.Stage
            CmdRunner.logCommand stage "" command

            let! token = Async.CancellationToken

            let! result =
                ProcessExecutor.capture (startInfo context command) token
                    (CmdRunner.announceKill (CmdRunner.stepPrefix stage context.StepIndex))
                |> Async.AwaitTask
                |> overStart command

            checkExitCode context command result.ExitCode (ValueSome result)
            return result
        }
    }

    /// <summary>Runs <paramref name="command"/> and answers the result of every process that completed.</summary>
    /// <remarks>
    /// Every exit code is a result here, the rejected ones included, so branching on one is the caller's. Every
    /// <see cref="T:Partas.Build.CommandResult"/> is a process that ran to completion — a failure to start and a
    /// cancellation are outcomes of their own — so a fallback keyed on an exit code applies to completed
    /// processes alone.
    /// </remarks>
    let attemptCapture (command: Cmd) : Operation<CommandResult> = {
        Execute = fun context -> async {
            let stage = context.Stage
            CmdRunner.logCommand stage "" command

            let! token = Async.CancellationToken

            return!
                ProcessExecutor.capture (startInfo context command) token
                    (CmdRunner.announceKill (CmdRunner.stepPrefix stage context.StepIndex))
                |> Async.AwaitTask
                |> overStart command
        }
    }
