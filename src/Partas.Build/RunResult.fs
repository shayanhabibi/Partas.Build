namespace Partas.Build

/// <summary>The process exit codes a command invocation ends with.</summary>
/// <remarks>
/// <c>UsageError</c> covers every failure to parse or validate the command line, dependency validation included,
/// and guarantees no stage ran.
/// </remarks>
[<RequireQualifiedAccess>]
module ExitCode =
    /// Every pipeline succeeded, or the invocation printed help, a version or an <c>--explain</c> tree.
    [<Literal>]
    let Success = 0

    /// A stage failed.
    [<Literal>]
    let Failure = 1

    /// The command line did not parse or validate, or the pipelines it selects failed dependency validation.
    [<Literal>]
    let UsageError = 2

    /// The run was cancelled, by a timeout of the pipeline or by the invocation's cancellation token.
    [<Literal>]
    let Cancelled = 130

/// <summary>The category of an invocation's result; <c>ExitCode</c> carries the corresponding code.</summary>
[<RequireQualifiedAccess; Struct>]
type RunOutcome =
    /// Exit code <c>0</c>.
    | Succeeded
    /// Exit code <c>1</c>, or any code outside the categories below.
    | Failed
    /// Exit code <c>2</c>.
    | UsageError
    /// Exit code <c>130</c>.
    | Cancelled

module RunOutcome =
    let ofExitCode (exitCode: int) =
        match exitCode with
        | ExitCode.Success -> RunOutcome.Succeeded
        | ExitCode.UsageError -> RunOutcome.UsageError
        | ExitCode.Cancelled -> RunOutcome.Cancelled
        | _ -> RunOutcome.Failed

/// <summary>A pipeline run by an invocation, as its scopes reported themselves.</summary>
type PipelineRun = {
    Name: string
    /// The scopes recorded at pipeline level, in the order they finished, each carrying its nested scopes.
    Reports: ScopeReport list
    /// Every stage the run finished, in pre-order.
    Timings: StageTiming list
}

/// <summary>The structured result of one command invocation.</summary>
/// <remarks>
/// <c>Pipelines</c> holds the pipelines the invocation started, in the order they ran, including one that failed
/// or was cancelled. It is empty for an invocation that ran nothing: help, a version, <c>--explain</c>, or a
/// usage error.
/// </remarks>
type RunResult = {
    ExitCode: int
    Outcome: RunOutcome
    Pipelines: PipelineRun list
} with

    /// The pipeline-level reports of every pipeline run, in run order.
    member this.Reports = this.Pipelines |> List.collect _.Reports

    /// The stage timings of every pipeline run, in run order.
    member this.Timings = this.Pipelines |> List.collect _.Timings

    /// Every failure recorded by the run, absorbed failures among them, in pre-order.
    member this.Failures = this.Reports |> List.collect ScopeReport.failures
