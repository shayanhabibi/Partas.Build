# Typed Execution Implementation Plan

> For agentic workers: use `superpowers:executing-plans` when execution is requested. Execute tasks sequentially; track the checkboxes. Read the spec before changing code.

**Goal:** Implement typed runtime command results, static producer dependencies, and scoped failure hooks without breaking applicative inputs.

**Architecture:** Share state-preserving settings through SRTP mapping. Separate process mechanics, runtime operations, declared producers, invocation state, and failure policy.

**Tech Stack:** F#, .NET SDK selected by `global.json`, library targets `net10.0;net8.0;netstandard2.0`, Expecto, System.CommandLine, Spectre.Console.

**Spec:** [PLAN-Execution.md](PLAN-Execution.md).

## Status and execution rules

- Planning only: no implementation tasks have been executed in the repository.
- T0–T1 establish a compiler-backed design; T2–T7 establish the behavioral vertical slice.
- T8 broadens the builder refactor only after the slice works; T9 validates the complete deliverable.
- Ship as four branches, each merged green before the next starts. Later branches depend on earlier ones; none
  is reviewable as a whole otherwise:
  1. `execution/commands`: T0, T1, T2, T3. Shared executor, typed capture, `Operation<'T>`, `Step.Operation`.
  2. `execution/producers`: T4, T5. Producer identity, validation, publication, scope reset.
  3. `execution/failures`: T6, T7. Raw outcomes, structured evidence, `onFailure`, the end-to-end fixture.
  4. `execution/builders`: T8, T9. SRTP dedup of the remaining settings, docs, full acceptance.
- Preserve unrelated existing changes; create an isolated worktree at implementation time if needed.
- Prefix every shell command with `rtk`.
- Prefer `voption`/`ValueOption` and struct DUs where consistent with existing library style.
- Preserve existing CLI input discovery, stage ordering, conditions, output routing, and command defaults.
- New source files must be explicitly ordered in `.fsproj`; account for F# type/implementation dependencies.
- New executable tests use Expecto and run `--sequenced`.
- For behavior changes: add a focused failing test, observe failure, implement, rerun that test, then its affected suite.
- Do not run live GitHub publication, downloads, npm installation, or Fable.Electron generation as acceptance tests.
- Treat code examples below as proposed signatures/fixtures until T1 replaces them with compiled evidence.
- If a compiler result forces a contract change, update the spec explicitly before rollout; do not weaken input discovery to make syntax compile.

## File responsibilities

- Modify `src/Partas.Build.Cmd/Program.fs`: retain command construction; route existing `Cmd.run` through shared execution.
- Create `src/Partas.Build.Cmd/Execution.fs`: process result types and shared process lifecycle; place before/after existing code as dependency order requires.
- Modify `src/Partas.Build.Cmd/Partas.Build.Cmd.fsproj`: explicit compile order; split command definitions from execution if needed to avoid a file-order cycle.
- Modify `src/Partas.Build/Types.fs`: minimal runtime model integration, raw outcomes, stage/step representation changes, and runner integration points.
- Create `src/Partas.Build/Builders/StageSettings.fs`: public-but-hidden SRTP mapping helpers and inherited stage settings.
- Modify `src/Partas.Build/Builders/Stage.fs`: inherit settings; integrate new operations/producers; preserve existing composition overload behavior.
- Create `src/Partas.Build/Operations.fs`: deferred typed operations and stage-aware command adapters.
- Create `src/Partas.Build/Dependencies.fs`: producer definitions, applicative dependency composition, and validation.
- Create `src/Partas.Build/ExecutionState.fs`: invocation/attempt scopes, publication, typed lookup, and supported scheduling.
- Create `src/Partas.Build/Failures.fs`: structured failure helpers and handler execution.
- Modify `src/Partas.Build/Process.fs`: adapt existing `run` operations to shared process execution.
- Modify `src/Partas.Build/Builders/Pipeline.fs` and `Builders/Command.fs`: declaration harvesting and validation entry points where required.
- Modify `src/Partas.Build/Explain.fs` and `Summary.fs`: dependency descriptions and actual outcomes.
- Modify `src/Partas.Build/Partas.Build.fsproj`: explicit compile ordering; keep model declarations needed by earlier files in `Types.fs`.
- Create `tests/Partas.Build.CompilerProbe/`: separate consumer executable referencing the real library, not copied library models.
- Create `tests/Partas.Build.CompilerProbe.Negative/`: one `.fsproj` per must-not-compile case, each a single
  file, none referenced by the solution. `tests/Partas.Build.Tests/CompilerTests.fs` runs `dotnet build` on each
  and asserts a non-zero exit and the expected `FS` code in the output; a build that fails for any other reason
  (restore, missing SDK) fails the test with the full output. This is the harness every negative fixture uses.
- Create `tests/Fixtures/ProcessFixture/`: deterministic console child for stdout/stderr, exit, delay, and cancellation cases.
- Add focused `ExecutionTests.fs`, `DependencyTests.fs`, and `FailureTests.fs` to `tests/Partas.Build.Tests/`.
- Extend existing `CompositionTests.fs`, `InputsTests.fs`, `OutputTests.fs`, `CmdTests.fs`, `StageTests.fs`, `ParallelismTests.fs`, `ExplainTests.fs`, and `SummaryTests.fs` for regressions relevant to each task.
- Modify `tests/Partas.Build.Tests/Partas.Build.Tests.fsproj` and build integration as necessary for new fixtures/probes.
- Update `README.md`, `docs/content/Build/CAPABILITIES.md`, and applicable `src/Partas.Build/xmldoc/` fragments after behavior is established.

## T0 — Establish baseline and preserve existing semantics

- Contracts: B01–B02, C01.
- Files: existing build/test projects; `notes/PLAN-Execution-Tasks.md` evidence section.
- Consumes: current repository state and `notes/PLAN.md` verified compiler findings.
- Produces: recorded baseline, relevant regression inventory, and exact implementation starting point.
- [x] Record branch, commit, and working-tree changes without modifying unrelated files.
- [x] Run Debug/Release library builds and the existing sequenced test suite using the commands below.
- [x] Identify existing tests covering argument masking, environment inheritance, cancellation/tree kill, retry, parallel buffers, and command defaults.
- [x] Record pre-existing failures separately from implementation failures.
- [x] Reproduce each defect in the spec's *Pre-existing defects* list with a failing test, marked pending until
      the task that fixes it: `netstandard2.0` argument re-splitting (T2), discarded `stageExns` (T6), and a
      pinning test for legacy `run` returning `Ok()` on caller-token cancellation (T2, behaviour preserved).
- Completion: baseline is reproducible; failures have identifiable causes before new runtime work begins.

## T1 — Prove builder mapping and static dependency composition

- Contracts: B01–B02, D01.
- Files: `Builders/StageSettings.fs`, `Builders/Stage.fs`, library project; new compiler probe; `CompositionTests.fs`.
- Consumes: existing `BuildStage`, `InputSpec`, and real builder overload families.
- Produces:
  - `mapStage: (StageContext -> StageContext) -> ^State -> ^State`, constrained to supported mapping cases.
  - A shared base implementation of `retry` only; leave broad settings migration for T8.
  - Compiled candidate interfaces for `Operation<'T>`, `Producer<'T>`, and `DependencySpec<'T>`.
- [x] Port the reduced mapping probe to the real library; test `retry` before, after, and on both sides of an input-aware child.
- [x] Assert inferred result types through functions requiring exactly `StageContext` or `InputSpec<StageContext>`.
- [x] Assert input declarations are visible with zero `Read` executions.
- [x] Compile a separate consumer in Debug and Release to exercise public inline helper accessibility.
- [x] Add a negative compiler fixture showing nested `InputSpec<InputSpec<_>>` is not silently accepted/flattened, through the `CompilerProbe.Negative` harness.
- [x] Add a negative compiler fixture showing `let! x = needs p` inside `stage { }` fails with `FS0708`.
- [x] Record the diagnostic text an SRTP setting produces when applied to an unsupported state, for T9's docs.
- [x] Prove a functional producer declaration accepting input sources, static dependencies, and a deferred callback before adding CE sugar.
- [x] Prove the consumer side: `Stage.consuming` returns `StageContext`, `Stage.consumingWith` an input source
      returns `InputSpec<StageContext>`, and a two-producer `DependencySpec.zip` delivers a typed tuple.
- [x] Prove multiple dependencies compose applicatively and contribute their inputs without invoking callbacks.
- [x] Attempt the `needs`/`execute` CE sugar over two producers; record whether the tuple type infers without annotation, and mark the sugar rejected or accepted in the spec.
- [x] Record concrete signatures, compile order, diagnostics, and examples in the spec; label any failed syntax as rejected.
- Completion: real-library positive/negative compiler probes establish composition and type boundaries for producers and consumers; no producer work occurs during construction.

## T2 — Consolidate process execution and introduce typed command results

- Contracts: C01–C02.
- Files: `Partas.Build.Cmd/Program.fs`, new `Execution.fs`, `Process.fs`, project files, process fixture, `ExecutionTests.fs`, `CmdTests.fs`, `OutputTests.fs`.
- Consumes: `Cmd`, explicit working directory/environment, cancellation, and output policy.
- Produces:
  - `CommandResult = { ExitCode: int; Stdout: string; Stderr: string }` for explicitly captured completion.
  - One shared executor with stage-independent configuration and explicit capture/stream behavior.
  - Compatibility paths for existing `Cmd.run` and stage `run`.
- [x] Build a deterministic process fixture with independent stdout/stderr text, chosen exit code, and delayed-exit modes.
- [x] Add a failing test: stdout containing blank lines/newlines is returned exactly and contains no stage prefix.
- [x] Add a failing test: simultaneous large stdout/stderr both drain completely before completion.
- [x] Add start-failure and cancellation tests; retain existing process-tree regression coverage.
- [x] Add a failing test: an argument containing whitespace and quotes reaches the child intact on every target, including `netstandard2.0`, where the executor quotes into `ProcessStartInfo.Arguments` with the MSVCRT rules.
- [x] Add a pinning test: legacy `run` still returns `Ok()` when the caller-supplied token cancels the command.
- [x] Implement the shared executor; preserve argument transport, target-specific APIs, and command-log masking.
- [x] Adapt legacy entry points without changing their documented return shapes or ordinary output routing.
- [x] Verify an uncaptured command does not allocate/store a hidden full-output result.
- Completion: both existing execution paths use the same process mechanics; capture is raw and command-local; legacy regressions pass.

## T3 — Add deferred runtime operations and explicit command failure policy

- Contracts: C02, D01, F02.
- Files: `Operations.fs`, minimal model declarations in `Types.fs`, `Builders/Stage.fs`, library project, `ExecutionTests.fs`.
- Consumes: shared executor and inherited stage execution context.
- Produces proposed operations:
  - `execute: Cmd -> Operation<unit>`.
  - `executeCapture: Cmd -> Operation<CommandResult>`.
  - `attemptCapture: Cmd -> Operation<CommandResult>`.
  - `Operation.ofAsync: Async<'T> -> Operation<'T>`.
  - `Operation.ofTaskFactory: (unit -> Task<'T>) -> Operation<'T>`.
- [x] Add failing tests for nonzero checked capture, nonzero attempted capture, accepted nonzero exit, parse failure, and startup failure.
- [x] Add a cancellation test proving an attempted capture cannot trigger fallback by treating cancellation as a process exit.
- [x] Add a failing test: cancelling an `attemptCapture` from the caller token surfaces as cancellation, not `Ok` and not a `CommandResult`.
- [x] Implement deferred execution and sequencing with inherited working directory/environment and acceptable exit codes.
- [x] Add `Step.Operation` to the `Step` union and run it in `StageContext.run` beside `StepFn`; leave `StepFn` and every `unifyResult` overload unchanged. Render a failed `StepOutcome` to the legacy string at the print site only.
- [x] Verify declaration/materialization invokes no operation or task factory.
- Completion: one executing stage can consume clean command data, branch on an attempted exit, and report structured failure.

## T4 — Declare and validate producer dependencies

- Contracts: B01, D01–D02.
- Files: `Dependencies.fs`, minimal model declarations in `Types.fs`, stage/pipeline/command builders, `Explain.fs`, projects, `DependencyTests.fs`, `InputsTests.fs`, `ExplainTests.fs`.
- Consumes: T1's compiled definition surface and T3's deferred operations.
- Produces: stable typed producer handles, applicative dependency declarations, and a validated invocation plan.
- [ ] Add failing tests for shared-handle identity, distinct same-name handles, transitive CLI option harvesting, and deferred callbacks.
- [ ] Add failing validation tests for dependency cycles and consumers preceding explicitly placed required producers.
- [ ] Add a failing validation test: an unlisted producer whose first consumer sits inside a `parallel'` or `shuffleExecuteSequence` scope is rejected, naming the producer and the scope.
- [ ] Add a failing test: a handle's `ProducerId` survives the runner's stage copy, and two `Producer.define` calls with identical arguments get different ids.
- [ ] Implement identity as an allocated `ProducerId` and declaration traversal without parsing/command side effects.
- [ ] Validate ownership/placement before execution; reject ambiguous arrangements with producer and scope names in diagnostics.
- [ ] Teach `--explain` to display dependencies without invoking producer callbacks; retain existing legacy-condition semantics.
- Completion: the graph and CLI requirements are inspectable; invalid supported-model arrangements fail before producer effects.

## T5 — Execute and publish producer values within invocation scopes

- Contracts: D02–D03, R01.
- Files: `ExecutionState.fs`, `Types.fs` runner integration, `DependencyTests.fs`, `StageTests.fs`, `ParallelismTests.fs`.
- Consumes: validated plan, producer identities, existing retry/cancellation policies.
- Produces: invocation-local execution state, attempt-local producer values, and typed consumer inputs.
- [ ] Add failing test: two sequential consumers share one producer execution; the next invocation executes it again.
- [ ] Add failing test: an explicitly listed producer plus `needs` executes only once in its valid placement.
- [ ] Add failing test: an unlisted producer runs immediately before its first consumer, without reordering preceding work.
- [ ] Add failing tests: skipped/failed producers publish no value; blocked consumers record a dependency reason.
- [ ] Add failing tests: consumer retry reuses external dependencies; enclosing retry refreshes owned producers and removes stale values.
- [ ] Implement publication only after successful completion and explicit reset at owning-attempt boundaries.
- [ ] Permit parallel consumers of completed values; reject unsupported unresolved parallel arrangements clearly.
- Completion: output identity, ordering, scope reset, and absence semantics match the spec without deadlocks or global caching.

## T6 — Expose raw outcomes independently of continuation policy

- Contracts: D03, F01–F02.
- Files: `Types.fs`, `Failures.fs`, `Summary.fs`, `StageTests.fs`, `SummaryTests.fs`, `FailureTests.fs`.
- Consumes: operation failures, producer outcomes, and legacy suppression policies.
- Produces: actual scope outcome, propagation decision, and structured failure evidence as separate data.
- [ ] Add failing test: suppressed producer failure remains failed and supplies no value while independent work continues.
- [ ] Add failing tests retaining exceptions, unacceptable exit codes, and parse failures without inferring anything from stderr.
- [ ] Fix `runStagesWithFailFast` to collect the exceptions `StageContext.run` returns; the T0 pending test for a `PipelineFailedException` carrying its cause turns green here.
- [ ] Retain a suppressed step's exception as evidence on the raw outcome even when `ContinueStageOnFailure` keeps it out of the propagated list.
- [ ] Integrate structured reports with existing stage execution and timing/summary output.
- [ ] Preserve existing public timing behavior where possible; avoid introducing a breaking public union change solely for internal control flow.
- Completion: downstream control flow never consults rendered logs or timing summaries to determine failure.

## T7 — Implement scoped failure handlers and complete the vertical slice

- Contracts: F01–F02, R01.
- Files: `Failures.fs`, `Types.fs`, stage settings/builder integration, `FailureTests.fs`, `DependencyTests.fs`.
- Consumes: raw outcomes, attempt scopes, and published values.
- Produces:
  - `onFailure` registration for a scope.
  - `FailureContext` with primary/secondary causes and stage/step identity.
  - `FailureContext.TryGetOutput: Producer<'T> -> 'T voption` with no execution side effects.
- [ ] Add failing tests: handler runs after retry exhaustion, never on successful retry, and never for ordinary cancellation.
- [ ] Add failing tests: a stage's own `timeout` runs its `onFailure` with `FailureCause.TimedOut`; a pipeline timeout or invocation cancellation runs no handler in the stages it cancels.
- [ ] Add failing tests: `onFailure` on `pipeline { }` runs after the failed stage's own handler and observes the pipeline's final failure.
- [ ] Add failing tests: inner-before-outer ordering, per-scope-execution invocation, and handler failure preserving the primary cause.
- [ ] Add failing test: lookup of an absent producer does not run it; successful still-valid metadata is available.
- [ ] Implement handlers subject to invocation cancellation; do not add general cleanup or a new cleanup budget.
- [ ] Run an end-to-end fixture: CLI input -> captured JSON -> typed producer -> consumer -> retry -> final failure -> recorded report.
- [ ] Run the same fixture through `--help` and `--explain`; verify no producer, process, or reporting callback executes.
- Completion: the vertical slice demonstrates the migration use case with local deterministic fixtures and no external side effects.

## T8 — Expand builder deduplication after the slice passes

- Contracts: B01–B02.
- Files: `Builders/StageSettings.fs`, `Builders/Stage.fs`, relevant XML fragments, compiler probe, `CompositionTests.fs`.
- Consumes: T1's verified mapping and the working runtime surface.
- Produces: one implementation per state-preserving stage setting, with genuinely distinct argument overloads retained.
- [ ] Inventory plain/input-aware mirror pairs; classify each as state-preserving, argument conversion, or representation-changing.
- [ ] Move state-preserving stage settings into the shared base in small groups.
- [ ] After each group, run relevant composition tests and the separate Debug/Release consumer.
- [ ] Verify member docs and user-facing completion attributes; hide support machinery rather than DSL operations.
- [ ] Record any operation retained as an overload pair and the compiler/semantic reason.
- Completion: duplicate setting implementations are reduced without erasing type distinctions or changing existing call-site results.

## T9 — Document limitations and run full acceptance

- Contracts: all; especially D03 and deferred scope.
- Files: README, capabilities docs, XML docs, build integration, both execution-plan files.
- Consumes: implemented behavior and recorded compiler/test evidence.
- Produces: documented supported surface and reproducible acceptance evidence.
- [ ] Document checked/attempted capture, static dependencies, handle identity, scope retry ownership, skips, and handler behavior.
- [ ] Document supported parallel arrangements and diagnostics for unsupported cases.
- [ ] Add a migration example moving command work out of `InputSpec.Read`; preserve the applicative CLI layer.
- [ ] Run focused suites, both library configurations, all library target builds, compiler consumers, and full repository acceptance.
- [ ] Inspect the final diff for accidental changes to existing command defaults, input discovery, or output routing.
- [ ] Record results and limitations; mark tasks complete only when their stated outcomes are observed.
- Completion: all accepted contracts have tests or compiler evidence; deferred behavior is explicitly documented, not silently approximated.

## Verification commands

```powershell
rtk dotnet build src/Partas.Build/Partas.Build.fsproj -c Debug
rtk dotnet build src/Partas.Build/Partas.Build.fsproj -c Release
rtk dotnet build src/Partas.Build.Cmd/Partas.Build.Cmd.fsproj -c Release
rtk dotnet run --project tests/Partas.Build.Tests -- --sequenced
rtk dotnet run --project tests/Partas.Build.Tests -- --sequenced --filter-test-case "<exact test name added by the task>"
rtk dotnet run --project tests/Partas.Build.CompilerProbe -c Debug
rtk dotnet run --project tests/Partas.Build.CompilerProbe -c Release
rtk dotnet run --project tests/Partas.Build.Tests -- --sequenced --filter "CompilerTests"   # drives CompilerProbe.Negative
rtk dotnet run --project Build.fsproj -- test --quick --configuration Debug
rtk dotnet run --project Build.fsproj -- test --configuration Release
```

- Project builds above cover each library's declared target frameworks.
- Negative compiler cases must fail with the expected type/constraint diagnostic; infrastructure failures do not count.
- Process behavior needs Windows and Linux coverage where supported; record unavailable-platform coverage rather than claiming it ran locally.
- Only execute commands for fixtures/projects once their task creates them.

## Evidence log

- Planning: inspected input/builders, process runners, runner hooks, existing tests, project targets, and repository guidance.
- Prior reduced probe: generic inherited setting succeeded across both state representations and a separate Release consumer.
- Repository implementation, full baselines, and end-to-end vertical slice: not run during planning.
- T0 (2026-09-17, worktree `Partas.Build-execution-commands`, branch `execution/commands` from `657c30c`):
  - Baseline builds: `src/Partas.Build` Debug and Release, `src/Partas.Build.Cmd` Release, all 0 errors 0 warnings.
  - Baseline suite: `tests/Partas.Build.Tests --sequenced` 160 passed, 0 failed. The "Could not execute" lines
    in the log are `OutputTests` deliberately running a missing `dotnet` command. No pre-existing failures.
  - Regression inventory (names as of this commit): masking — `CmdTests` "a sensitive command passes its values
    through …", "a secret argument masked in log string …", `ExplainTests` "explain masks secret …"; environment —
    `CmdTests` "the working directory environment resolved through parent", `CommandTests` "env vars merge per
    key"; cancellation/tree kill — `CmdTests` "a stage timeout kills the process and everything it started";
    retry — `StageTests` "retry …" (six tests); parallel buffers — `ParallelismTests` "… flush output in blocks",
    `OutputTests` "a step buffer forces redirection …", `StageTests` "a retry under parallel' …"; command
    defaults — `CommandTests` "a command default …" family (eight tests).
  - Pending defect tests (each observed failing un-pended, then marked `ptest`):
    - `tests/Partas.Build.Cmd.NetStandard.Tests` (new; references the netstandard2.0 build of Partas.Build.Cmd
      via `SetTargetFramework`) "the netstandard build quotes an argument that contains whitespace or a quote":
      actual `a b plain say "hi"`. Unpend in T2. Not yet registered with the `Build` CLI `test` command.
    - `PipelineTests` "a pipeline surfaces the exception a stage raised": `PipelineFailedException.InnerException`
      is null. Unpend in T6.
    - `CmdTests` "legacy run reports success when the caller's own token cancels the process": passes; pins C02.
  - Suite after T0: 161 passed, 1 ignored (the pending pipeline test), 0 failed.
  - Sibling worktree `Partas.Build-execution-slice` (`codex/typed-execution-slice`) holds uncommitted codex drafts of
    `Execution.fs` (90 lines), `StageSettings.fs` (25 lines) and a C# `ProcessFixture`; read for salvage in T1/T2,
    not built on.
- T1 (2026-09-17, worktree `Partas.Build-execution-commands`, branch `execution/commands`):
  - Builder mapping: `Builders/StageSettings.fs` holds `StageMap` (three `Map` overloads plus the SRTP `Apply`),
    `StageMap.mapStage`, and `StageSettingsBuilder` with the one generic `retry`. `StageBuilder` inherits it and
    declares neither `retry` any more. Debug and Release library builds cover all three target frameworks;
    `Build.fsproj`, written against the library, compiles unchanged.
  - Dependencies: `src/Partas.Build/Dependencies.fs` (after `Types.fs`) holds `RuntimeContext`, `Operation<'T>`,
    `ProducerId`, `ProducerRef`, `ProducerValues`, `Producer<'T>`, `DependencySpec<'T>`, and the `Operation`,
    `DependencySpec`, `Producer` and `Stage` modules. Signatures and limits are in the spec's *Compiled surface*.
  - Probes: `tests/Partas.Build.CompilerProbe` (5 tests, green in Debug and Release, added to `Partas.Build.slnx`);
    `tests/Partas.Build.CompilerProbe.Negative/{NestedInputSpec,MonadicNeeds,UnsupportedSettingState}` driven by
    `tests/Partas.Build.Tests/CompilerTests.fs`, in no solution. Observed `FS0193`, `FS0708` and `FS0001`
    respectively; the three builds take about 5s together.
  - CE sugar over two producers is rejected: the threaded state is `(unit * 'A) * 'B`, so a flat tuple pattern is
    `FS0001`. Recorded in the spec under *Rejected syntax*.
  - Suite after T1: 175 passed, 1 ignored (the T0 pending pipeline test), 0 failed.
  - Review fix round 1: `DependencySpec.Read` and `Producer.Prepare` answer `Result<_, string>` so a caller's own
    exception is no longer read as a missing prerequisite; `Stage.consuming` rejects prerequisites declaring CLI
    inputs, naming `consumingWith`; `--explain` over consumer stages is pinned; the two recorded diagnostics are
    now verbatim.
- T2 (2026-09-17, worktree `Partas.Build-execution-commands`, branch `execution/commands`):
  - Shared executor: `src/Partas.Build.Cmd/Execution.fs`, compiled before `Program.fs`, holds `CommandResult`,
    `OutputPolicy` and `ProcessExecutor` (`Arguments.quote`/`Arguments.transport`, `stream`/`capture` raising on
    cancellation, `streamToExit`/`captureToExit` reporting a killed process's exit code). Signatures are in the
    spec's *Implemented surface (T2)*.
  - Adapters: `Cmd.run` captures and splits, keeping its `struct {| exitCode; output; error |}`; `CmdRunner.run`
    links the ambient and caller tokens into one and keeps `Ok()` for a caller-token cancellation. Neither
    routing nor masking moved.
  - Fixture: `tests/Fixtures/ProcessFixture` (C#, `net10.0`, in `Partas.Build.slnx` under `/tests/fixtures/`),
    referenced with `ReferenceOutputAssembly="false"` by `tests/Partas.Build.Tests` and
    `tests/Partas.Build.Cmd.NetStandard.Tests`, and registered with no `Build/` CLI command.
  - RED observed before implementing: raw stdout through the old line-oriented capture returned `alphabeta` for
    `alpha\n\nbeta\n`; the un-pended `netstandard2.0` quoting test returned `a b plain say "hi"` where
    `"a b" plain "say \"hi\""` was expected. Both green afterwards.
  - Suites: `tests/Partas.Build.Tests` 186 passed, 1 ignored (the T0 pending pipeline test), 0 failed;
    `tests/Partas.Build.Cmd.NetStandard.Tests` 3 passed; the two external-annotation suites unchanged at 65 and
    74 passed. Library Debug and Release, `Partas.Build.Cmd` Release and `Build.fsproj` all build with 0 errors.
  - Not run: anything on Linux, and the `netstandard2.0` tree kill on any platform.
- T3 (2026-09-17, worktree `Partas.Build-execution-commands`, branch `execution/commands`):
  - Model: `Types.fs` gains `FailureCause`, `StepOutcome`, `OperationFailedException`, `FailureCause.describe`,
    `RuntimeContext` (moved from `Dependencies.fs`), the `Step.Operation` case and
    `StageContext.addOperation`. `StepFn` and all twelve `unifyResult` overloads are untouched; the failed
    outcome becomes a string only in `StageContext.run`'s new branch, which hands it to the same `printError`.
  - Operations: `src/Partas.Build/Operations.fs` holds `Operation<'T>`, the `Operation` module
    (`ret`/`ofAsync`/`ofTaskFactory`/`map`/`bind`/`fail`/`toStepOutcome`) and the auto-opened
    `execute`/`executeCapture`/`attemptCapture`. Only `ProcessExecutor.stream`/`capture` are used, so a
    cancelled command never arrives as a `CommandResult`. Signatures are in the spec's
    *Implemented surface (T3)*.
  - Compile order: `Types.fs` → `Process.fs` → `Operations.fs` → `Dependencies.fs` → `Builders/StageSettings.fs`
    → `Builders/Stage.fs`. `Process.fs` now exposes `CmdRunner.stepPrefix`/`logCommand`/`outputPolicy`/
    `announceKill`, which `CmdRunner.run` and the new adapters share; `run`'s behaviour is unchanged.
  - Stage syntax: `runOperation <operation> [<label>]`, plus its `InputSpec` mirror, outside the `run` family so
    that `run`'s SRTP catch-all keeps resolving as it did. `Stage.consuming`/`consumingWith` build a
    `Step.Operation` too.
  - RED observed before fixing: the start-failure test reported an `AggregateException` where a
    `FailureCause.Start` was expected, because `Async.AwaitTask` wraps a task's exception; `Awaited.unwrap` is
    the fix, and `toStepOutcome` unwraps as well so a parse failure keeps its own exception. The model and the
    adapters were written before their tests rather than after, which is a deviation from the task's TDD order.
  - Suites: `tests/Partas.Build.Tests --sequenced` 202 passed, 1 ignored (the T0 pending pipeline test), 0
    failed, of which the new `operations` list is 16. `tests/Partas.Build.CompilerProbe -c Release` 5 passed.
    Library Debug and Release and `Build.fsproj` build with 0 errors; `Build.fsproj`'s two `NU1605` FSharp.Core
    downgrade warnings predate this task.
  - Timeout classification belongs to `StageContext.run`, which holds the stage's own timeout source, the
    ancestor token and the stage-policy source together. It records the running operation's step prefix and
    classifies once the attempt has unwound, because an `async` under a cancelled token runs neither its `with`
    handler nor its continuation. RED observed twice here: `got []` with no classification at all, then again
    with the classification written as a `with` handler inside the step's `async`, which never ran.
    `RuntimeContext.OwnTimeout` was removed once the runner owned the decision.
  - Not reached: a `timeoutForStep` expiry (`Async.StartChild` reports it to the waiter as a `TimeoutException`),
    and naming each of several concurrent operation steps a stage timeout ended.
