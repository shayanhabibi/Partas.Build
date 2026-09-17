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

### Motivating use case

The Fable.Electron CI migration (`../Fable.Electron/ci/`) from FAKE. Its pipeline today performs command work inside
`InputSpec.Read` because nothing else can carry a typed value between stages. The acceptance fixture in T7
reproduces its shape with local deterministic commands:

1. Resolve a release: run `gh release list --json ...`, parse JSON, select by the `--release` CLI option.
2. Download the API file for the selected release to a temporary path.
3. Generate bindings from the downloaded file.
4. Publish the generated changes, needing both the release and the generated result.
5. Dependency install with recovery: `npm ci`, and on unacceptable exit `npm install` then `npm ci` again.
6. On failure of the generate/publish scope, open an error PR carrying the release metadata if it was resolved.

## Current implementation facts

- `InputSpec<'T>` separates discoverable `Inputs` from `Read: ParseResult -> 'T`.
- `input` is applicative; absence of `Bind` prevents value-dependent CLI input discovery.
- `StageBuilder` has no `Bind` either: `PLAN.md` findings 1, 2 and 4 record that a monadic `let!` inside `stage`
  re-parents the stage and breaks input discovery. Consumer syntax must not reintroduce it.
- `InputSpec.map`, `map2`, and `sequence` do not memoize computed values.
- `Builders/Command.fs` materializes each pipeline with `Read` before running it.
- `--explain` materializes pipelines too; existing condition evaluation can itself perform effects.
- `src/Partas.Build.Cmd/Program.fs` owns `Cmd`, argument construction, start-info construction, and a minimal capturing runner.
- `src/Partas.Build/Process.fs` owns a separate stage-aware process runner.
- Existing process capture is line-oriented, drops empty lines, and can include stage prefixes.
- `OutputCapture.Errors` identifies stderr lines, not execution failures.
- `runAfterEachStage` receives a context without the completed outcome; timing is recorded after that hook.
- `post` is not guaranteed cleanup: cancellation and escaping exceptions can bypass it.
- `StageContext.run` returns `ContinueStageOnFailure || isSuccess`, but records the timing outcome from `isSuccess`
  alone. The raw outcome is therefore already tracked for timings and lost only on the return path.
- A suppressed step exception is dropped (`Types.fs`, the `if not stage.ContinueStageOnFailure then exns.Add` guard),
  so no evidence survives suppression.
- `StageContext` is a record with structural equality, and the runner copies every nested stage
  (`{ subStage with ParentContext = ... }`) before running it. Reference identity does not survive execution.
- `Step` is a `[<RequireQualifiedAccess>]` union in `Partas.Build.Internal` with two cases, `StepFn` and
  `StepOfStage`; `StepFn` returns `Async<Result<unit, string>>` and every `SRTPStageBuilderRunner.unifyResult`
  overload targets that shape.

### Pre-existing defects this work touches

Recorded here so T0 can separate them from implementation failures. Each is fixed by the task named.

- `netstandard2.0` argument transport is broken, not limited: `Cmd.toStartInfo` joins arguments with spaces and no
  quoting, so an argument containing whitespace is re-split by the child. Fixed in T2.
- A command cancelled through the caller-supplied token returns `Ok()` from `CmdRunner.run` ("a cancelled command
  succeeds"). The new operations must not inherit this. Decided in C02; legacy `run` keeps its behaviour.
- `PipelineContext.runStagesWithFailFast` discards the exception list `StageContext.run` returns and always yields
  an empty `stageExns`, so `PipelineFailedException` never carries a cause. Fixed in T6.
- A stage timeout is reported as a cancellation on the stage's own token and as a failure to its parent. F02
  classifies it.

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
- Accepted cost: an SRTP member shows `^State` in completion and reports a constraint failure instead of an
  overload mismatch when misused. T1 records the actual diagnostic text for a misuse so T9 can document it.
- The reduced probe proved compilation over stand-in types only. Behaviour against the real overload set, XML
  documentation, and IntelliSense presentation is T1's to establish, not this contract's.

### C01 — One process executor, separate stage policy

- Share process lifecycle implementation between standalone commands and stage execution.
- Low-level executor owns argument transport, process start, output draining, completion, and cancellation.
- Stage adapter owns inherited working directory/environment, acceptable exit codes, labels, routing, and stage diagnostics.
- Preserve command argument boundaries and existing secret masking in rendered commands.
- Never infer success from stdout/stderr content.
- Process-start failure, unacceptable exit, parsing failure, and cancellation remain distinguishable.
- Register cancellation with the process lifecycle; preserve existing process-tree termination guarantees on supported targets.
- `netstandard2.0` transports arguments with the same boundaries as the other targets: the executor quotes each
  argument into `ProcessStartInfo.Arguments` using the MSVCRT rules `ArgumentList` applies on modern targets.
  Only tree kill (`Process.Kill(true)`) and `WaitForExitAsync` stay target-specific, and those limitations are
  documented.

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
- Cancellation of a new operation, from any token, surfaces as cancellation: never as `Ok`, never as a
  `CommandResult`. The legacy `run` step keeps returning `Ok()` for a caller-token cancellation; changing it is
  out of scope and its current behaviour gets a pinning test in T2.
- `Operation<'T>` exists so that typed results do not multiply the `run` overload family
  (`SRTPStageBuilderRunner.unifyResult`, twelve overloads over `Result<unit, string>`) once per result type. It is
  a reader over the executing stage's runtime context returning `Async<'T>`; it adds no scheduling of its own.

### D01 — Static dependencies, runtime sequencing

- Keep dependency declaration separate from the runtime execution callback.
- Before running producers, inspect dependencies without invoking any runtime continuation.
- CLI values may configure a materialized plan; producer results may not discover additional stage dependencies dynamically.
- Runtime `Operation.Bind` sequences local operations; it does not discover stage edges.
- Validate cycles, ordering conflicts, and unsupported placements before producer effects begin.
- `--explain` shows declared dependencies and invokes no producer callback. It still evaluates every stage's
  `IsActive`, so a producer guarded by `whenStage` or `whenBranch` has that condition run during `--explain`,
  exactly as any stage does today.
- Preserve existing condition behavior separately; this work does not make all legacy conditions effect-free.
- No implicit conversion from a producer handle into an operation that secretly schedules work inside arbitrary `Bind`.

### D02 — Producer identity and scheduling

- A producer handle describes a typed producer; constructing it does not execute it.
- Identity is an allocated key (`ProducerId`, a fresh `Guid` or interlocked counter) taken when the handle is
  constructed and carried by every copy of the handle and of the stage that wraps it. Neither structural
  equality nor reference equality of a `StageContext` participates: the runner copies stages.
- Reusing the same handle shares one successful result per applicable invocation/attempt scope.
- Separately constructed handles remain distinct even when names and command arguments match.
- Sharing is not a global cache; repeated pipeline invocations start with fresh execution state.
- Depending on a producer schedules it; listing the same handle explicitly must not cause an extra execution.
- Explicitly placed producers retain their placement and ordering.
- Reject a consumer that precedes its explicitly placed required producer; do not move the producer across intervening work.
- Run an unlisted producer immediately before its first consumer, after resolving that producer's own declared prerequisites.
- "First consumer" is the first in declaration order within a sequential scope. Implicit placement inside a
  scope that is `parallel'` or `shuffleExecuteSequence`, at any nesting depth, is rejected at validation with the
  producer and scope names; the author lists the producer explicitly before the parallel scope instead.
- An implicitly placed producer is owned by the scope containing its first consumer.
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

- A scope is a stage or a pipeline. `onFailure` registers on both builders; the pipeline-level handler runs after
  every pipeline-level stage handler and observes the pipeline's final failure. A whole-invocation handler
  covering `InputSpec.Read` and parsing is deferred.
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
- Classification of a timeout: a scope's own `timeout`/`timeoutForStep` expiring is a failure of that scope
  (`FailureCause.TimedOut`), and its `onFailure` runs. Cancellation propagated from an ancestor scope, the
  pipeline timeout, or the invocation token is a cancellation and no handler runs. The distinction is made from
  which token fired, not from the exception type.
- Structured evidence travels in a new `Step` case, not through `StepFn`. `Step.Operation of label * (RuntimeContext
  -> Async<StepOutcome>)` is added beside `StepFn` and `StepOfStage`; `StepFn` and every `unifyResult` overload
  keep their `Result<unit, string>` shape. The runner renders a `StepOutcome` failure to the legacy string for
  printing and annotations. Adding a union case to `Partas.Build.Internal.Step` breaks any external exhaustive
  match on it; that is accepted, the type is internal by convention.
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
  - Reaches the runner through the new `Step.Operation` case (F02).
- Producer definitions:
  - `Producer<'T>` has an allocated `ProducerId`, settings, discoverable inputs, declared dependencies, and deferred execution.
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

### Consumer side

Both forms below are applicative: dependencies are declared as a `DependencySpec`, and the callback receives the
resolved values. There is no `let! x = needs p` inside `stage`; that would need the `StageBuilder.Bind` that
`PLAN.md` removed, and it would hide the dependency from `--explain` and validation.

```fsharp
// A plain stage that consumes producers and produces nothing.
let publish =
    Stage.consuming "publish"
        (DependencySpec.zip release generate)
        (fun (release, changes) -> operation {
            do! execute (publishCommand release changes)
        })
// : StageContext — placed in a pipeline like any stage.

// A consumer that declares a CLI input as well: the input and the dependencies are both applicative,
// so the result is InputSpec<StageContext>, never InputSpec<InputSpec<_>>.
let publishTo =
    Stage.consumingWith "publish"
        (InputSpec.ofInput Options.target)
        (DependencySpec.require release)
        (fun target release -> operation { do! execute (publishCommand target release) })
// : InputSpec<StageContext>

// A producer is a consumer that returns a value; `Producer.define` above is the same shape plus a result.
```

CE sugar, if T1 proves it, layers `needs` and `execute` as custom operations that change the state's dependency
type:

```fsharp
let download = produces "download" {
    needs release                      // state now carries Release
    execute (fun release -> operation { ... })
}
stage "publish" {
    needs release
    needs generate                     // state now carries Release * GeneratedChanges
    execute (fun (release, changes) -> operation { ... })
}
```

`needs` is representation-changing (it changes the value type the state carries), so it is an explicit
`Yield`/`Combine`-level member and not a `mapStage` setting. Whether F# infers the tuple through two `needs`
without annotation is a T1 question; the functional form is the fallback the fixture uses.

- Prefer a compiler-proven functional definition interface before adding CE sugar.
- Keep existing `input { return stage { ... } }` valid and typed as `InputSpec<StageContext>`.
- Specify the exact producer integration with `Yield`/`Combine` only after checking real compiler inference.

## Evidence and limits

- Verified in a reduced throwaway F# probe over stand-in types (`StageContext = { Retry; Names }`, a two-field
  `InputSpec`), not the library's:
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

## Appendix — design questions and agreed answers

The decisions above came out of a question-by-question review on 2026-09-17. Each question, its answer, and the
contract that carries it:

| Q | Question | Agreed answer | Contract |
|---|---|---|---|
| 1 | What must captured command output mean? | Raw command-local stdout/stderr, empty lines kept, no prefixes. | C02 |
| 2 | Where does recovery from a failed command happen? | Inside the executing operation, by branching on an attempted result. | C02 |
| 3 | Does an always-run failure stage cover input resolution? | No. Whole-invocation handling is deferred; effects move out of `Read`. | F01, deferred |
| 4 | Must produced values cross stage boundaries? | Yes: typed producers with explicit dependencies. | D01–D03 |
| 5 | Must plain `stage` keep returning `StageContext`? | Yes; share settings through SRTP mapping instead of one representation. | B01–B02 |
| 6 | Does depending on a producer schedule it? | Yes; listing it as well runs it once. | D02 |
| 7 | What identifies a shared producer? | The handle's allocated id; separate constructions are distinct. | D02 |
| 8 | What happens when a required producer is skipped? | Consumers skip with a dependency reason; use `Option` for expected absence. | D03 |
| 9 | Are dependencies discoverable before execution? | Yes; static declaration only, runtime `Bind` sequences local work. | D01 |
| 10 | Are existing order guarantees kept? | Yes; no automatic parallelism, users nest stages for order. | D02–D03 |
| 11 | Does retrying a consumer rerun its dependencies? | No; successful dependencies outside the retried scope are reused. | R01 |
| 12 | Does `onFailure` see failed attempts? | No; once, after retries are exhausted. | F01 |
| 13 | Does successful reporting clear the failure? | No; handler failure is secondary evidence. | F01 |
| 14 | How does a handler become active? | Structurally, by the scope it is registered on. | F01 |
| 15 | Does cancellation trigger failure reporting? | No; cancellation is distinct, `finally` is deferred. | F02 |
| 16 | What does an enclosing retry do to owned producers? | Fresh results per attempt; external dependencies reused. | R01 |
| 17 | Does suppression give a producer a result? | No; independent work continues, consumers stay blocked. | D03 |
| 18 | Can a handler read outputs that may not exist? | Yes, `TryGetOutput`, without scheduling anything. | F02 |
| 19 | Does the first release include `finally`? | No; typed results, dependencies and `onFailure` first. | deferred |
| 20 | How much output do failures retain? | Only explicitly captured output, plus command identity and exit code. | C02, F02 |
| 21 | What if a dependency conflicts with declaration order? | Reject forward references to placed producers; unlisted ones run before their first consumer. | D02 |
| 22 | Does capture fail on an unacceptable exit? | `executeCapture` does; `attemptCapture` returns the result. | C02 |
| 23 | Do inner and outer handlers both run? | Yes, inner first, once per failed scope execution. | F01 |
| 24 | Are dependencies declared alongside runtime `let!`? | No; declaration is applicative and separate from the callback. | D01, consumer side |
