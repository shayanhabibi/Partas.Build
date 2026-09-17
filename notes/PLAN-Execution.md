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
- `src/Partas.Build.Cmd/Program.fs` owns `Cmd`, argument construction, and start-info construction. Process
  mechanics moved to `Execution.fs` in T2; `Cmd.run` and `Process.fs` are adapters over it.
- `src/Partas.Build/Process.fs` owns the stage adapter: prefixes, routing, exit-code policy and stage diagnostics.
- Stage output capture is line-oriented, drops empty lines, and can include stage prefixes. Raw capture arrived
  in T2 as a separate surface and keeps both.
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
  quoting, so an argument containing whitespace is re-split by the child. **Fixed in T2** by
  `ProcessExecutor.Arguments.quote`.
- A command cancelled through the caller-supplied token returns `Ok()` from `CmdRunner.run` ("a cancelled command
  succeeds"). The new operations must not inherit this. Decided in C02; legacy `run` keeps its behaviour, pinned
  by `CmdTests` "legacy run reports success when the caller's own token cancels the process".
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
- Accepted cost: an SRTP member shows `^State` in completion. T1 measured the misuse diagnostic: the compiler
  lists the three supported states as unmatched `StageMap.Map` overloads, recorded verbatim under
  *Compiled surface (T1)* for T9's docs.
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

- This was pseudocode; *Compiled surface* below records what T1 compiled, and takes precedence over it.
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

## Compiled surface (T1)

Compiled against the real library and exercised from a separate consumer assembly in Debug and Release.
Signatures are as the compiler reports them.

### Shared builder mapping

`src/Partas.Build/Builders/StageSettings.fs`, namespace `Partas.Build.Internal`:

```fsharp
type StageMap =
    static member Map: build: BuildStage * update: (StageContext -> StageContext) -> BuildStage
    static member Map: spec: InputSpec<BuildStage> * update: (StageContext -> StageContext) -> InputSpec<BuildStage>
    static member Map: spec: InputSpec<StageContext> * update: (StageContext -> StageContext) -> InputSpec<StageContext>
    static member inline Apply: state: ^State * update: (StageContext -> StageContext) -> ^State

module StageMap =                                                    // [<CompilationRepresentation(ModuleSuffix)>]
    val inline mapStage: update: (StageContext -> StageContext) -> state: ^State -> ^State

type StageSettingsBuilder =
    [<CustomOperation("retry")>] member inline retry: state: ^State * count: int -> ^State
```

- `StageBuilder` inherits `StageSettingsBuilder`, and the two `retry` members it used to declare are gone.
  Reflection over `StageBuilder` finds one `retry`: generic, declared by the base, carrying no `EditorBrowsable`.
- `retry` compiles before a yielded input-aware child, after one, and on both sides of one. The last setting
  wins, a negative count clamps in either representation, and declaring any of it reads no input.
- `StageMap` carries `EditorBrowsable(Never)`; `StageSettingsBuilder` carries `Advanced`, as `StageBuilder` does.
- Only `retry` moved to the base. The remaining mirrored pairs are T8's.

### Compile order

`Types.fs` → `Dependencies.fs` → `Process.fs` → `Builders/StageSettings.fs` → `Builders/Stage.fs`, the rest
unchanged. `Dependencies.fs` depends on `Types.fs` alone. `StageSettings.fs` precedes the builder inheriting it.

### Producers, dependencies, consumers

`src/Partas.Build/Dependencies.fs`, namespace `Partas.Build`:

```fsharp
type RuntimeContext = { Stage: StageContext; StepIndex: StepIndex }
type Operation<'T> = { Execute: RuntimeContext -> Async<'T> }
type ProducerId = ProducerId of id: int64
type ProducerRef = { Id: ProducerId; Name: string; Requires: ProducerRef list }

type ProducerValues =
    static member Empty: ProducerValues
    member Add: id: ProducerId * value: 'T -> ProducerValues
    member TryGet: id: ProducerId -> 'T voption

type Producer<'T> = {
    Id: ProducerId
    Name: string
    Inputs: ActionInput list
    Requires: ProducerRef list
    Prepare: ParseResult -> ProducerValues -> Result<Operation<'T>, string>
}

type DependencySpec<'T> = {
    Requires: ProducerRef list
    Inputs: ActionInput list
    Read: ProducerValues -> Result<'T, string>
}

module Operation =
    val ret: value: 'T -> Operation<'T>
    val ofAsync: work: Async<'T> -> Operation<'T>

module DependencySpec =
    val empty: DependencySpec<unit>
    val require: producer: Producer<'T> -> DependencySpec<'T>
    val map: fn: ('T -> 'U) -> spec: DependencySpec<'T> -> DependencySpec<'U>
    val map2: fn: ('T -> 'U -> 'V) -> first: DependencySpec<'T> -> second: DependencySpec<'U> -> DependencySpec<'V>
    val zip: first: Producer<'T> -> second: Producer<'U> -> DependencySpec<'T * 'U>

module Producer =
    val define:
        name: string -> inputs: InputSpec<'I> -> dependencies: DependencySpec<'D> -> execute: ('I -> 'D -> Operation<'T>)
            -> Producer<'T>

module Stage =
    val consuming: name: string -> dependencies: DependencySpec<'D> -> execute: ('D -> Operation<unit>) -> StageContext
    val consumingWith:
        name: string -> inputs: InputSpec<'I> -> dependencies: DependencySpec<'D> -> execute: ('I -> 'D -> Operation<unit>)
            -> InputSpec<StageContext>
```

Compiled example, both consumer forms, from `tests/Partas.Build.CompilerProbe/Probe.fs`:

```fsharp
let resolve =
    Producer.define "resolve" (InputSpec.ofInput release) DependencySpec.empty (fun requested () -> Operation.ret requested)

let generate =
    Producer.define "generate" (InputSpec.ret ()) (DependencySpec.require resolve) (fun () resolved ->
        Operation.ret (String.length resolved))

let both: DependencySpec<string * int> = DependencySpec.zip resolve generate

// `resolve` reads `--release`, so a consumer of it declares its inputs. A plain stage registers none, and
// `Stage.consuming` rejects prerequisites that read the command line.
let publish: InputSpec<StageContext> =
    Stage.consumingWith "publish" (InputSpec.ofInput target) both (fun destination (resolved, generated) ->
        Operation.ret (printfn "%s %s %d" destination resolved generated))

let count = Producer.define "count" (InputSpec.ret ()) DependencySpec.empty (fun () () -> Operation.ret 1)

let report: StageContext =
    Stage.consuming "report" (DependencySpec.require count) (fun counted -> Operation.ret (printfn "%d" counted))
```

- `Producer.define` allocates a `ProducerId` from an interlocked counter and harvests its own inputs together
  with its prerequisites'. Two declarations sharing a name and arguments take different identities.
- Declaring a producer, composing dependencies, and materializing a consumer invoke no callback. `Prepare` is
  applied by whoever schedules the producer, and the `Operation` it answers runs after that.
- `DependencySpec.map2` unions prerequisites by identity and inputs by reference, keeping declaration order, so
  a producer required twice is listed once.
- `ProducerValues` stores results as `obj` behind `TryGet`, which answers a value only when the identity and the
  result type agree.
- `DependencySpec.Read` and `Producer.Prepare` answer `Result<_, string>`: an unreadable prerequisite is an
  `Error` naming that producer, and an exception out of either comes from a function the caller supplied — a
  throwing `DependencySpec.map` reaches the caller rather than being reported as a missing prerequisite.
- `Stage.consuming` rejects prerequisites declaring CLI inputs with an `ArgumentException` naming
  `Stage.consumingWith`, since a plain `StageContext` registers a stage's own inputs alone.
- `retry` is the only setting accepting an `InputSpec<StageContext>` state until T8 moves the others: `retry`
  followed by `timeout` against the same value compiles the first and fails the second with `FS0001`.

### Diagnostics for T9's docs

An unsupported state makes the compiler list the supported ones. Both paths — the helper and the custom
operation — produce the same list, with `FS0001` for `StageMap.mapStage` and `FS0193` for `StageBuilder.retry`.
Verbatim from the `UnsupportedSettingState` fixture's build output:

```
UnsupportedSettingState.fs(9,5): error FS0001: No overloads match for method 'Map'.

Known return type: InputSpec<int>

Known type parameters: < InputSpec<int> , (StageContext -> StageContext) >

Available overloads:
 - static member StageMap.Map: build: BuildStage * update: (StageContext -> StageContext) -> BuildStage // Argument 'build' doesn't match
 - static member StageMap.Map: spec: InputSpec<BuildStage> * update: (StageContext -> StageContext) -> InputSpec<BuildStage> // Argument 'spec' doesn't match
 - static member StageMap.Map: spec: InputSpec<StageContext> * update: (StageContext -> StageContext) -> InputSpec<StageContext> // Argument 'spec' doesn't match
```

`InputSpec<InputSpec<_>>` stays unflattened: yielding an input-aware value inside a returned stage is, verbatim
from the `NestedInputSpec` fixture's build output,

```
NestedInputSpec.fs(10,16): error FS0193: Type constraint mismatch. The type
    'InputSpec<StageContext>'
is not compatible with type
    'StageContext'
```

### Rejected syntax

- `let! value = needs producer` inside `stage { }`: `FS0708`. Pinned by the `MonadicNeeds` fixture.
- `needs`/`execute` CE sugar over two producers: the state a custom operation threads is a left-nested tuple
  seeded by the builder's `Yield(unit)`, so two `needs` carry `(unit * Release) * Generated`. A flat
  `fun (release, changes) -> …` is `FS0001`, "This expression was expected to have type 'string' but is a tuple
  of type 'unit * string'", and the same body compiles as `fun (((), release), changes) -> …`. The sugar is
  **rejected**: `DependencySpec.zip`/`require` deliver the flat tuple the fixture wants, without a pattern that
  exposes the seed. Revisit only with a builder shape that avoids seeding the state with `unit`.

### Limits of this surface

- `Stage.consumingWith` declares its own inputs together with its prerequisites', so a command registers both.
  `Stage.consuming` answers a plain `StageContext`, which has nowhere to record the producers it requires, so it
  rejects prerequisites declaring CLI inputs rather than dropping them. The step's `--explain` label names the
  producers either way; the model field that makes their inputs harvestable from a plain stage is T4's, and the
  rejection lifts once it exists.
- `Producer.Prepare` compiles but no caller invokes it: scheduling is T5's. The producer execution shape is
  therefore type-checked and unexercised.
- Nothing publishes producer values yet. A consumer declaring prerequisites therefore fails when it runs, naming
  the producer whose result is unavailable; a consumer declaring none executes its operation. T5 replaces the
  `ProducerValues.Empty` the consumer step reads with the owning scope's published values.
- `RuntimeContext` carries the executing stage and the step index; cancellation reaches an operation as the
  ambient token. T3 settles it alongside `Step.Operation`.

## Implemented surface (T2)

### The shared executor

`src/Partas.Build.Cmd/Execution.fs`, namespace `Partas.Build`:

```fsharp
type CommandResult = { ExitCode: int; Stdout: string; Stderr: string }

[<RequireQualifiedAccess>]
type OutputPolicy =
    | Inherit
    | Lines of onStdout: (string -> unit) * onStderr: (string -> unit)

module ProcessExecutor =
    module Arguments =
        val quote: value: string -> string
        val transport: startInfo: ProcessStartInfo -> arguments: string seq -> unit

    val streamToExit: ProcessStartInfo -> OutputPolicy -> CancellationToken -> (unit -> unit) -> Task<int>
    val captureToExit: ProcessStartInfo -> CancellationToken -> (unit -> unit) -> Task<CommandResult>
    val stream: ProcessStartInfo -> OutputPolicy -> CancellationToken -> (unit -> unit) -> Task<int>
    val capture: ProcessStartInfo -> CancellationToken -> (unit -> unit) -> Task<CommandResult>
```

- The executor takes a `ProcessStartInfo` and nothing else: working directory, environment, exit-code policy,
  labels and routing stay with the caller. It modifies the start info only to add the redirection its policy
  requires.
- `stream`/`capture` raise `OperationCanceledException` when the supplied token cancelled the process, which is
  the C02 behaviour T3's operations need. `streamToExit`/`captureToExit` report the killed process's exit code
  instead, which is what the legacy stage adapter maps through `acceptExitCodes`.
- The final parameter runs once, before the kill, for a caller with something to report; `ignore` for one with
  nothing. The kill is issued from the token registration, never from a continuation, and kills the tree.
- `OutputPolicy.Lines` hands over every line including empty ones and retains none of them: streaming returns
  `Task<int>`, so there is nowhere for a hidden full-output buffer to live. Dropping an empty line is the
  caller's policy, and `CmdRunner` keeps dropping it.
- `Arguments.transport` uses `ArgumentList` on `net8.0`/`net10.0` and a `quote`d `Arguments` string on
  `netstandard2.0`. `quote` applies the MSVCRT rules: a value free of whitespace and quotes passes through, an
  empty value becomes `""`, and a backslash run is doubled only where a quote follows it.
- Target-specific and documented at the call site: `WaitForExitAsync` (a `Task.Run` over `WaitForExit` on
  `netstandard2.0`) and tree kill. The `netstandard2.0` build reaches `Process.Kill(bool)` by reflection, so it
  kills the tree on any host of .NET Core 3.0 or later and the child alone on an older one.

### Compile order

`Execution.fs` → `Program.fs` in `Partas.Build.Cmd`. `Execution.fs` depends on `ProcessStartInfo` rather than on
`Cmd`, which is what lets `Cmd.toStartInfo` and `Cmd.run` both call it without a file-order cycle.

### Adapters

- `Cmd.run` keeps its `struct {| exitCode; output; error |}` shape, splitting `ProcessExecutor.capture`'s raw text
  on newlines and dropping empty lines.
- `CmdRunner.run` keeps returning `Ok()` for a caller-token cancellation and `Error` for an unacceptable exit
  code, lifting a capture's failure text as before. It links the ambient token and the caller's into one for the
  executor and tells them apart afterwards, as it did with two registrations.

### The process fixture

`tests/Fixtures/ProcessFixture` is a C# console child taking its behaviour from its arguments: `text <exit>`
(blank lines on both streams, no trailing newline on stderr), `args <value>...` (one `<length>:<value>` line per
argument), `env <name>`, `flood <lines>` (that many lines on each stream, alternating, each flushed), and
`sleep <ms>`. Every byte goes through a UTF-8 writer with the newlines spelled out, so the expected text is the
same on every platform. It is referenced with `ReferenceOutputAssembly="false"` and run as
`dotnet <path-to-dll>`; the tests find it by walking up to `Partas.Build.slnx` and into the fixture's own `bin`.

## Implemented surface (T3)

### The step model

`src/Partas.Build/Types.fs`, namespace `Partas.Build.Internal`, declared before `Step` and inside its recursive
group:

```fsharp
[<RequireQualifiedAccess>]
type FailureCause =
    | Command of command: string * exitCode: int * captured: CommandResult voption
    | Start of executable: string * error: exn
    | Raised of error: exn
    | TimedOut
    | Reported of message: string

[<Struct; RequireQualifiedAccess>]
type StepOutcome =
    | Completed
    | Failed of cause: FailureCause

type OperationFailedException =
    inherit Exception
    new: cause: FailureCause -> OperationFailedException
    member Cause: FailureCause

module FailureCause =
    val describe: cause: FailureCause -> string

type [<Struct; RequireQualifiedAccess>] Step =
    | StepFn of label: string voption * fn: (StageContext -> StepIndex -> Async<Result<unit, string>>)
    | Operation of operationLabel: string voption * operation: (RuntimeContext -> Async<StepOutcome>)
    | StepOfStage of stage: StageContext

and [<Struct>] RuntimeContext = {
    Stage: StageContext
    StepIndex: StepIndex
    OwnTimeout: CancellationToken
}

module StageContext =
    val inline addOperation: label: string voption -> operation: (RuntimeContext -> Async<StepOutcome>) -> stage: StageContext -> StageContext
```

- `Step` is a struct union, so its case fields share one name space: the new case is spelled
  `operationLabel`/`operation` because `label`/`fn` are `StepFn`'s.
- `StepFn` and the twelve `SRTPStageBuilderRunner.unifyResult` overloads are untouched.
- `FailureCause.describe` is applied at one site, `StageContext.run`'s `Step.Operation` branch, which hands the
  string to the same `printError` the `StepFn` branch uses. A failed operation therefore fails its step and its
  stage exactly as a `StepFn` returning `Error` does, GitHub Actions annotation included.
- `RuntimeContext` moved from `Dependencies.fs` into `Types.fs`, and from namespace `Partas.Build` into
  `Partas.Build.Internal`: `Step.Operation`'s payload names it, and `Step` is declared in `Types.fs`. It gained
  `OwnTimeout`, which T1 left for T3 to settle.
- `--explain` renders an operation's label the way it renders a `StepFn`'s, and its index where there is none.

### Operations

`src/Partas.Build/Operations.fs`, namespace `Partas.Build`:

```fsharp
[<Struct>]
type Operation<'T> = { Execute: RuntimeContext -> Async<'T> }

module Operation =
    val ret: value: 'T -> Operation<'T>
    val ofAsync: work: Async<'T> -> Operation<'T>
    val ofTaskFactory: factory: (unit -> Task<'T>) -> Operation<'T>
    val map: fn: ('T -> 'U) -> operation: Operation<'T> -> Operation<'U>
    val bind: fn: ('T -> Operation<'U>) -> operation: Operation<'T> -> Operation<'U>
    val fail: cause: FailureCause -> 'T
    val toStepOutcome: operation: Operation<unit> -> context: RuntimeContext -> Async<StepOutcome>

[<AutoOpen>]
module Operations =
    val execute: command: Cmd -> Operation<unit>
    val executeCapture: command: Cmd -> Operation<CommandResult>
    val attemptCapture: command: Cmd -> Operation<CommandResult>
```

- `Operation<'T>` and the `Operation` module moved here from `Dependencies.fs`; `ret` and `ofAsync` keep the
  bodies T1 compiled. Two modules of the same name cannot merge across files, which is what moves them.
- All three adapters read the executing stage for their working directory, environment, acceptable exit codes
  and output routing, by walking `ParentContext` upward through the existing lookups.
- Only `ProcessExecutor.stream`/`capture` are used, never `streamToExit`/`captureToExit`: a cancelled command
  raises out of the executor, so a `CommandResult` is only ever a normally completed process.
- `execute` streams through `CmdRunner.outputPolicy`, so a step keeps its prefix, its silencing and its capture.
  An unacceptable exit code fails it with `FailureCause.Command(log string, code, ValueNone)` — streaming retains
  no text, so the failure carries none.
- `executeCapture` fails the same way with `ValueSome result`, and `FailureCause.describe` lifts the child's
  stderr, or its stdout where stderr is empty, onto a line of its own.
- `attemptCapture` answers every normally completed process, unacceptable exit codes included.
- A process that never started becomes `FailureCause.Start(executable, Win32Exception)` from all three, never a
  `CommandResult`.
- `Operation.toStepOutcome` classifies what escapes: `OperationFailedException` to its cause, cancellation
  through where `OwnTimeout` did not fire, `FailureCause.TimedOut` where it did, the pipeline and soft
  cancellation exceptions through untouched, and anything else — a parsing failure among them — to
  `FailureCause.Raised`, holding the exception itself. The aggregate an `Async.AwaitTask` wraps a task's
  exception in is removed first, and a propagated exception keeps its original stack trace.
- `Operation.ofTaskFactory` applies the factory inside the async it answers, so a definition holds no started
  task.

### Stage syntax

`Builders/Stage.fs` gains one custom operation and its `InputSpec` mirror:

```fsharp
[<CustomOperation>] member runOperation: build: BuildStage * operation: Operation<unit> * ?label: string -> BuildStage
[<CustomOperation>] member runOperation: spec: InputSpec<BuildStage> * operation: Operation<unit> * ?label: string -> InputSpec<BuildStage>
```

Written as `stage "release" { runOperation work "release data" }`. The name is deliberately outside the `run`
family: `run`'s catch-all SRTP overload resolves against the shape of its argument, and an extra overload there
changes what infers. `Stage.consuming`/`consumingWith` now build a `Step.Operation` too, and an unpublished
prerequisite reaches the step as `FailureCause.Reported`.

### Compile order

`Types.fs` → `Process.fs` → `Operations.fs` → `Dependencies.fs` → `Builders/StageSettings.fs` →
`Builders/Stage.fs`, the rest unchanged. `Dependencies.fs` moved after `Process.fs` because its consumer step
now runs an `Operation`, and `Operations.fs` needs `Cmd` and `CmdRunner`.

`Process.fs` exposes what both command paths share: `CmdRunner.stepPrefix`, `CmdRunner.logCommand`,
`CmdRunner.outputPolicy` and `CmdRunner.announceKill`. `CmdRunner.run` is those four plus the code it already
had, and its behaviour is unchanged.

## Evidence and limits

- Verified against the real library by T1, and reproducible from the repository:
  - `tests/Partas.Build.CompilerProbe` (in the solution, Debug and Release): one inherited generic `retry` over
    both state representations, settings on either side of an input-aware child, inferred results fixed by
    functions taking exactly `StageContext` or `InputSpec<StageContext>`, deferred input reads, `mapStage` over
    all three supported states, and producer/consumer declaration without a callback.
  - `tests/Partas.Build.CompilerProbe.Negative/<Case>` (in no solution, driven by
    `tests/Partas.Build.Tests/CompilerTests.fs`): `NestedInputSpec`, `MonadicNeeds`, `UnsupportedSettingState`.
  - `tests/Partas.Build.Tests/CompositionTests.fs`: the same composition and declaration properties inside the
    library's own suite, including the zero-read assertions and the single-`retry` reflection check.
  - Interaction with the complete overload set: `retry` is the only setting moved so far, and the `Build` CLI,
    which is written against the library, compiles unchanged.
- Verified against the real library by T2, and reproducible from the repository:
  - `tests/Partas.Build.Tests/ExecutionTests.fs`: raw capture of blank lines and a newline-free tail, 5000
    simultaneous lines on each stream draining completely, awkward arguments delivered intact, `quote` against
    the MSVCRT rules, explicit working directory and environment, a start failure raising `Win32Exception`,
    cancellation raising from both `capture` and `stream`, `captureToExit` reporting a killed process's exit
    code, and `stream`'s `Task<int>` return type.
  - `tests/Partas.Build.Tests/OutputTests.fs`: a routed line carries the step prefix and a routed blank line is
    dropped, where the same command captured raw keeps both.
  - `tests/Partas.Build.Cmd.NetStandard.Tests`: the `Arguments` string quotes each argument, and a real child
    reads back what went in.
  - `tests/Partas.Build.Tests/CmdTests.fs`: the pre-existing tree kill on stage timeout and on caller-token
    cancellation, and `Ok()` for the latter, all unchanged through the shared executor.
- Verified against the real library by T3, and reproducible from the repository:
  - `tests/Partas.Build.Tests/ExecutionTests.fs`, the `operations` list (16 tests): an attempted capture
    answering a rejected exit code with its raw text; a checked capture failing with that text as evidence; a
    streamed command failing with no capture; a stage's acceptable set letting both checked forms succeed; a
    parsing failure retaining its `FormatException`; a start failure naming the executable from all three
    adapters; cancellation of an attempt surfacing as cancellation and firing no fallback; a cancelled step
    staying a cancellation; `OwnTimeout` producing `FailureCause.TimedOut`; a successful capture printing
    nothing into the stage's own capture; working directory and environment inherited; declaration and
    materialization starting neither an async nor a task factory; a stage consuming clean data and branching on
    an attempted exit; a failing operation failing its stage; `--explain` rendering a label and an index.
  - `tests/Partas.Build.Tests/StageTests.fs` and `CompositionTests.fs` name the new step case, and the T1
    consumer tests pass unchanged through `Step.Operation`.
- Not yet verified:
  - XML documentation rendering and IntelliSense presentation of the inherited operation.
  - Producer registration, ownership, retry reset, publication, or dependency validation diagnostics.
  - `FailureCause.TimedOut` reaching a report from a real stage `timeout`. The runner passes the stage's own
    timeout token as `RuntimeContext.OwnTimeout`, and `Operation.toStepOutcome` classifies on it, but that token
    is also inside the ambient one, so F# `async` cancels the continuation before the outcome is returned and
    the stage reports the timeout through the path it already had. A separate own-timeout token is T7's, with
    `onFailure`.
  - Process behaviour on Linux: every T2 and T3 run was on Windows. The tree kill on `netstandard2.0` is
    unexercised on every platform, since only the `net10.0` build runs the process tests.

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
