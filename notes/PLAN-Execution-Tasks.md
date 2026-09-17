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
- [ ] Record branch, commit, and working-tree changes without modifying unrelated files.
- [ ] Run Debug/Release library builds and the existing sequenced test suite using the commands below.
- [ ] Identify existing tests covering argument masking, environment inheritance, cancellation/tree kill, retry, parallel buffers, and command defaults.
- [ ] Record pre-existing failures separately from implementation failures.
- Completion: baseline is reproducible; failures have identifiable causes before new runtime work begins.

## T1 — Prove builder mapping and static dependency composition

- Contracts: B01–B02, D01.
- Files: `Builders/StageSettings.fs`, `Builders/Stage.fs`, library project; new compiler probe; `CompositionTests.fs`.
- Consumes: existing `BuildStage`, `InputSpec`, and real builder overload families.
- Produces:
  - `mapStage: (StageContext -> StageContext) -> ^State -> ^State`, constrained to supported mapping cases.
  - A shared base implementation of `retry` only; leave broad settings migration for T8.
  - Compiled candidate interfaces for `Operation<'T>`, `Producer<'T>`, and `DependencySpec<'T>`.
- [ ] Port the reduced mapping probe to the real library; test `retry` before, after, and on both sides of an input-aware child.
- [ ] Assert inferred result types through functions requiring exactly `StageContext` or `InputSpec<StageContext>`.
- [ ] Assert input declarations are visible with zero `Read` executions.
- [ ] Compile a separate consumer in Debug and Release to exercise public inline helper accessibility.
- [ ] Add a negative compiler fixture showing nested `InputSpec<InputSpec<_>>` is not silently accepted/flattened.
- [ ] Prove a functional producer declaration accepting input sources, static dependencies, and a deferred callback before adding CE sugar.
- [ ] Prove multiple dependencies compose applicatively and contribute their inputs without invoking callbacks.
- [ ] Record concrete signatures, compile order, diagnostics, and examples in the spec; label any failed syntax as rejected.
- Completion: real-library positive/negative compiler probes establish composition and type boundaries; no producer work occurs during construction.

## T2 — Consolidate process execution and introduce typed command results

- Contracts: C01–C02.
- Files: `Partas.Build.Cmd/Program.fs`, new `Execution.fs`, `Process.fs`, project files, process fixture, `ExecutionTests.fs`, `CmdTests.fs`, `OutputTests.fs`.
- Consumes: `Cmd`, explicit working directory/environment, cancellation, and output policy.
- Produces:
  - `CommandResult = { ExitCode: int; Stdout: string; Stderr: string }` for explicitly captured completion.
  - One shared executor with stage-independent configuration and explicit capture/stream behavior.
  - Compatibility paths for existing `Cmd.run` and stage `run`.
- [ ] Build a deterministic process fixture with independent stdout/stderr text, chosen exit code, and delayed-exit modes.
- [ ] Add a failing test: stdout containing blank lines/newlines is returned exactly and contains no stage prefix.
- [ ] Add a failing test: simultaneous large stdout/stderr both drain completely before completion.
- [ ] Add start-failure and cancellation tests; retain existing process-tree regression coverage.
- [ ] Implement the shared executor; preserve argument transport, target-specific APIs, and command-log masking.
- [ ] Adapt legacy entry points without changing their documented return shapes or ordinary output routing.
- [ ] Verify an uncaptured command does not allocate/store a hidden full-output result.
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
- [ ] Add failing tests for nonzero checked capture, nonzero attempted capture, accepted nonzero exit, parse failure, and startup failure.
- [ ] Add a cancellation test proving an attempted capture cannot trigger fallback by treating cancellation as a process exit.
- [ ] Implement deferred execution and sequencing with inherited working directory/environment and acceptable exit codes.
- [ ] Preserve structured causes before adapting to legacy `Result<unit,string>` reporting where required.
- [ ] Verify declaration/materialization invokes no operation or task factory.
- Completion: one executing stage can consume clean command data, branch on an attempted exit, and report structured failure.

## T4 — Declare and validate producer dependencies

- Contracts: B01, D01–D02.
- Files: `Dependencies.fs`, minimal model declarations in `Types.fs`, stage/pipeline/command builders, `Explain.fs`, projects, `DependencyTests.fs`, `InputsTests.fs`, `ExplainTests.fs`.
- Consumes: T1's compiled definition surface and T3's deferred operations.
- Produces: stable typed producer handles, applicative dependency declarations, and a validated invocation plan.
- [ ] Add failing tests for shared-handle identity, distinct same-name handles, transitive CLI option harvesting, and deferred callbacks.
- [ ] Add failing validation tests for dependency cycles and consumers preceding explicitly placed required producers.
- [ ] Implement identity and declaration traversal without parsing/command side effects.
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
