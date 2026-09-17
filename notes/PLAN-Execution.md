# Typed execution, producer dependencies, and failure handling

- Status: behavioral decisions agreed; implementation not started.
- Date: 2026-09-17.
- Implementation checklist: [PLAN-Execution-Tasks.md](PLAN-Execution-Tasks.md).
- Input-model prerequisite: [PLAN.md](PLAN.md), especially verified findings and the prescribed composition workaround.
- Document roles:
  - This file owns behavioral requirements and design constraints.
  - The task checklist owns sequencing, file changes, tests, and completion criteria.
  - Proposed type names and CE syntax are implementation candidates; compiler evidence must establish them before rollout.

## Objective

- Consume command output during stage execution without moving effects into `InputSpec.Read`.
- Pass typed results between explicitly declared runtime producers and consumers.
- Execute scoped failure handlers using structured outcomes, independent of output routing.
- Reduce builder boilerplate while preserving plain/input-aware type distinctions.

## Current implementation facts

- `InputSpec<'T>` separates discoverable `Inputs` from `Read: ParseResult -> 'T`.
- `input` is applicative; absence of `Bind` prevents value-dependent CLI input discovery.
- `InputSpec.map`, `map2`, and `sequence` do not memoize computed values.
- `Builders/Command.fs` materializes each pipeline with `Read` before running it.
- `--explain` materializes pipelines too; existing condition evaluation can itself perform effects.
- `src/Partas.Build.Cmd/Program.fs` owns `Cmd`, argument construction, start-info construction, and a minimal capturing runner.
- `src/Partas.Build/Process.fs` owns a separate stage-aware process runner.
- Existing process capture is line-oriented, drops empty lines, and can include stage prefixes.
- `OutputCapture.Errors` identifies stderr lines, not execution failures.
- `runAfterEachStage` receives a context without the completed outcome; timing is recorded after that hook.
- `post` is not guaranteed cleanup: cancellation and escaping exceptions can bypass it.
- Existing `continueStageOnFailure` conflates raw failure with permission for the parent to continue.

## Accepted contracts

### B01 — Preserve input discovery and public distinctions

- Preserve `StageContext` versus `InputSpec<StageContext>`.
- Preserve `BuildStage` versus `InputSpec<BuildStage>` during CE composition.
- Keep CLI input collection applicative and available before parsing.
- Do not add a flattening operation for arbitrary `InputSpec<InputSpec<'T>>`.
- Move new command, download, generation, and publication work into execution callbacks.
- Returning a definition from an input builder must not execute that definition.
- Producer dependencies must contribute their CLI inputs before parsing.
- Do not force all stages to return `InputSpec<_>` merely to simplify builder implementations.

### B02 — Share state-preserving builder operations

- Centralize two mappings:
  - `BuildStage`: compose a `StageContext -> StageContext` update.
  - `InputSpec<BuildStage>`: map the same composition over the specification.
- Use a member-level SRTP state parameter so each custom-operation invocation resolves its current representation.
- Share custom-operation implementations through an inherited settings builder.
- Keep representation-changing `Yield`, `Delay`, `Combine`, `For`, and `Run` behavior explicit.
- Retain meaningful argument overloads, e.g. timeout units and `TimeSpan`.
- Place supporting helpers in a non-auto-open implementation module/namespace.
- Permit public helpers needed by consuming assemblies; mark machinery with `EditorBrowsable(Never)`.
- Do not hide the user-facing custom operations from completion.
- Do not assume future `.fsi` files can hide helpers referenced by public inline code or constraints.
- Keep CE finalizers applying `Build*` function aliases non-inline where required to avoid Release-only FS1118.

### C01 — One process executor, separate stage policy

- Share process lifecycle implementation between standalone commands and stage execution.
- Low-level executor owns argument transport, process start, output draining, completion, and cancellation.
- Stage adapter owns inherited working directory/environment, acceptable exit codes, labels, routing, and stage diagnostics.
- Preserve command argument boundaries and existing secret masking in rendered commands.
- Never infer success from stdout/stderr content.
- Process-start failure, unacceptable exit, parsing failure, and cancellation remain distinguishable.
- Register cancellation with the process lifecycle; preserve existing process-tree termination guarantees on supported targets.
- Document target-specific limitations for `netstandard2.0` rather than silently claiming modern process APIs exist there.

### C02 — Checked capture and deliberate attempts

- `executeCapture: Cmd -> Operation<CommandResult>`:
  - Capture stdout and stderr separately.
  - Enforce inherited acceptable exit codes.
  - Fail the operation on an unacceptable exit, retaining the captured result as failure evidence.
- `attemptCapture: Cmd -> Operation<CommandResult>`:
  - Return every normally completed process result, including unacceptable exit codes.
  - Leave recovery/branching to the caller.
  - Preserve process-start errors and cancellation as distinct non-result outcomes.
- Ordinary `execute: Cmd -> Operation<unit>` streams using the configured stage output policy.
- Output capture is explicit; ordinary commands do not acquire hidden full-output buffers.
- Raw captured stdout/stderr are command-local and precede display prefixes or other formatting.
- Preserve empty lines and newline content in captured text; capture is text, not a binary-output interface.
- Document that raw captured output is application data and can contain secrets; do not automatically print successful captures.
- Parsing occurs inside the executing operation and participates in stage failure handling.
- `Operation.ofTaskFactory` accepts a factory; avoid starting eager tasks while constructing a definition.

### D01 — Static dependencies, runtime sequencing

- Keep dependency declaration separate from the runtime execution callback.
- Before running producers, inspect dependencies without invoking any runtime continuation.
- CLI values may configure a materialized plan; producer results may not discover additional stage dependencies dynamically.
- Runtime `Operation.Bind` sequences local operations; it does not discover stage edges.
- Validate cycles, ordering conflicts, and unsupported placements before producer effects begin.
- `--explain` shows declared dependencies without executing producers or their callbacks.
- Preserve existing condition behavior separately; this work does not make all legacy conditions effect-free.
- No implicit conversion from a producer handle into an operation that secretly schedules work inside arbitrary `Bind`.

### D02 — Producer identity and scheduling

- A producer handle describes a typed producer; constructing it does not execute it.
- Reusing the same handle shares one successful result per applicable invocation/attempt scope.
- Separately constructed handles remain distinct even when names and command arguments match.
- Sharing is not a global cache; repeated pipeline invocations start with fresh execution state.
- Depending on a producer schedules it; listing the same handle explicitly must not cause an extra execution.
- Explicitly placed producers retain their placement and ordering.
- Reject a consumer that precedes its explicitly placed required producer; do not move the producer across intervening work.
- Run an unlisted producer immediately before its first consumer, after resolving that producer's own declared prerequisites.
- Preserve existing declaration order and explicit parallelism; add no automatic parallel execution.
- Never publish an unfinished or failed attempt's value.

### D03 — Skips, suppression, and parallelism

- A skipped required producer causes consumers to be skipped with a dependency reason.
- A successful `Option<'T>`/`ValueOption<'T>` result models intentional absence that consumers can handle.
- Suppression permits independent work to continue; it does not create a missing producer result.
- Track actual producer outcome separately from parent-continuation policy.
- Required consumers of a failed, suppressed producer remain blocked/skipped.
- Short-term supported baseline: users establish execution order through sequential/nested stages.
- Parallel consumers of an already completed producer may reuse its value.
- Unsupported arrangements involving unresolved dependencies and parallel scopes must fail clearly, not deadlock or read invalid values.
- Broader concurrent scheduling/deduplication guarantees are deferred; document the exact supported subset.

### R01 — Retry ownership

- Retrying a consumer reuses successful dependencies outside the retried scope.
- Each attempt of an enclosing stage gets fresh results for producers owned by that scope.
- Failed attempts never leave a published value accessible to a subsequent attempt.
- Define and validate producer ownership before execution; scope membership cannot depend on completion order.
- Keep a producer's latest successful value available until its owning scope is reset or invocation ends.
- Authors remain responsible for safe repetition of external side effects.

### F01 — Scoped failure handlers

- `onFailure` observes the scope's actual final failure, after that scope exhausts retries.
- Run each registered handler once per failed scope execution, not once globally across enclosing retries.
- Invoke inner handlers before outer handlers.
- An inner handler may run during an outer attempt that later succeeds on retry.
- Successful reporting preserves the original failure.
- Handler failure is additional evidence; retain the original cause as primary.
- A handler failure does not recursively invoke that same handler.
- Express activation through nested scopes; omit mutable activate/deactivate operations initially.
- The handler receives structured scope/step identity and causes, not an output-text heuristic.
- A suppressed raw failure remains a failure for that scope's reporting; propagation policy remains separate.

### F02 — Failure evidence and cancellation

- `FailureContext.TryGetOutput producer` inspects only already-published, still-valid values.
- Lookup does not schedule producers or rerun dependencies.
- Preserve command identity and exit code for command failures.
- Attach captured stdout/stderr only where capture was requested.
- Preserve exceptions and parsing failures without reducing everything to a formatted string.
- Cancellation is distinct from failure and does not activate `onFailure` as an ordinary failed exit.
- Initial handlers remain subject to invocation cancellation; cancellation-safe cleanup requires the deferred cleanup feature.
- General `finally`, separate cleanup budgets, and termination guarantees are outside this implementation.

## Proposed module design

- `Partas.Build.Cmd`:
  - `CommandResult`: exit code plus separate captured stdout/stderr text.
  - Shared process executor; no dependency on `StageContext`.
  - Compatibility adapter for the existing `Cmd.run` entry point.
- Builder support:
  - `StageMap` dispatches state-preserving updates across both representations.
  - `StageSettingsBuilder` defines each setting once.
  - Broader stage/pipeline settings unification is optional follow-up, not a prerequisite.
- Runtime operations:
  - `Operation<'T>` represents deferred execution under a runtime context.
  - Owns sequencing, command adapters, and value transformation inside one executing scope.
  - Exposes no runtime-dependent stage discovery.
- Producer definitions:
  - `Producer<'T>` has stable identity, settings, discoverable inputs, declared dependencies, and deferred execution.
  - `DependencySpec<'T>` composes prerequisite handles applicatively without reading their values.
  - Public consumers receive typed values; any heterogeneous internal storage stays encapsulated and validates identity/type agreement.
- Invocation execution state:
  - Owns validated placement, producer ownership, attempt scopes, publication, and lookup.
  - Does not store run state on reusable definitions or in process-global caches.
- Failure records:
  - Preserve actual outcome, propagation decision, primary failure, secondary handler failures, and available command evidence.
  - Adapt existing timing/summary surfaces without forcing callers to inspect timing storage for control flow.

## Candidate composition interface

- This is pseudocode; Task T1 proves the concrete F# surface.
- Dependencies and inputs are declared outside the runtime callback.
- Plain and input-aware definitions must compose without nested `InputSpec` values.

```fsharp
let release =
    Producer.define "resolve release"
        (InputSpec.ofInput Options.release)
        DependencySpec.empty
        (fun requested () -> operation {
            let! result = executeCapture releaseListCommand
            return parseAndSelect requested result.Stdout
        })

let download =
    Producer.define "download"
        (InputSpec.ret ())
        (DependencySpec.require release)
        (fun () release -> operation {
            do! execute (downloadCommand release)
            return destinationFor release
        })
```

- Prefer a compiler-proven functional definition interface before adding CE sugar.
- Keep existing `input { return stage { ... } }` valid and typed as `InputSpec<StageContext>`.
- Specify the exact producer integration with `Yield`/`Combine` only after checking real compiler inference.

## Evidence and limits

- Verified in a reduced throwaway F# probe:
  - One inherited generic `retry` custom operation supports both state representations.
  - Settings compile before and after yielding an input-aware child.
  - Inferred results remain `StageContext` or `InputSpec<StageContext>` as appropriate.
  - Input reads stay deferred.
  - Separate Release consumer assembly compiles and executes the probe.
- Not yet verified:
  - Interaction with the complete Partas.Build overload set and XML documentation.
  - Producer registration, ownership, retry reset, or dependency diagnostics.
  - New operation/dependency CE syntax.
- The original probe is temporary and outside the repository; T1 must create reproducible repository evidence.

## Deferred work

- General `finally` and bounded cancellation-independent cleanup.
- Dynamic producer dependencies based on runtime values.
- Automatic parallel scheduling and unresolved cross-parallel dependency support.
- Cross-invocation result caching and incremental builds.
- Implicit memoization of `InputSpec.Read`.
- Whole-invocation recovery for arbitrary effects left in legacy input resolution.
- Binary process output, bounded/spooled full capture, and automatic redaction of command-produced text.
- Universal abstraction across every stage/pipeline/command setting.
- Full Fable.Electron migration and externally publishing error PRs.

## Coverage index

- Q1–Q3: C01–C02, F01–F02.
- Q4: D01–D03.
- Q5 and builder discussion: B01–B02.
- Q6–Q10: D01–D03.
- Q11 and Q16: R01.
- Q12–Q15: F01–F02.
- Q17: D03.
- Q18–Q20: C02, F02, deferred work.
- Q21: D02.
- Q22: C02.
- Q23: F01.
- Q24: D01 and candidate composition interface.
