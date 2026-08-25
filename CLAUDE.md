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
```

Flags sit on the commands whose stages read them, not on the root: `--quick` (skip restores and the clean), `--skip-tests`, `--configuration Debug|Release`. `<command> --help` is generated from those stages and is the authority on which command takes what.

For a single test, run the Expecto suite directly:

```shell
dotnet run --project tests/Partas.Build.Tests -- --filter-test-case "conditions conjoin rather than replace"
dotnet run --project tests/Partas.Build.Tests -- --list-tests
```

Fast inner loop while working on the library only: `dotnet build src/Partas.Build`.

## Current state (verify before assuming)

- Phases 0-7 of `PLAN.md` are done: `dotnet build src/Partas.Build` is clean and `dotnet run --project Build.fsproj -- test` is green (209 Expecto tests across three suites — 67 in `tests/Partas.Build.Tests`, one file per layer, plus `tests/Partas.Build.ExternalAnnotations.Tests` and `tests/Partas.ExternalAnnotations.Tests`). The `Build/` CLI is itself written against the library, so it is the first thing a breaking change breaks.
- The DSL exists end to end: `inputs` (`Builders/Inputs.fs`), `stage` (`Builders/Stage.fs`), `pipeline` (`Builders/Pipeline.fs`), `command`/`rootCommand` (`Builders/Command.fs`). A stage that declares an input turns its pipeline into an `InputSpec<PipelineContext>`, and the command registers whatever those specs declare. Conditions are in `Builders/Conditions.fs` — `whenAll`/`whenAny`/`whenNot`/`whenEnv`/`whenStage` plus the `when'`/`whenEnvVar`/`whenBranch`/`when{Windows,Linux,OSX}` operations on `StageBuilder`. `Builders.fs` is still an empty stub.
- `run`/`runSensitive` start real processes through `Process.fs`: a `Cmd` keeps the executable and its arguments apart all the way to `ProcessStartInfo.ArgumentList`, so the platform does the escaping. Interpolate through the `cmd` helper — `run (cmd $"dotnet build {project}")` — because `run $"..."` binds to the `string` overload and flattens the holes; `runSensitive $"..."` takes the `FormattableString` directly and masks every hole as `***`. There is no `Fake.Core.Process` dependency; `PLAN.md`'s *The command runner* records why.
- Fun.Build's `Mode` (`Execution | CommandHelp | Verification`) has **not** been ported. `PipelineContext.Verify` is a placeholder and `buildPipelineVerification` is commented out. `CommandHelp` is redundant now that System.CommandLine generates help; whether `Verification` survives is an open question in `PLAN.md`.
- `PipelineContext.run` is complete enough to execute stages, post stages, timeouts, parallelism and cancellation.

## Architecture notes

Compile order in `Partas.Build.fsproj` matters (F#): `System.CommandLine/Inputs.fs` → `Types.fs` → `Process.fs` → `Builders/Stage.fs` → `Builders/Conditions.fs` → `Builders/Pipeline.fs` → `Builders/Inputs.fs` → `Builders/Command.fs` → `Baked.fs` → `Builders.fs`.

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

Anything that kills a process on cancellation must do it from a `CancellationToken` registration, never from `Async.OnCancel`: a `Process.Kill(entireProcessTree = true)` issued from a cancellation continuation kills the child and silently leaves its grandchildren running. `CmdRunner.run` takes the ambient token from `Async.CancellationToken` — that is the one carrying the stage timeout — and the `cmd` test asserting no surviving process is what catches a regression here.

Settings resolve by walking `ParentContext` upward (stage → parent stage → pipeline). `StageContext.mapParentContext` and its `mapStageParentContext`/`mapPipelineParentContext` specialisations are the standard way to do this; write new lookups with them rather than matching on `ParentContext` by hand.

The CEs follow Fun.Build's shape: `inline` members with `[<InlineIfLambda>]` on delegate parameters, and matched `Yield`/`Delay`/`Combine`/`For` overload sets — adding a new kind of yieldable value means adding all four. Note that F# translates `let! x = e in rest` to `Bind(e, fun x -> «rest»)` with **no `Delay` wrapper**, so the continuation's type is whatever the body's `Yield` returned; with heterogeneous `Yield` overloads that is a common source of `FS0193`.

### Verify CE changes against the compiler

Overload resolution in these builders fails in ways that are invisible by inspection — a catch-all overload swallowing a value, an overload that can never match, `Combine` right-associating into nested tuples. Every non-trivial claim in `PLAN.md` was established by compiling a spike and printing the resulting `StageContext` tree, not by reading the code. Do the same: build a throwaway project in the scratchpad that references `src/Partas.Build`, exercise the syntax, and walk `Steps`/`Stages` to confirm the shape.

## The `Build/` CLI (this repo's own build, not the library)

`Build/Spec.fs` holds the nouns, `Build/Program.fs` the stages and commands. Repository paths come from `Partas.TypeProvider.BuildHelper` (`type Repo = BuildHelperProvider<...>`, with `Root`/`VRoot` for the real and virtual file systems), so a renamed project breaks compilation instead of failing mid-release. A new packable project goes in `Spec.Options.Project.versioned`, which is simultaneously what `bump` can version and what `pack` packs; `Repo.Project.<name>.Path` is a compile-time constant, while `PackageId`/`AssemblyName`/`Version` are MSBuild evaluations that shell out to `dotnet msbuild -getProperty`, so keep those off any path that runs before parsing.

Since Phase 7 the CLI is written against Partas.Build (`Build.fsproj` has a project reference to `src/Partas.Build`), so it doubles as the design's acceptance test. A step is a stage; a stage that needs a flag binds it in `inputs { let! quick = Options.quick ... return stage "..." { when' (not quick); run (cmd $"dotnet ...") } }`, and the command registers it by running the pipeline that contains it. `Spec.fs` therefore has no per-command option lists and no process wrappers. Custom operations cannot sit under an `if`/`match`, so a stage that branches builds a `Cmd` first and runs it unconditionally — `ProjectManagement.publish` picks `Cmd.ofFormattable true` (masking, as `runSensitive` does) when `--nuget-key` is present and `Cmd.ofString` when it is not.

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
