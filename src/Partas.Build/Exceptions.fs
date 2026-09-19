[<AutoOpen>]
module Partas.Build.ErrorHandling

open System

type PipelineCancelledException(msg: string) = inherit Exception(msg)
type PipelineFailedException =
    inherit Exception
    new(msg: string) = { inherit Exception(msg) }
    new(msg: string, ex: exn) = { inherit Exception(msg, ex) }

type StepSoftCancelledException(msg: string) = inherit Exception(msg)
type StageSoftCancelledException(msg: string) = inherit Exception(msg)


/// <summary>Carries a <see cref="T:Partas.Build.Internal.FailureCause"/> out of the operation that produced it.</summary>
type OperationFailedException(cause: FailureCause) =
    inherit Exception(
        match cause with
        | FailureCause.Command(command, exitCode, _) -> $"'%s{command}' exited with %i{exitCode}."
        | FailureCause.Start(executable, error) -> $"'%s{executable}' could not be started. %s{error.Message}"
        | FailureCause.Raised error -> error.Message
        | FailureCause.TimedOut -> "The step timed out."
        | FailureCause.Reported message -> message)

    member _.Cause = cause

/// <summary>Why a step failed, with the evidence the failure carries.</summary>
/// <remarks>
/// A cause stays structured up to the print site, where
/// <see cref="M:Partas.Build.Internal.FailureCause.describe"/> renders it.
/// </remarks>
and [<RequireQualifiedAccess>] FailureCause =
    /// <summary>A command completed with an exit code the stage rejects.</summary>
    /// <remarks>
    /// <c>command</c> is the log form, with secrets masked. <c>captured</c> holds the child's raw output where
    /// the operation asked for capture.
    /// </remarks>
    | Command of command: string * exitCode: int * captured: CommandResult voption
    /// <summary>A command failed to start.</summary>
    /// <remarks>The executable name and the platform's exception are the whole of the evidence a process that
    /// never ran leaves behind.</remarks>
    | Start of executable: string * error: exn
    /// <summary>An exception escaped the operation, a parsing failure among them.</summary>
    | Raised of error: exn
    /// The executing scope's own timeout expired.
    | TimedOut
    /// A failure the operation reported itself.
    | Reported of message: string

/// <summary>How a step of deferred work ended.</summary>
[<Struct; RequireQualifiedAccess>]
type StepOutcome =
    | Completed
    | Failed of cause: FailureCause

/// <summary>How a stage ended.</summary>
[<Struct; RequireQualifiedAccess>]
type StageOutcome =
    | Succeeded
    /// Inactive: a condition on the stage was false.
    | Skipped
    /// <summary><c>error</c> is the message of the first exception a step raised, falling back to the first
    /// line of the first cause the scope recorded.</summary>
    | Failed of error: string

module FailureCause =
    /// <summary>The line a failed step prints and annotates with.</summary>
    /// <remarks>
    /// A captured failure appends the child's own text — stderr where the command used it, stdout otherwise —
    /// on a line of its own, the way a stage's capture is lifted.
    /// </remarks>
    let describe (cause: FailureCause) =
        match cause with
        | FailureCause.Command(command, exitCode, captured) ->
            let headline = $"Exit code not acceptable. '%s{command}' exited with %i{exitCode}."

            match captured with
            | ValueSome result ->
                let evidence = if String.IsNullOrWhiteSpace result.Stderr then result.Stdout else result.Stderr
                if String.IsNullOrWhiteSpace evidence then headline else $"%s{headline}%s{Environment.NewLine}%s{evidence.TrimEnd()}"
            | ValueNone -> headline
        | FailureCause.Start(executable, error) -> $"The command '%s{executable}' could not be started. %s{error.Message}"
        | FailureCause.Raised error -> $"%s{error.GetType().Name}: %s{error.Message}"
        | FailureCause.TimedOut -> "The step timed out."
        | FailureCause.Reported message -> message

    /// The first line of <c>describe</c>, for a report with room for one line.
    let summarise (cause: FailureCause) =
        let described = describe cause
        match described.IndexOf '\n' with
        | -1 -> described
        | breakAt -> described.Substring(0, breakAt).TrimEnd()

    /// <summary>The exception <paramref name="cause"/> travels as.</summary>
    /// <remarks>An exception that escaped an operation is itself; every other cause travels inside an
    /// <see cref="T:Partas.Build.ErrorHandling.OperationFailedException"/>, which keeps it readable at the catch site.</remarks>
    let toException (cause: FailureCause) : exn =
        match cause with
        | FailureCause.Raised error -> error
        | _ -> OperationFailedException cause
