# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Read the plans in `notes/` first

`notes/PLAN.md` is the design record for input binding: what changed in the core model and the builders, what each phase did, and what the compiler proved along the way. All seven phases are implemented. Read the phase statuses before changing a builder — several explain why a member looks the way it does.

`notes/PLAN-Execution.md` and its checklist `notes/PLAN-Execution-Tasks.md` own the current execution model: typed producers and `consumes`, failure handlers, the scope reports, and the shared `StageSettingsBuilder`/`PipelineSettingsBuilder`.

The one-line version: input collection is **applicative**, not monadic. `InputSpec<'T> = { Inputs: ActionInput list; Read: ParseResult -> 'T }` makes the input set readable without a `ParseResult`, breaking the circularity between registering options and binding their values. Stages are pure (no `ActionContext`); binding happens in an `inputs { let! … and! … return … }` CE, and pipelines/commands harvest `.Inputs` upward.

## Commands

Every task goes through the `Build` CLI project, not a script:

```shell
dotnet run --project Build.fsproj -- --help
dotnet run --project Build.fsproj -- build            # restore + build
dotnet run --project Build.fsproj -- test             # build + the four Expecto suites
dotnet run --project Build.fsproj -- test --quick     # skip restores/clean
dotnet run --project Build.fsproj -- publish          # pack + push (--nuget-key, else the `local` feed)
dotnet run --project Build.fsproj -- bump [BUMP] -p <project>...   # rewrite <Version> in a project file
dotnet run --project Build.fsproj -- docs [--watch]   # Nacara site build / live serve
dotnet run --project Build.fsproj -- <command> --explain     # print the resolved stage tree, run nothing
```

Flags sit on the commands whose stages read them, not on the root: `--quick` (skip restores and the clean), `--skip-tests`, `--configuration Debug|Release`. `<command> --help` is generated from those stages and is the authority on which command takes what.

For a single test, run the Expecto suite directly:

```shell
dotnet run --project tests/Partas.Build.Tests -- --filter-test-case "conditions conjoin rather than replace"
dotnet run --project tests/Partas.Build.Tests -- --list-tests
```

Fast inner loop while working on the library only: `dotnet build src/Partas.Build`.

## Current state (verify before assuming)

- Phases 0-7 of `notes/PLAN.md` are done: `dotnet build src/Partas.Build` is clean and `dotnet run --project Build.fsproj -- test` is green (443 Expecto tests across four suites — 301 in `tests/Partas.Build.Tests`, one file per layer, plus 65 in `tests/Partas.Build.ExternalAnnotations.Tests`, 74 in `tests/Partas.ExternalAnnotations.Tests` and 3 in `tests/Partas.Build.Cmd.NetStandard.Tests`; the `test` stage also runs `tests/Partas.Build.CompilerProbe` in both Debug and Release, 11 tests each). The `Build/` CLI is written against the library: a breaking change breaks it first.
- The DSL exists end to end: `inputs` (`Builders/Inputs.fs`), `stage` (`Builders/Stage.fs`), `pipeline` (`Builders/Pipeline.fs`), `command`/`rootCommand` (`Builders/Command.fs`). A stage that declares an input turns its pipeline into an `InputSpec<PipelineContext>`, and the command registers whatever those specs declare. Conditions are in `Builders/Conditions.fs` — `whenAll`/`whenAny`/`whenNot`/`whenEnv`/`whenStage` plus the `when'`/`whenEnvVar`/`whenBranch`/`when{Windows,Linux,OSX}` operations on `StageBuilder`.
- A command carries `PipelineDefaults: BuildPipeline` and takes the pipeline-level operations itself (`workingDir`, `envVars`, the three timeouts, `acceptExitCodes`, the output operations, `noPrefixForStep`/`noStdRedirectForStep`, `runBeforeEachStage`/`runAfterEachStage`, `post`, `verbosity`/`verbose`/`quiet`), each one built through `CommandBuilderBase.MapPipelineDefault`. They are **defaults, not overrides**: `PipelineContext.applyDefaults` copies a setting across only where the pipeline left it at the value `PipelineContext.create` gave it, so a pipeline that sets the same thing wins. See *Command defaults* below.
- `run`/`runSensitive` start real processes through `CmdRunner` (`Process.fs`). A `Cmd`, defined in `src/Partas.Build.Cmd/Program.fs`, keeps the executable and its arguments apart all the way to `ProcessStartInfo.ArgumentList`, so the platform does the escaping. Interpolate through the `cmd` helper — `run (cmd $"dotnet build {project}")`. `run $"..."` binds to the `string` overload and flattens the holes; `runSensitive $"..."` takes the `FormattableString` directly and masks every hole as `***`. There is no `Fake.Core.Process` dependency; `notes/PLAN.md`'s *The command runner* records why.
- Fun.Build's `Mode` (`Execution | CommandHelp | Verification`) has **not** been ported. `PipelineContext.Verify` is a placeholder and `buildPipelineVerification` is commented out. `CommandHelp` is redundant: System.CommandLine generates help. Whether `Verification` survives is an open question in `notes/PLAN.md`.
- `PipelineContext.run` is complete enough to execute stages, post stages, timeouts, parallelism and cancellation.

## Architecture notes

Compile order in `Partas.Build.fsproj` matters (F#): `System.CommandLine/Aliases.fs` → `System.CommandLine/Inputs.fs` → `Exceptions.fs` → `Output.fs` → `Environment.fs` → `Timing.fs` → `Producer.fs` → `Failures.fs` → `Conductors.fs` → `Conductors.Runners.fs` → `Process.fs` → `Operations.fs` → `Dependencies.fs` → `DependencyPlan.fs` → `ExecutionState.fs` → `Builders/StageSettings.fs` → `Builders/Stage.fs` → `Builders/Conditions.fs` → `Builders/PipelineSettings.fs` → `Builders/Pipeline.fs` → `Builders/Inputs.fs` → `Explain.fs` → `Summary.fs` → `RunResult.fs` → `Builders/Command.fs`. The batteries-included layer is its own project, `src/Partas.Build.Baked`.

`Explain.fs` renders the resolved stage tree `--explain` prints, as text only, independent of the console and
any stage sink — a stage that silences or captures its output is still described in full. It compiles before
`Builders/Command.fs`, whose `applyTo` registers the flag on every command and prints what the renderer returns.
It depends on nothing beyond the core model and the `Input.*` combinators. A command that runs pipelines gets
`Explain.option` and reads it in its own action. A grouping command gets `Explain.groupingOption`, which carries
the rendering — a list of the subcommands it dispatches to — on the option's own `Action`: such a command has no
action of its own, and adding one would displace System.CommandLine's "Required command was not provided.".
Rendering evaluates every stage's `IsActive`, so a `whenBranch` starts `git` and a `whenStage` runs its condition
stage. `StageContext.Conditions` lets a skip name the condition that caused it: `addPredicateBecause` writes it
alongside `IsActive`. The structured conditions in `Builders/Conditions.fs` supply a reason; `when'` supplies
none, since a `bool` argument carries no reason to report.

`Failures.fs` holds what a scope reports about itself, apart from what it prints. A `ScopeReport` carries three
fields: `Outcome` (the scope's own `StageOutcome`), `Propagates` (whether that failure fails the scope
containing it), and `Failures` (a `StepFailure` per cause, each naming the step's index and label and retaining
the `FailureCause` itself, the raised exception included). `Exceptions` is what the containing scope received,
and `Nested` the reports of the sub-stages. A `continueStageOnFailure` therefore reports `Failed` with
`Propagates = false` and keeps the cause, letting a consumer of a suppressed producer tell a failed producer
from a skipped one. `ScopeReport.propagated` reads the tree down to the scopes whose failures reached the
pipeline; `PipelineContext.run` takes the `PipelineFailedException`'s inner exception from the first of them
through `FailureCause.toException`. The file sits after `Environment.fs`: otherwise `ScopeReport.Name` would be
the last `Name`-bearing record in scope, risking FS0667 if `EnvArg.withName` binds to the wrong record. It sits
after `Producer.fs` because a `FailureContext` carries `ProducerValues`.

`onFailure` registers a `FailureHandler` on a stage and on a pipeline. `Failures.fs` holds both the
`FailureContext` it receives and `FailureContext.runHandlers`, which runs them. A handler runs once per failed
execution of its scope, from the same `finally` of `StageContext.run` that builds the report: nested scopes
report before it does, and retries finish before it runs. It reads the scope's identity off `ScopeAddress` —
the ordinals and names from the pipeline's own stage inward, carried on `StageContext.Address` the way
`TimingOrder` is — and reads the failure itself off `Primary`/`Secondary`, not off rendered text.
`FailureContext.TryGetOutput` is an extension member in `Dependencies.fs`, where `Producer<'T>` exists: a
lookup over `ExecutionState.values` that schedules nothing. An exception out of a handler is appended to the
report's `Failures` under `StepFailure.NoStep`, excluded from `Exceptions`: the cause the pipeline raises stays
the scope's own.

Only `StageContext.run` holds every token, and which one fired tells a timeout from a cancellation. `cts` (the
stage's own `timeout`) and a `StepBudget.expiry` (the `timeoutForStep` given to one step) are failures of that
stage, recorded as `FailureCause.TimedOut` against the steps of `InFlightSteps` that had started and not
finished — every one of them for the stage's own budget, several under `parallel'`, and its own step for a step
budget. `ct` (an ancestor's, the pipeline's, the invocation's) and `stepErrorCts` (stage policy) are
cancellations and run no handler. A step takes its budget when it starts and runs its whole body under it —
`Async.StartChild` adds no second clock — so a stage of several sequential steps gives each of them the whole
budget, and a command reads that token through `Async.CancellationToken`, taking its process tree down with it.
A condition stage answers its condition by failing, runs no handler, and records neither a timing nor a report.
The stage's stopwatch stops before its handlers run, so a slow handler stays out of the summary row.

`PipelineContext.Reports` collects those reports the way `Timings` collects timings: a `ScopeReports` on the
pipeline value, emptied at the start of a run and appended to as each stage finishes. It is the structured
counterpart of the summary table — a failure is readable directly, rather than from rendered output or
`StageTiming`. Both records are written in the same `finally` of `StageContext.run`, keyed off
`Internal.getTimings`/`getReports`, so a stage that raises out of `run` — a `failIfIgnored` guard, an exception
escaping a sub-stage — appears in both. `getReports` answers the pipeline for a stage parented to one; a
sub-stage travels inside its parent's `Nested`, and a condition stage is recorded nowhere.

A step writes its evidence into `StepEvidence` as it produces it, rather than handing it back. A step that
fails cancels its own scope through `stepErrorCts` before its `async` returns; a cancelled `async` delivers no
result, so evidence carried in the return value of a failing step is dropped on the floor. `StepEvidence` owns
its three collections and is the only door to them: every write, the per-attempt `clear`, and the `snapshot`
the report is built from take one lock, needed by a `parallel'` stage and a straggler of an earlier attempt.

`Summary.fs` renders the per-stage timing table printed at the end of a run, as text only, the way
`Explain.fs` renders the tree. Timings are collected in `Timing.fs` and `Conductors.fs`: `PipelineContext.Timings`
is a `StageTimings` the stages append a `StageTiming` to as each finishes, reached by walking `ParentContext` up
to the pipeline. A stage takes an ordinal from `StageTimings.Start()` when it starts and records it alongside
its parent's, read off `StageContext.TimingOrder` — the field `StageContext.run` sets on the value it gives its
sub-stages as their `ParentContext`. `Ordered` walks those pairs into a pre-order tree, so a stage sits under
its own parent however its siblings interleave under `parallel'`; a stage of the pipeline records the parent
ordinal `0L`. A condition stage takes no ordinal, and neither it nor anything under it is recorded. The print
site is `Builders/Command.fs`'s `runReportingTimings`, which prints in a `finally` so a failed run still
reports, and prints nothing for a quiet pipeline or a run of a single stage, whose wall time the pipeline's own
line already carries. `Summary.render` sizes its three columns to the ambient console and elides what does not
fit — the middle of a stage name, the end of an outcome — so the table is one row per stage at any width, and
the `Depth` indent survives an 80-column CI log.

`RunResult.fs` holds what a command invocation answers, and the exit codes it ends with: `ExitCode.Success`
(0 — help, `--version` and `--explain` included), `Failure` (1, a stage failed), `UsageError` (2 — a parse
error, a missing subcommand, a failed `DependencyPlan.validate`) and `Cancelled` (130). `RunResult` carries the
code, its `RunOutcome`, and a `PipelineRun` per pipeline started — its `ScopeReports.stages` and ordered
`StageTimings`, snapshotted in `runReportingTimings`'s `finally` so a later run of the same pipeline value
leaves an earlier result intact. `Command.root { … }` builds a `RootCommandDefinition` (the `rootCommand`
operations, via the shared `RootCommandBuilderBase.Define`) without parsing or running; `Command.invoke args`
and `RootCommandDefinition.Invoke(args, ?output, ?error, ?cancellationToken)` parse and run it, and
`RootCommandBuilder.Run` is `Define` + `Invoke` + `.ExitCode`, so the exit-code mapping lives in `Invoke` alone:
a parse result whose `Action` is System.CommandLine's `ParseErrorAction` becomes 2 there, whatever the action
returned. The command action finds its invocation's state — the cancellation token and the `PipelineRun`
collector — in a `ConditionalWeakTable` keyed by the `ParseResult`; a parse result invoked directly through
System.CommandLine has none, runs uncancellable, and still gets 2 for a failed dependency validation (the
action returns it) but 1 for a parse error (System.CommandLine's own). The token reaches the engine through
`PipelineContext.runWith`, which links it into the pipeline's own timeout source. `output` replaces the
invocation configuration's `Output`/`Error`, which is where help, parse errors, `--explain`, the timing summary
and the dependency diagnostic go; stage output still goes to Spectre's ambient console.

`src/Partas.Build.Cmd` (`Program.fs`, `Execution.fs`) is the process layer, defining `Cmd` and compiling before `Partas.Build`.

`src/Partas.Build.Baked` is the batteries-included layer over the library: ready-made `Input.*`/`Argument.*` definitions
for the options every build CLI ends up wanting (`--configuration`, `--nuget-key`, `--project`, `--ci`, a version
bump), the semver arithmetic in `Version`, and `IO.writeVersion`/`IO.bumpVersion` for editing a project file's
`<Version>`. It is the only place in the library that writes to disk.

The core model, once a single `Types.fs`, is split by responsibility and compiles in this order:
1. `Exceptions.fs` (`Partas.Build.ErrorHandling`) — pipeline exceptions, `FailureCause`, `StepOutcome`.
2. `Output.fs` (`Partas.Build.OutputHandling`) — `OutputCapture`, `StageOutput`, markup.
3. `Environment.fs`, `Timing.fs`, `Producer.fs` (`Partas.Build`) — `EnvArg`; `StageTimings`; `ProducerId`/`ProducerRef`/`ProducerValues` and the `ExecutionState` type.
4. `Conductors.fs` — `Partas.Build.Internal` first: `StageContext`, `PipelineContext`, `CommandSpec`. `Step` is `StepFn | Operation | StepOfStage`: a nested stage *is* one step of its parent, and stages nest arbitrarily. `StageParent` links a stage to a parent stage or the pipeline. The file ends in `Partas.Build` with the `StageContext` lookups that need `FsToolkit`/`HttpClient`.
5. `Conductors.Runners.fs` (`Partas.Build.Internal.Runners`) — the execution engine (`StageContext.run`, `PipelineContext.run`).

The `Build*` function aliases (`BuildStage`, `BuildStep`, `BuildStageIsActive`, …) no longer take an `ActionContext`: stages are pure, and the aliases match Fun.Build's originals except that Fun.Build declares them as delegates where this port uses plain function types. That difference has one mechanical consequence: **do not mark a CE entry member `inline` when it applies a `Build*` alias**, or Release builds fail with `FS1118` (Debug compiles fine). `Run` members are the usual offenders. `run (build: BuildStage, buildStep: StageContext -> BuildStep)` on `StageBuilder` stays non-generic: generalizing it ties it against the flexible-signature `run (step)` overload under `FS0041` at four existing call sites (`PLAN-Execution.md`'s T8 section). Being non-generic, it composes the alias itself, so that member must also stay non-`inline`. `FS1114` ("marked inline but was not bound in optimization environment") is the failure mode of forwarding one generic member to another sibling overload rather than building its step directly — moving the shared builder settings hit it in both Debug and Release, not only in Release like `FS1118`.

Where a step's output goes is a stage setting like any other, resolved by walking `ParentContext` upward: `StageContext.getOutput` answers a `StageOutput` (`Console | Silent | Captured of OutputCapture | Redirect`), and `StageContext.writeLine ctx stream line` is the only way to emit something the stage can suppress. A bare `printfn` from inside a step is unroutable, so `echo` does not use one. `CmdRunner` redirects the child's streams whenever the sink is not `Console` (both streams, always: an undrained stderr pipe blocks the child once it fills), and on a bad exit code lifts `capture.FailureText` into the step's `Error` — stderr if the process used it, everything otherwise. `noStdRedirectForStep` overrides all of it, since routing depends on redirection. That error reaches `printError`, which percent-encodes it for the GitHub Actions annotation: a workflow command ends at its first newline, and a lifted capture is many.

Anything that kills a process on cancellation must do it from a `CancellationToken` registration, never from `Async.OnCancel`: a `Process.Kill(entireProcessTree = true)` issued from a cancellation continuation kills the child and silently leaves its grandchildren running. `CmdRunner.run` takes the ambient token from `Async.CancellationToken` — the one carrying the stage timeout — and the `cmd` test asserting no surviving process catches a regression of this.

Settings resolve by walking `ParentContext` upward (stage → parent stage → pipeline). `StageContext.mapParentContext` and its `mapStageParentContext`/`mapPipelineParentContext` specialisations are the standard way to do this; write new lookups with them rather than matching on `ParentContext` by hand.

The CEs follow Fun.Build's shape: `inline` members with `[<InlineIfLambda>]` on delegate parameters, and matched `Yield`/`Delay`/`Combine`/`For` overload sets — adding a new kind of yieldable value means adding all four. F# translates `let! x = e in rest` to `Bind(e, fun x -> «rest»)` with **no `Delay` wrapper**, so the continuation's type is whatever the body's `Yield` returned. Heterogeneous `Yield` overloads are a common source of `FS0193`.

### Command defaults

`CommandSpec.PipelineDefaults` is a `BuildPipeline` the command accumulates from its pipeline-level custom
operations, and `PipelineContext.applyDefaults` merges it into each pipeline the command runs. It applies the
defaults to a *pristine* `PipelineContext.create`, never to the finished pipeline, then copies field by field:
a `voption` setting transfers only when the pipeline left it `ValueNone`; `PostStages` only when the pipeline
declared none; `AcceptableExitCodes` only while the pipeline is still on `set [0]`; the hooks and `Verify` only
while they are still the `noStageHook`/`alwaysVerify` values `create` installed, so those are named
module-level bindings rather than inline `ignore` — reference equality is the test; and `EnvVars` per key,
since a pipeline's map starts as the whole ambient environment. `NoPrefixForStep`/`NoStdRedirectForStep` are
plain bools with no unset state, so a pipeline setting one to the value it already had is indistinguishable
from not setting it at all — the single known gap.

The merge happens in `applyTo`, once the whole `CommandSpec` is built, not in `addPipeline` as each pipeline is
yielded, so a default written below a pipeline reaches it just as one written above does. The same pass names
an unnamed pipeline after the command. Changing where either runs breaks that ordering guarantee, asserted by
`tests/Partas.Build.Tests/CommandTests.fs`'s "command pipeline defaults" list through real invocations.

### Verify CE changes against the compiler

Overload resolution in these builders fails in ways that are invisible by inspection — a catch-all overload swallowing a value, an overload that can never match, `Combine` right-associating into nested tuples. Every non-trivial claim in `notes/PLAN.md` was established by compiling a spike and printing the resulting `StageContext` tree, not by reading the code. Do the same: build a throwaway project in the scratchpad that references `src/Partas.Build`, exercise the syntax, and walk `Steps`/`Stages` to confirm the shape.

## The `Build/` CLI (this repo's own build, not the library)

`Build/Program.fs` is the whole CLI — repository paths, options, stages and commands in one file. Repository paths come from `Partas.TypeProvider.BuildHelper` (`type Repo = BuildHelperProvider<...>`, with `Repo.FileSystem` and `Repo.VirtualFileSystem` for the real and virtual file systems), so a renamed project breaks compilation instead of failing mid-release. A new packable project goes in `Project.allProjects`, which is both what `bump` can version and what `pack` packs; `Repo.Project.<name>.Path` is a compile-time constant, while `PackageId`/`AssemblyName`/`Version` are MSBuild evaluations that shell out to `dotnet msbuild -getProperty`, so keep those off any path that runs before parsing.

Since Phase 7 the CLI is written against Partas.Build (`Build.fsproj` has a project reference to `src/Partas.Build`), so it doubles as the design's acceptance test. A step is a stage; a stage that needs a flag binds it in `inputs { let! quick = Options.quick ... return stage "..." { when' (not quick); run (cmd $"dotnet ...") } }`, and the command registers it by running the pipeline that contains it. The CLI has no per-command option lists and no process wrappers. Custom operations cannot sit under an `if`/`match`, so a stage that branches builds a `Cmd` first and runs it unconditionally, and a stage that exists only when an option was supplied is yielded through `whenSome` — `ProjectManagement.publish` yields its `nuget publish` stage that way, so the stage closes over the key rather than reaching for `key.Value` under a `when'`.

The four Expecto suites run `--sequenced`. Each drives real pipelines, and a pipeline writes to one process-wide console and holds a thread in `Async.RunSynchronously` for the length of every stage — run in parallel on a two-core runner, that yields a log whose lines belong to no test in particular, with enough blocked workers that the thread pool grows one thread at a time. `Tests.execute` captures the suites' output when `--ci` is set, so a green CI run says nothing and a red one lifts the whole failure into the annotation; locally it stays live. `Tests.execute` also runs `tests/Partas.Build.CompilerProbe` twice, once per configuration, regardless of `--configuration`. Release catches an `inline` member that applies a `Build*` alias (`FS1118`), which a Debug build compiles clean.

Fake survives only where it is better than a process call: `!!` and `Shell.cleanDirs` for the clean. Everything else is a `run` step.

### Versioning

Versions live in the project files. Each packable project carries a `<Version>` (the package version) and an
`<AssemblyVersion>`; `dotnet run bump <major|minor|patch|alpha|beta|rc|preview|SEMVER> -p <project>...`
rewrites both — the second as `<major>.0.0.0`, so it only moves when the major does. Nothing on the pack path
passes a `Version` property any more: CI packs what the project file says, making the published version a
property of the commit rather than of the machine that ran the pack. `bump` is skipped when `--ci` is set, which
it is by default under GitHub Actions.

`AssemblyVersion` trails `Version`. It is the identity every already-compiled assembly references, so letting
a patch bump move it breaks anything not rebuilt in the same pass with `Could not load file or assembly
'<name>, Version=…'` — which happened when `<Version>` was first introduced.

`docs/content/RELEASE_NOTES.md` is a changelog only; no build step reads it.

## Conventions

- `.editorconfig` sets Stroustrup style, `max_line_length=150`, `fsharp_space_before_uppercase_invocation=true`. No fantomas tool is installed (`.config/dotnet-tools.json` declares no tools at all) and there is no `format`/`lint` command — match surrounding style manually.
- Prefer `voption`/`ValueOption` and `[<Struct>]` DUs in the library: a departure from the ported Fun.Build code.
- Public API goes in `[<AutoOpen>]` modules under `Partas.Build`; the model and engine stay in `Partas.Build.Internal`.
- Console output is Spectre.Console throughout, with GitHub Actions `::error title=...::` fallbacks when `GITHUB_ENV` is present (see `printError` in both context modules).
- The Nacara site (`docs/Site.fs`, `docs/docs.fsproj`) publishes every page under `docs/content/` and `docs/blog/`, plus the generated API reference, so internal working documents belong in `notes/` (as the `PLAN*.md` files do), not under `docs/`.
