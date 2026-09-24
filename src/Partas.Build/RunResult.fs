namespace Partas.Build

open System
open System.Text.Json

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

    /// A stage failed, or the invocation raised an exception.
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
/// <example>
/// <code lang="fsharp">
/// let result = Command.invoke [ "test" ] root
///
/// for timing in result.Timings do
///     printfn "%s%s %.0fms" (String.replicate timing.Depth "  ") timing.Name timing.Elapsed.TotalMilliseconds
///
/// for failure in result.Failures do
///     printfn "step %d: %s" failure.Index (FailureCause.describe failure.Cause)
/// </code>
/// </example>
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

module RunResult =
    let private outcomeName (outcome: RunOutcome) =
        match outcome with
        | RunOutcome.Succeeded -> "succeeded"
        | RunOutcome.Failed -> "failed"
        | RunOutcome.UsageError -> "usageError"
        | RunOutcome.Cancelled -> "cancelled"

    let private writeStageOutcome (writer: Utf8JsonWriter) (outcome: StageOutcome) =
        match outcome with
        | StageOutcome.Succeeded ->
            writer.WriteString("outcome", "succeeded")
            writer.WriteNull "error"
        | StageOutcome.Skipped ->
            writer.WriteString("outcome", "skipped")
            writer.WriteNull "error"
        | StageOutcome.Failed error ->
            writer.WriteString("outcome", "failed")
            writer.WriteString("error", error)

    let private writeCause (writer: Utf8JsonWriter) (cause: FailureCause) =
        writer.WriteStartObject "cause"

        match cause with
        | FailureCause.Command(command, exitCode, _) ->
            writer.WriteString("kind", "command")
            writer.WriteString("command", command)
            writer.WriteNumber("exitCode", exitCode)
        | FailureCause.Start(executable, error) ->
            writer.WriteString("kind", "start")
            writer.WriteString("executable", executable)
            writer.WriteString("exceptionType", error.GetType().FullName)
        | FailureCause.Raised error ->
            writer.WriteString("kind", "raised")
            writer.WriteString("exceptionType", error.GetType().FullName)
        | FailureCause.TimedOut -> writer.WriteString("kind", "timedOut")
        | FailureCause.Reported _ -> writer.WriteString("kind", "reported")

        writer.WriteString("message", FailureCause.describe cause)
        writer.WriteEndObject()

    let rec private writeReport (writer: Utf8JsonWriter) (report: ScopeReport) =
        writer.WriteStartObject()
        writer.WriteString("name", report.Name)
        writer.WriteString("address", ScopeAddress.text report.Address)

        writer.WriteStartArray "path"
        for ordinal in report.Address.Path do
            writer.WriteNumberValue ordinal
        writer.WriteEndArray()

        writeStageOutcome writer report.Outcome
        writer.WriteBoolean("propagates", report.Propagates)

        writer.WriteStartArray "failures"
        for failure in report.Failures do
            writer.WriteStartObject()

            if failure.Index = StepFailure.NoStep then writer.WriteNull "step"
            else writer.WriteNumber("step", failure.Index)

            MachineOutput.writeOptionalString writer "label" failure.Label
            writeCause writer failure.Cause
            writer.WriteEndObject()
        writer.WriteEndArray()

        writer.WriteStartArray "nested"
        for nested in report.Nested do
            writeReport writer nested
        writer.WriteEndArray()

        writer.WriteEndObject()

    let private writeTiming (writer: Utf8JsonWriter) (timing: StageTiming) =
        writer.WriteStartObject()
        writer.WriteString("name", timing.Name)
        writer.WriteNumber("depth", timing.Depth)
        writer.WriteNumber("elapsedMs", Math.Round(timing.Elapsed.TotalMilliseconds, 3))
        writeStageOutcome writer timing.Outcome
        writer.WriteEndObject()

    /// <summary><paramref name="result"/> as a JSON document, one line long unless <paramref name="indented"/>.</summary>
    /// <remarks>
    /// Each pipeline carries its reports, nested as the scopes nest, and its timings in pre-order. A failure's
    /// <c>step</c> counts from zero and is <c>null</c> for a cause no step produced. A command's line is its log
    /// form, with every secret masked.
    /// </remarks>
    /// <example>
    /// A failed run, as <c>--json</c> prints it (indented here):
    /// <code lang="json">
    /// {"formatVersion": 1, "exitCode": 1, "outcome": "failed",
    ///  "pipelines": [{"name": "test",
    ///    "reports": [{"name": "unit", "address": "unit", "path": [0], "outcome": "failed",
    ///                 "error": "Exit code not acceptable.", "propagates": true,
    ///                 "failures": [{"step": 0, "label": "dotnet test",
    ///                               "cause": {"kind": "reported", "message": "Exit code not acceptable."}}],
    ///                 "nested": []}],
    ///    "timings": [{"name": "unit", "depth": 0, "elapsedMs": 90.7, "outcome": "failed",
    ///                 "error": "Exit code not acceptable."}]}]}
    /// </code>
    /// </example>
    let toJson (indented: bool) (result: RunResult) =
        MachineOutput.document indented (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", MachineOutput.FormatVersion)
            writer.WriteNumber("exitCode", result.ExitCode)
            writer.WriteString("outcome", outcomeName result.Outcome)

            writer.WriteStartArray "pipelines"
            for pipeline in result.Pipelines do
                writer.WriteStartObject()
                writer.WriteString("name", pipeline.Name)

                writer.WriteStartArray "reports"
                for report in pipeline.Reports do
                    writeReport writer report
                writer.WriteEndArray()

                writer.WriteStartArray "timings"
                for timing in pipeline.Timings do
                    writeTiming writer timing
                writer.WriteEndArray()

                writer.WriteEndObject()
            writer.WriteEndArray()

            writer.WriteEndObject())
