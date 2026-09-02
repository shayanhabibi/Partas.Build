# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`src/Partas.Build` is a **library in early construction**. It merges three things that already exist separately in sibling repositories:

- **Fun.Build** (`../Fun.Build`) — the pipeline/stage/step computation-expression DSL and its execution engine. `Types.fs` is a port of Fun.Build's `Types.fs` + `Types.Internal.fs` + `StageContextExtensions.fs` + `PipelineContextExtensions.fs`, restructured into one file and rewritten from members-on-records into `module StageContext` / `module PipelineContext` functions using `voption` instead of `option`.
- **System.CommandLine 2.0.11** via a vendored, adapted copy of `FSharp.SystemCommandLine`'s input layer (`System.CommandLine/Inputs.fs`, `Aliases.fs`) — `ActionInput<'T>`, the `Input.xxx` combinators, `ActionContext`.
- **ActionPath** (from `../Partas.ProjectTemplates`) — the `ActionContext -> ActionContext` composable build step, where each step reads its own flags so commands are flat ordered lists rather than a dependency graph. It is where the flat-ordered-list shape came from; this repo's own `Build/` CLI used it until Phase 7 replaced it with the library.

The goal of the merge: a Fun.Build-style pipeline that *declares the CLI inputs it needs*, with commands deriving their `System.CommandLine` option set from the pipelines they activate — so options, validation and help text are generated from the pipeline definition instead of registered by hand.

Do not treat `../Fun.Build` as a dependency: it is the reference implementation being absorbed and reshaped. Consult it (and its `CLAUDE.md`) when porting a feature; do not copy its record-member style.

## Read `PLAN.md` first

`PLAN.md` is the agreed design for how input binding works and what changes in `Types.fs` and the builders, plus a running record of what each phase actually did and what the compiler proved along the way. All seven phases are implemented. Read the phase statuses before changing a builder — several of them are the reason a member looks the way it does.

The one-line version: input collection is **applicative**, not monadic. An `InputSpec<'T> = { Inputs: ActionInput list; Read: ParseResult -> 'T }` makes the input set readable without a `ParseResult`, which is what breaks the circularity between registering options and binding their values. Stages become pure (no `ActionContext`), binding happens in an `inputs { let! … and! … return … }` CE, and pipelines/commands harvest `.Inputs` upward.

## Commands

Every task goes through the `Build` CLI project, not a script:

```shell
dotnet run --project Build.fsproj -- --help
dotnet run --project Build.fsproj -- build            # restore + build
dotnet run --project Build.fsproj -- test             # build + the three Expecto suites
dotnet run --project Build.fsproj -- test --quick     # skip restores/clean
dotnet run --project Build.fsproj -- publish          # pack + push (--nuget-key, else the `local` feed)
dotnet run --project Build.fsproj -- bump [BUMP] -p <project>...   # rewrite <Version> in a project file
dotnet run --project Build.fsproj -- docs [--watch]   # fsdocs build / live serve
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

- Phases 0-7 of `PLAN.md` are done: `dotnet build src/Partas.Build` is clean and `dotnet run --project Build.fsproj -- test` is green (220 Expecto tests across three suites — 80 in `tests/Partas.Build.Tests`, one file per layer, plus `tests/Partas.Build.ExternalAnnotations.Tests` and `tests/Partas.ExternalAnnotations.Tests`). The `Build/` CLI is itself written against the library, so it is the first thing a breaking change breaks.
- The DSL exists end to end: `inputs` (`Builders/Inputs.fs`), `stage` (`Builders/Stage.fs`), `pipeline` (`Builders/Pipeline.fs`), `command`/`rootCommand` (`Builders/Command.fs`). A stage that declares an input turns its pipeline into an `InputSpec<PipelineContext>`, and the command registers whatever those specs declare. Conditions are in `Builders/Conditions.fs` — `whenAll`/`whenAny`/`whenNot`/`whenEnv`/`whenStage` plus the `when'`/`whenEnvVar`/`whenBranch`/`when{Windows,Linux,OSX}` operations on `StageBuilder`. `Builders.fs` is still an empty stub.
- A command carries `PipelineDefaults: BuildPipeline` and takes the pipeline-level operations itself (`workingDir`, `envVars`, the three timeouts, `acceptExitCodes`, the output operations, `noPrefixForStep`/`noStdRedirectForStep`, `runBeforeEachStage`/`runAfterEachStage`, `post`, `verbosity`/`verbose`/`quiet`), each one built through `CommandBuilderBase.MapPipelineDefault`. They are **defaults, not overrides**: `PipelineContext.applyDefaults` copies a setting across only where the pipeline left it at the value `PipelineContext.create` gave it, so a pipeline that sets the same thing wins. See *Command defaults* below.
- `run`/`runSensitive` start real processes through `Process.fs`: a `Cmd` keeps the executable and its arguments apart all the way to `ProcessStartInfo.ArgumentList`, so the platform does the escaping. Interpolate through the `cmd` helper — `run (cmd $"dotnet build {project}")` — because `run $"..."` binds to the `string` overload and flattens the holes; `runSensitive $"..."` takes the `FormattableString` directly and masks every hole as `***`. There is no `Fake.Core.Process` dependency; `PLAN.md`'s *The command runner* records why.
- Fun.Build's `Mode` (`Execution | CommandHelp | Verification`) has **not** been ported. `PipelineContext.Verify` is a placeholder and `buildPipelineVerification` is commented out. `CommandHelp` is redundant now that System.CommandLine generates help; whether `Verification` survives is an open question in `PLAN.md`.
- `PipelineContext.run` is complete enough to execute stages, post stages, timeouts, parallelism and cancellation.

## Architecture notes

Compile order in `Partas.Build.fsproj` matters (F#): `System.CommandLine/Inputs.fs` → `Types.fs` → `Process.fs` → `Builders/Stage.fs` → `Builders/Conditions.fs` → `Builders/Pipeline.fs` → `Builders/Inputs.fs` → `Explain.fs` → `Summary.fs` → `Builders/Command.fs` → `Baked.fs` → `Builders.fs`.

`Explain.fs` renders the resolved stage tree `--explain` prints, as text and nothing else: it writes to no
console and to no stage sink, so a stage that silences or captures its execution output is still described in
full. It compiles before `Builders/Command.fs`, whose `applyTo` registers the flag on every command and prints
what the renderer returns; it depends on nothing beyond `Types.fs` and the `Input.*` combinators. A command that
runs pipelines gets `Explain.option` and reads it in its own action. A grouping command gets
`Explain.groupingOption`, which carries the rendering — a list of the subcommands it dispatches to — on the
option's own `Action`, because such a command deliberately has no action of its own and adding one would displace
System.CommandLine's "Required command was not provided.".
Rendering evaluates every stage's `IsActive`, so a `whenBranch` starts `git` and a `whenStage` runs its condition
stage. `StageContext.Conditions` is what lets a skip name the condition that caused it: `addPredicateBecause`
writes it alongside `IsActive`, the structured conditions in `Builders/Conditions.fs` supply a reason and `when'`
supplies none, because a `bool` argument leaves nothing to report.

`Summary.fs` renders the per-stage timing table printed at the end of a run, as text and nothing else, the
way `Explain.fs` renders the tree. The timings themselves are collected in `Types.fs`: `PipelineContext.Timings`
is a `StageTimings` the stages append a `StageTiming` to as each finishes, reached by walking `ParentContext` up
to the pipeline. A stage takes an ordinal from `StageTimings.Start()` when it starts and records it alongside
its parent's, which it reads off `StageContext.TimingOrder` — the field `StageContext.run` sets on the value it
gives its sub-stages as their `ParentContext`. `Ordered` walks those pairs into a pre-order tree, so a stage sits
under its own parent however its siblings interleave under `parallel'`; a stage of the pipeline records the
parent ordinal `0L`. A condition stage takes no ordinal, and neither it nor anything under it is recorded. The
print site is `Builders/Command.fs`'s `runReportingTimings`, which prints in a `finally` so a failed run still
reports, and prints nothing for a quiet pipeline or for a run of a single stage, whose wall time the pipeline's
own line already carries. `Summary.render` sizes its three columns to the ambient console and elides what does
not fit — the middle of a stage name, the end of an outcome — so the table is one row per stage at any width and
the `Depth` indent survives an 80-column CI log.

`Baked.fs` is the batteries-included layer over everything before it: ready-made `Input.*`/`Argument.*` definitions
for the options every build CLI ends up wanting (`--configuration`, `--nuget-key`, `--project`, `--ci`, a version
bump), the semver arithmetic in `Version`, and `IO.writeVersion`/`IO.bumpVersion` for editing a project file's
`<Version>`. It is the only place in the library that writes to disk.

`Types.fs` interleaves namespaces on purpose, in this order:
1. `Partas.Build` — public `EnvArg`, pipeline exceptions.
2. `Partas.Build.Internal` — the core model. `Step` is `StepFn | StepOfStage`, which is why a nested stage *is* one step of its parent and stages nest arbitrarily. `StageParent` links a stage to a parent stage or the pipeline.
3. `Partas.Build` again — `StageContext` lookups that need `FsToolkit`/`HttpClient`.
4. `Partas.Build.Internal.Runners` — the execution engine (`StageContext.run`, `PipelineContext.run`).

The `Build*` function aliases (`BuildStage`, `BuildStep`, `BuildStageIsActive`, …) no longer take an `ActionContext`: stages are pure and the aliases match Fun.Build's originals, except that Fun.Build declares them as delegates where this port uses plain function types. That difference has one mechanical consequence — **do not mark a CE entry member `inline` when it applies a `Build*` alias**, or Release builds fail with `FS1118` (Debug compiles fine). `Run` members are the usual offenders.

Where a step's output goes is a stage setting like any other, resolved by walking `ParentContext` upward: `StageContext.getOutput` answers a `StageOutput` (`Console | Silent | Captured of OutputCapture | Redirect`) and `StageContext.writeLine ctx stream line` is the only way to emit something the stage can suppress — a bare `printfn` from inside a step is unroutable, which is why `echo` does not use one. `CmdRunner` redirects the child's streams whenever the sink is not `Console` (both of them, always: an undrained stderr pipe blocks the child once it fills), and on a bad exit code lifts `capture.FailureText` into the step's `Error` — stderr if the process used it, everything otherwise. `noStdRedirectForStep` overrides all of it, since redirection is what makes routing possible at all. That error reaches `printError`, which percent-encodes it for the GitHub Actions annotation: a workflow command ends at its first newline, and a lifted capture is many.

Anything that kills a process on cancellation must do it from a `CancellationToken` registration, never from `Async.OnCancel`: a `Process.Kill(entireProcessTree = true)` issued from a cancellation continuation kills the child and silently leaves its grandchildren running. `CmdRunner.run` takes the ambient token from `Async.CancellationToken` — that is the one carrying the stage timeout — and the `cmd` test asserting no surviving process is what catches a regression here.

Settings resolve by walking `ParentContext` upward (stage → parent stage → pipeline). `StageContext.mapParentContext` and its `mapStageParentContext`/`mapPipelineParentContext` specialisations are the standard way to do this; write new lookups with them rather than matching on `ParentContext` by hand.

The CEs follow Fun.Build's shape: `inline` members with `[<InlineIfLambda>]` on delegate parameters, and matched `Yield`/`Delay`/`Combine`/`For` overload sets — adding a new kind of yieldable value means adding all four. Note that F# translates `let! x = e in rest` to `Bind(e, fun x -> «rest»)` with **no `Delay` wrapper**, so the continuation's type is whatever the body's `Yield` returned; with heterogeneous `Yield` overloads that is a common source of `FS0193`.

### Command defaults

`CommandSpec.PipelineDefaults` is a `BuildPipeline` the command accumulates from its pipeline-level custom
operations, and `PipelineContext.applyDefaults` is what merges it into each pipeline the command runs. It applies
the defaults to a *pristine* `PipelineContext.create`, never to the finished pipeline, then copies field by
field: a `voption` setting transfers only when the pipeline left it `ValueNone`, `PostStages` only when the
pipeline declared none, `AcceptableExitCodes` only while the pipeline is still on `set [0]`, the hooks and
`Verify` only while they are still the `noStageHook`/`alwaysVerify` values `create` installed (which is why those
are named module-level bindings rather than inline `ignore` — reference equality is the test), and `EnvVars` per
key, since a pipeline's map starts as the whole ambient environment. `NoPrefixForStep`/`NoStdRedirectForStep` are
plain bools with no unset state, so a pipeline setting one to the value it already had is indistinguishable from
not setting it at all; that is the single known gap.

The merge happens in `applyTo`, once the whole `CommandSpec` is built — not in `addPipeline` as each pipeline is
yielded — so a default written below a pipeline reaches it just as one written above does. The same pass names an
unnamed pipeline after the command. Changing where either runs breaks that ordering guarantee, which
`tests/Partas.Build.Tests/CommandTests.fs`'s "command pipeline defaults" list asserts through real invocations.

### Verify CE changes against the compiler

Overload resolution in these builders fails in ways that are invisible by inspection — a catch-all overload swallowing a value, an overload that can never match, `Combine` right-associating into nested tuples. Every non-trivial claim in `PLAN.md` was established by compiling a spike and printing the resulting `StageContext` tree, not by reading the code. Do the same: build a throwaway project in the scratchpad that references `src/Partas.Build`, exercise the syntax, and walk `Steps`/`Stages` to confirm the shape.

## The `Build/` CLI (this repo's own build, not the library)

`Build/Spec.fs` holds the nouns, `Build/Program.fs` the stages and commands. Repository paths come from `Partas.TypeProvider.BuildHelper` (`type Repo = BuildHelperProvider<...>`, with `Root`/`VRoot` for the real and virtual file systems), so a renamed project breaks compilation instead of failing mid-release. A new packable project goes in `Spec.Options.Project.versioned`, which is simultaneously what `bump` can version and what `pack` packs; `Repo.Project.<name>.Path` is a compile-time constant, while `PackageId`/`AssemblyName`/`Version` are MSBuild evaluations that shell out to `dotnet msbuild -getProperty`, so keep those off any path that runs before parsing.

Since Phase 7 the CLI is written against Partas.Build (`Build.fsproj` has a project reference to `src/Partas.Build`), so it doubles as the design's acceptance test. A step is a stage; a stage that needs a flag binds it in `inputs { let! quick = Options.quick ... return stage "..." { when' (not quick); run (cmd $"dotnet ...") } }`, and the command registers it by running the pipeline that contains it. `Spec.fs` therefore has no per-command option lists and no process wrappers. Custom operations cannot sit under an `if`/`match`, so a stage that branches builds a `Cmd` first and runs it unconditionally, and a stage that exists only when an option was supplied is yielded through `whenSome` — `ProjectManagement.publish` yields its `nuget publish` stage that way, so the stage closes over the key rather than reaching for `key.Value` under a `when'`.

The three Expecto suites run `--sequenced`. Each of them drives real pipelines, and a pipeline writes to one process-wide console and holds a thread in `Async.RunSynchronously` for the length of every stage — run in parallel on a two-core runner that yields a log whose lines belong to no test in particular, and enough blocked workers that the thread pool has to grow its way out one thread at a time. `Tests.execute` also captures the suites' output when `--ci` is set, so a green CI run says nothing and a red one lifts the whole failure into the annotation; locally it stays live.

Fake survives only where it is better than a process call: `!!`/`Shell.cleanDirs` for the clean, and `Fake.Tools.Git`. Everything else is a `run` step.

`Build/TargetOperators.fs` (list-taking FAKE operators) is unused, kept for a future real dependency graph.

### Versioning

Versions live in the project files. Each packable project carries a `<Version>` (the package version) and an
`<AssemblyVersion>`, and `dotnet run bump <major|minor|patch|alpha|beta|rc|preview|SEMVER> -p <project>...`
rewrites both — the second as `<major>.0.0.0`, so it only moves when the major does. Nothing on the pack path
passes a `Version` property any more: CI packs what the project file says, which makes the published version a
property of the commit rather than of the machine that ran the pack. `bump` is skipped when `--ci` is set, which
it is by default under GitHub Actions.

`AssemblyVersion` deliberately trails `Version`. It is the identity every already-compiled assembly references,
so letting a patch bump move it breaks anything not rebuilt in the same pass with `Could not load file or
assembly '<name>, Version=…'` — which is exactly what happened when `<Version>` was first introduced here.

`docs/RELEASE_NOTES.md` is a changelog only; no build step reads it.

## Conventions

- `.editorconfig` sets Stroustrup style, `max_line_length=150`, `fsharp_space_before_uppercase_invocation=true`. No fantomas tool is installed here (`.config/dotnet-tools.json` has only `fsdocs`) and there is no `format`/`lint` command — match surrounding style manually.
- Prefer `voption`/`ValueOption` and `[<Struct>]` DUs in the library; that is the deliberate departure from the ported Fun.Build code.
- Public API goes in `[<AutoOpen>]` modules under `Partas.Build`; the model and engine stay in `Partas.Build.Internal`.
- Console output is Spectre.Console throughout, with GitHub Actions `::error title=...::` fallbacks when `GITHUB_ENV` is present (see `printError` in both context modules).
- `fsdocs` renders every `.md` under `docs/`, so internal working documents belong at the repository root (as `PLAN.md` does), not in `docs/`.
