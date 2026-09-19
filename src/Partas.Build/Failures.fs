namespace Partas.Build

/// <summary>A failure one step produced, with the step it came from.</summary>
[<Struct>]
type StepFailure = {
    /// The step's position among the declared steps of its scope, counting from zero.
    Index: int
    /// The label the step was declared with.
    Label: string voption
    Cause: FailureCause
}

/// <summary>What one execution of a scope did, the evidence its steps left, and whether its failure reaches the
/// scope containing it.</summary>
/// <remarks>
/// <c>Outcome</c> is the scope's own result and <c>Propagates</c> the decision taken from it, held apart:
/// a failure a <c>continueStageOnFailure</c> absorbs reports <c>Failed</c> with <c>Propagates = false</c>.
/// <para><c>Failures</c> retains every cause of the reported execution, the absorbed ones among them, and
/// <c>Exceptions</c> holds what the containing scope received.</para>
/// </remarks>
type ScopeReport = {
    Name: string
    Outcome: StageOutcome
    /// Whether a failure of this scope fails the scope containing it.
    Propagates: bool
    Failures: StepFailure list
    /// The exceptions offered to the containing scope.
    Exceptions: exn list
    /// The reports of the scopes nested under this one, in the order they finished.
    Nested: ScopeReport list
}

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ScopeReport =
    /// Whether the reported execution failed, whatever the propagation decision taken from it.
    let failed (report: ScopeReport) =
        match report.Outcome with
        | StageOutcome.Failed _ -> true
        | _ -> false

    /// Whether the scope containing <paramref name="report"/> carries on past it.
    let continues (report: ScopeReport) = not report.Propagates

    /// <paramref name="report"/> and every report nested under it, in pre-order.
    let rec flatten (report: ScopeReport) = [
        report
        for nested in report.Nested do yield! flatten nested
    ]

    /// The failures of <paramref name="report"/> and of the scopes nested under it, in pre-order.
    let failures (report: ScopeReport) = flatten report |> List.collect _.Failures

    /// <summary>The failures that reached the scope containing <paramref name="report"/>, in pre-order.</summary>
    /// <remarks>A scope that absorbs its own failure is a boundary: the failures below it stay with it.</remarks>
    let rec propagated (report: ScopeReport) = [
        if report.Propagates then
            yield! report.Failures
            for nested in report.Nested do yield! propagated nested
    ]

/// <summary>The scopes one pipeline invocation ran, as they reported themselves.</summary>
/// <remarks>
/// Invocation-local: a run empties the collector before its first stage. A stage of the pipeline appends as it
/// finishes and carries the scopes nested under it in its own report.
/// </remarks>
[<ReferenceEquality>]
type ScopeReports = private {
    recorded: System.Collections.Generic.List<ScopeReport>
}

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ScopeReports =
    let create() = { recorded = ResizeArray() }

    /// Records <paramref name="report"/> after the scopes already recorded.
    let add (report: ScopeReport) (reports: ScopeReports) =
        lock reports.recorded (fun () -> reports.recorded.Add report)

    /// Discards every recorded scope. An invocation starts here.
    let clear (reports: ScopeReports) = lock reports.recorded (fun () -> reports.recorded.Clear())

    /// The pipeline's own stages, in the order they finished.
    let stages (reports: ScopeReports) = lock reports.recorded (fun () -> List.ofSeq reports.recorded)

    /// Every scope of the run, each nested scope under the one containing it.
    let all (reports: ScopeReports) = stages reports |> List.collect ScopeReport.flatten

    /// Every failure of the run, the absorbed ones among them, in pre-order.
    let failures (reports: ScopeReports) = stages reports |> List.collect ScopeReport.failures

    /// The failures that reached the pipeline, in pre-order.
    let propagated (reports: ScopeReports) = stages reports |> List.collect ScopeReport.propagated
