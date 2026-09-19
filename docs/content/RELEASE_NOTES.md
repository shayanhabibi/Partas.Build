---
title: Release Notes
---

### 0.6.1-alpha.1

* Producers: `Producer.define name inputs dependencies execute` declares a typed, named unit of deferred work
  with its own CLI inputs and prerequisites. A stage requires one through
  `consumes (DependencySpec.require producer) (fun value -> ...)`, which also schedules it: the producer runs
  once per invocation and every consumer shares the result, so retrying a consumer re-runs only that consumer.
  `DependencySpec.map`/`map2`/`zip` combine several.
* `onFailure` registers a failure handler on a stage, a pipeline or a command. It runs once per failed execution
  of its scope, after that scope's retries are exhausted, and reads the failure off `FailureContext.Primary`/
  `Secondary` and a producer's published value off `FailureContext.TryGetOutput`, rather than off rendered text.
* `PipelineContext.Reports` carries a `ScopeReport` per stage: the outcome, whether the failure propagates to
  the containing scope, and a `StepFailure` per cause naming the step. A `continueStageOnFailure` reports
  `Failed` with `Propagates = false` and keeps the cause, which lets a consumer tell a failed producer from a
  skipped one.
* Three ways to run a `Cmd` from inside a step, when the step needs the result rather than the exit code:
  `execute` streams the output and fails on an exit code the stage does not accept, `executeCapture` captures
  instead and attaches the capture to the failure as evidence, and `attemptCapture` always answers a
  `CommandResult`, unaccepted exit codes included. `runOperation` adds any `Operation<unit>` as a step.
* `timeoutForStep` is each step's own budget: a step takes it when it starts and runs its whole body under it,
  so a stage of several sequential steps gives each step the full budget rather than sharing one clock.
* A timeout and a cancellation are told apart by which token fired. A stage's own `timeout`, and a
  `timeoutForStep` it gave a step, are failures of that stage and run its handlers; an ancestor's cancellation
  runs none.

### 0.5.0

* `Cmd` and the process runner ship as their own package, `Partas.Build.Cmd`, which `Partas.Build` references.
* The ready-made options, the semver arithmetic and the bump stages ship as `Partas.Build.Baked`. A script that
  used them now takes a package reference on it; they are no longer part of `Partas.Build`.
* `Cmd.run` answers a copy of its result rather than sharing one.
* `--nuget-key` accepts `--nuget` and `-k` as aliases.

### 0.4.0-alpha.3

* An empty interpolation hole yields an empty argument rather than disappearing from the command line.
* `runSensitive`'s flush no longer splits a masked hole across its delimiter.

### 0.4.0-alpha.2

* **Breaking.** `Input.mapFromAmong` and `Input.mapFromMany` replace `Input.choice` and `Input.choices`: an
  option over a fixed set of spellings, each bound to a typed value.

### 0.4.0-alpha.1

* `--explain` on every command prints the resolved stage tree and runs nothing. A grouping command lists the
  subcommands it dispatches to.
* `--version` reports the pinned Partas.Build version.
* A per-stage timing table is printed at the end of a run, nested by parentage and sized to the console. A quiet
  pipeline, and a run of a single stage, print none.
* `retry` on a stage runs its steps again, up to `count` further attempts. The stage's `timeout` is the budget
  for the whole stage, retries included.
* Each parallel branch buffers its output and flushes it as one block, so interleaved steps stay readable.
* A step's label carries the command line it runs, which is what `--explain` shows for it.

### 0.3.0

* Command-level pipeline defaults: `workingDir`, `envVars`, the three timeouts, `acceptExitCodes`, the output
  operations, `post`, `runBeforeEachStage`/`runAfterEachStage` and `verbosity`/`verbose`/`quiet` are available on
  `command` and `rootCommand`. They are defaults, not overrides — a pipeline that sets the same thing wins.
* `InputSpec<'T>` is published under `Partas.Build`, so a block can take one as a parameter.
* `whenSome` and `whenOk` yield a stage only when the value is present, binding it for the stage to close over.
* `Cmd.argIf` and `Cmd.argWhenSome` add an argument to a command line conditionally.
* `name` on `rootCommand` sets what the root command calls itself, and script arguments are sliced by default.

### 0.2.0 - 0.2.3

* Bump stages in `Baked`: the version bump taken as a positional argument, or as `--bump`.
* Overload fixes on `InputSpec`.

### 0.1.5

* Stage-level output sinks: `silentOutput`, `captureOutput`, `redirectOutput` and `outputTo` on both the stage
  and pipeline builders. A captured stage prints nothing and lifts what it held — stderr if the process wrote
  any, everything otherwise — into the error message when a step fails.
* `StageContext.writeLine ctx stream line` is the routable way for a step to emit output; `echo` now uses it.
* Error messages are percent-encoded into GitHub Actions annotations, so a multi-line failure survives one.
* A step that failed with something to say reported nothing: the runner matched its error with an inverted
  guard, printing only the empty ones. Fixed, and covered by a test that records the console.
* `Partas.Build.ExternalAnnotations` packs its MSBuild logic as `build/Partas.Build.ExternalAnnotations.targets`.
  NuGet only auto-imports `build/$(PackageId).targets`, so under its own name it imported nothing (NU5129).
* Both of a redirected child's streams are always drained, which removes a pipe-buffer deadlock.

### 0.1.4

* `bump` command: `dotnet run --project Build.fsproj -- bump <major|minor|patch|alpha|beta|rc|preview|SEMVER> -p <project>...`
  rewrites `<Version>` and `<AssemblyVersion>` in the target project files.
* Versions now live in the project files. Nothing on the pack path passes a version property, so a published
  package carries whatever the committed project file says. This file is a changelog only; no build step reads it.
* `Baked.fs`: ready-made inputs (`--configuration`, `--nuget-key`, `--project`, `--ci`, `--bump`), semver
  arithmetic under `Version`, and `IO.writeVersion`/`IO.bumpVersion`.

### 0.1.3

* Initial release.
