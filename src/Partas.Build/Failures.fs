namespace Partas.Build

/// <summary>Where a scope sits in the run.</summary>
/// <remarks>
/// <c>Path</c> holds the position of each scope from the stage of the pipeline inward, ending in the position of
/// the scope addressed; <c>Names</c> holds the names of the same scopes. Two sibling scopes sharing a name
/// differ in the last position.
/// </remarks>
[<Struct>]
type ScopeAddress = {
    Path: int list
    Names: string list
}

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ScopeAddress =
    /// The address of the pipeline itself, which encloses every scope of the run.
    let root = { Path = []; Names = [] }

    /// <summary>The address of the scope at <paramref name="ordinal"/> of the scope <paramref name="address"/>
    /// addresses.</summary>
    /// <param name="ordinal" />
    /// <param name="name" />
    /// <param name="address" />
    let child (ordinal: int) (name: string) (address: ScopeAddress) =
        { Path = address.Path @ [ ordinal ]; Names = address.Names @ [ name ] }

    /// The names from the outermost scope inward, separated by '/'.
    let text (address: ScopeAddress) = String.concat "/" address.Names

/// <summary>A failure one step produced, with the step it came from.</summary>
[<Struct>]
type StepFailure = {
    /// The step's position among the declared steps of its scope, counting from zero.
    Index: int
    /// The label the step was declared with.
    Label: string voption
    Cause: FailureCause
}

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module StepFailure =
    /// The <c>Index</c> of a cause the scope left itself, which no step of it produced.
    [<Literal>]
    let NoStep = -1

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
    /// Where the scope sits in the run.
    Address: ScopeAddress
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
/// finishes and carries the scopes nested under it in its own report, and the pipeline appends itself last
/// where its own handlers left a cause.
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

    /// <summary>The scopes recorded at pipeline level, in the order they finished.</summary>
    /// <remarks>The pipeline's own stages, and the pipeline itself where its handlers left a cause.</remarks>
    let stages (reports: ScopeReports) = lock reports.recorded (fun () -> List.ofSeq reports.recorded)

    /// Every scope of the run, each nested scope under the one containing it.
    let all (reports: ScopeReports) = stages reports |> List.collect ScopeReport.flatten

    /// Every failure of the run, the absorbed ones among them, in pre-order.
    let failures (reports: ScopeReports) = stages reports |> List.collect ScopeReport.failures

    /// The failures that reached the pipeline, in pre-order.
    let propagated (reports: ScopeReports) = stages reports |> List.collect ScopeReport.propagated

/// <summary>What one failed execution of a scope hands the handlers registered on it.</summary>
/// <remarks>
/// The scope's own result, as its report records it, alongside the producer values the invocation holds. A
/// handler reads why the scope failed from <c>Primary</c> and <c>Secondary</c> rather than from the text the
/// run printed.
/// </remarks>
type FailureContext = {
    /// The scope's name.
    Scope: string
    /// Where the scope sits in the run.
    Address: ScopeAddress
    /// What the scope did.
    Outcome: StageOutcome
    /// The causes this execution of the scope recorded, the primary first.
    Failures: StepFailure list
    /// The exceptions offered to the scope containing this one.
    Exceptions: exn list
    /// The reports of the scopes this one ran, in the order they finished.
    Nested: ScopeReport list
    /// The values the invocation has published and still holds.
    Published: ProducerValues
}
with
    /// The failure the scope reports as its result.
    member this.Primary = this.Failures |> List.tryHead |> ValueOption.ofOption
    /// The causes of the same execution beyond the primary, in the order they were recorded.
    member this.Secondary = match this.Failures with [] -> [] | _ :: rest -> rest

/// Runs when the scope it is registered on fails.
type FailureHandler = FailureContext -> unit

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FailureContext =
    /// The <c>StepFailure.Label</c> of a cause a handler itself left.
    [<Literal>]
    let HandlerLabel = "onFailure"

    /// <summary>The context the handlers of the scope <paramref name="report"/> covers receive.</summary>
    /// <param name="published" />
    /// <param name="report" />
    let ofReport (published: ProducerValues) (report: ScopeReport) = {
        Scope = report.Name
        Address = report.Address
        Outcome = report.Outcome
        Failures = report.Failures
        Exceptions = report.Exceptions
        Nested = report.Nested
        Published = published
    }

    /// <summary>Runs <paramref name="handlers"/> in registration order, answering the causes they themselves
    /// left.</summary>
    /// <remarks>
    /// An exception out of a handler is one more cause of the same scope, recorded after the scope's own and
    /// leaving the primary where it was. The handlers registered after it still run, and each handler is
    /// entered at most once.
    /// </remarks>
    /// <param name="handlers" />
    /// <param name="context" />
    let runHandlers (handlers: FailureHandler list) (context: FailureContext) =
        let raised = ResizeArray<StepFailure>()

        for handler in handlers do
            try handler context
            with error -> raised.Add { Index = StepFailure.NoStep; Label = ValueSome HandlerLabel; Cause = FailureCause.Raised error }

        List.ofSeq raised

    /// <summary>The report a scope leaves for <paramref name="raised"/>, the causes its own handlers
    /// produced.</summary>
    /// <remarks>
    /// <c>Propagates</c> is false: a handler's failure is evidence beside the scope's own cause, and what
    /// reaches the scope containing it stays the cause it failed with. A stage records these on the report its
    /// steps already fill; this is how a pipeline, whose report no step fills, records the same thing.
    /// </remarks>
    /// <param name="context" />
    /// <param name="raised" />
    let handlerReport (context: FailureContext) (raised: StepFailure list) : ScopeReport = {
        Name = context.Scope
        Address = context.Address
        Outcome = context.Outcome
        Propagates = false
        Failures = raised
        Exceptions = []
        Nested = []
    }
