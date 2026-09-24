# PLAN-Integration

Findings from a survey of how Partas.Build is used in practice, and the design that follows. Drafted
2026-09-24. **Status: proposal, partly implemented — see the status line under each section.**

Companion to `PLAN-Discoverability.md`, which responded to `FEEDBACK-Xantham.md` (a report written against
0.3.0). This document starts from the consumers' *code* instead of a written report, and adds a third axis the
earlier plans do not cover: running builds inside a long-lived F# host, specifically SageFs.

Three axes, in priority order:

1. **Usability**: the boilerplate every consumer rewrites, and the API traps they fall into.
2. **Agent discoverability**: what an agent can learn by running a command, as opposed to reading docs.
3. **SageFs integration**: making a pipeline safe and useful to invoke repeatedly from a warm FSI session.

## 1. Consumers surveyed

| Consumer | Form | Partas.Build version |
|---|---|---|
| Xantham `build.fsx` | `dotnet fsi` script, `#r "nuget: …"` | 0.4.0-alpha.3 |
| Xantham `tools/generate-wire.fsx`, `tools/xantham-fixtures.fsx` | `dotnet fsi` scripts, invoked from `build.fsx` as child processes | 0.3.0 |
| Xantham `tools/workspace.fsx` | `#load`-only helper | — |
| `Build/Program.fs` (this repo) | project reference | current |
| `Partas.Build.ExternalAnnotations` / `Partas.ExternalAnnotations.Tool` | library stages + dotnet tool | current |

The library is at 0.6.5. Xantham has not upgraded, so several of its workarounds are already solved upstream
(`Cmd.argWhenSome`/`argIf`, `mapFromAmong`, the renamed Baked API). Upgrading is the cheapest item in this
plan; it is listed under §6 because it is Xantham's to do.

## 2. Patterns common to every consumer

Established by reading the consumers, not by inference.

### Hand-rolled in both `Build/Program.fs` and Xantham `build.fsx`

| Pattern | Xantham | Partas.Build |
|---|---|---|
| Restore: `dotnet restore <sln>` + `dotnet tool restore`, gated on `--quick` | `build.fsx:177-189` | `Program.fs:96-103` |
| Clean: `bin` directories through Fake `Shell.cleanDirs` | `build.fsx:204-220` | `Program.fs:104-119` (plus `*.nupkg`) |
| `--quick`/`-q`, `--skip-tests`, `--watch` option declarations | `build.fsx` `Options` | `Program.fs` `Options` |
| Configuration defaulting to Release: `InputSpec.ofInput \|> InputSpec.map (Option.defaultValue "Release")` | `build.fsx:78-81` | `Program.fs:55-58` |
| Build/pack per project: `dotnet build {p} -c {config} -v q`, `dotnet pack --no-build --no-restore -o bin` | `build.fsx:222-238, 531-553` | `Program.fs:122-146` |
| Run an Expecto suite | hard-coded `bin/{config}/net10.0/{Name}.dll` (`build.fsx:397-419`) — breaks on a TFM change | `dotnet run --no-build -- --summary --sequenced` |
| NuGet push: local feed vs nuget.org, `--skip-duplicate`, `bin/*.nupkg` | `build.fsx:555-568` | `Program.fs:147-168` |
| Docs: `dotnet run --project <site> -- watch\|build` | `build.fsx:240-267` | `Program.fs:208-218` |
| Split src/test projects by string-matching the path | `build.fsx:54-55` | `Program.fs:62-72` |

### Constructs recurring across consumers

- **`if` inside a stage to choose its shape** (docs watch vs build, `npm ci` vs `npm install`): the known
  "custom operations cannot sit under `if`/`match`" limitation, worked around by building a `Cmd` first.
- **`when' (not quick)`** is the most common condition by far. It reports no reason under `--explain`.
- **Loops generating stages**: `for project in projects do stage $"pack-{project.Name}" { … }`.
- **`open Partas.Build.Internal`** in consumer code (`Program.fs:18`, `xantham-fixtures.fsx:113`) and in the
  docs pages (`docs/content/index.fsx:50`, `docs/content/Build/*.fsx`). Task 14b of
  `PLAN-Discoverability-Tasks.md` is still open.
- **Raw `printfn` inside steps**, bypassing `quiet` and capture: Xantham `findings`, `generate-wire.fsx`,
  `workspace.fsx`, and **Baked's own `bumpImpl`** in `src/Partas.Build.Baked/SemVer.fs`.
- **Side effects while binding inputs**: Xantham's `Workspace.ensureTsc` runs inside `input { }` before
  `return` — it sets process environment variables and prints, and so runs under `--help` and `--explain`.

### Unused by every consumer

`onFailure`, `retry`, `timeout`, `continueStageOnFailure`, producers/`consumes`. Xantham also never uses
`pipeline` or `description`. The execution model of `PLAN-Execution.md` has no consumer yet; its first real
user will likely be whatever §3.1's prefab stages need.

## 3. Usability

### 3.1 Prefab stages in Baked

Every row of the first table in §2 becomes a function in `Partas.Build.Baked`, taking `InputSpec`s so it
composes with the consumer's own options:

| Proposed | Absorbs |
|---|---|
| `Baked.Common.quick`, `skipTests`, `watch` | the three duplicated option declarations |
| `Baked.Dotnet.configOrRelease : InputSpec<string>` | the `ofInput \|> map (defaultValue "Release")` chain |
| `Baked.Stages.restore (sln)` | restore + tool restore, skipped by `quick` |
| `Baked.Stages.clean (dirs)` | Fake `Shell.cleanDirs`; removes the Fake dependency and its `FakeExecutionContext` setup (`Program.fs:21-22`) |
| `Baked.Stages.build (projects)`, `pack (projects, outDir)` | per-project build/pack under `parallel'` |
| `Baked.Stages.expecto (project, filter)` | resolves the test executable through MSBuild `TargetPath` or `dotnet run --no-build`, never a hand-built path; passes `--summary` under `--ci` and captures output there |
| `Baked.Stages.nugetPush (key, source, glob)` | built on `whenSome` + `runSensitive`, so the key is always masked and never read through `.Value` |
| `Baked.Stages.fantomas`, `npmInstall` | format and npm install |

`nugetPush` is the most important of these: Xantham's hand-written push (§6) leaks the key into the local log.

Open question: whether prefabs are functions returning `InputSpec<StageContext>` or records with overridable
fields (W12 in `FEEDBACK-Xantham.md`, deferred). Functions first; records only if a consumer needs to patch
one prefab field.

### 3.2 API traps

- **`run (fun ctx -> "…")` executes the returned string as a command line** (`StageSettings.fs:363`). A lambda
  written to return a message becomes a process launch. Rename to `runLine`, or mark the `string`-returning
  overload `[<Obsolete>]` in favour of `run (fun ctx -> cmd …)`. Check that the rename does not disturb the
  `FS0041` balance recorded in CLAUDE.md for `run (build, buildStep)`.
  **Status: implemented on `claude/partas-build-patterns-jgrwr0-api-traps`.** Both: the six `run` overloads over
  `StageContext -> string`, `Async<string>`, `Task<string>` and their `option` forms are `[<Obsolete>]` (they keep
  working), and `runLine` carries the same six under a name that says what the string is. The `option` forms are
  included, a deviation from the plan's "the `string`-returning overload": `fun _ -> Some "…"` is the same trap.
  `runLine` is a separate operation, so `run`'s overload set is unchanged and the `FS0041` balance with it;
  compiler-established in a scratch project over every call-site shape (annotated and bare lambdas, each return
  type, `fun _ -> None`, `fun ctx -> failwith …`, the `BuildStep`-returning lambda, a `runLine` after an
  input-declaring sub-stage) and by `tests/Partas.Build.CompilerProbe` in Debug and Release. `Obsolete` does not
  take part in overload resolution: the warning fires only where an obsolete overload is the one chosen. No
  project in the repository sets `TreatWarningsAsErrors`; the call sites were migrated anyway.
- **Duplicate names**: `Input.desc`/`Input.description` (`Inputs.fs:209,215`) and `input`/`inputs`
  (`Builders/Inputs.fs:61,64`). Consumers mix them. Keep one of each, `[<Obsolete>]` the other.
  **Status: implemented on `claude/partas-build-patterns-jgrwr0-api-traps`.** `Input.description` and `input` are
  kept: `description` matches System.CommandLine's `Description` and the `description` operation of `command` and
  `pipeline`; `inputs` was already `[<Obsolete>]`. Every `Input.desc` in `src/`, `Build/`, `Partas.Build.ExternalAnnotations/`,
  `README.md` and `docs/content/` now reads `Input.description`. Deviation: `docs/blog/` and `notes/` keep
  `Input.desc` — they are dated records of the API at the time they were written.
- **`when' bool` carries no reason.** Add `when' (reason: string, cond: bool)` or a `whenNotFlag` for the
  `when' (not quick)` idiom, so `--explain` names the flag that skipped the stage.
  **Status: implemented on `claude/partas-build-patterns-jgrwr0-api-traps`.** `when' value "reason"` on `stage`
  (and its `InputSpec` mirror), recorded through `addPredicateBecause`: `when' (not quick) "--quick is set"` renders
  as `(skipped: --quick is set)`. Deviation: the condition comes first, so a reason is appended to an existing
  `when'` rather than wrapped around it; CE custom operations take their arguments space-separated, so the plan's
  tupled `when' (reason, cond)` was never the call-site shape. `ConditionsBuilder`'s `when'` is unchanged: a
  `whenAll`/`whenAny`/`whenNot` records no per-leaf reason. `Build/Program.fs` gives every `when'` a reason.
- **`printfn` in Baked**: replace with `StageContext.writeLine`. **Status: implemented on `claude/partas-build-patterns-jgrwr0-int-usability`**
  (`bumpImpl`; the only `printfn` in Baked), with a test in `tests/Partas.Build.Tests/BakedTests.fs`.

### 3.3 Finish Task 14b

`StageContext`, `PipelineContext` and `CommandSpec` still live in `Partas.Build.Internal`. Acceptance criterion
6 of `PLAN-Discoverability.md` (no consumer signature needs `Internal`) is unmet. Move the types, or add public
aliases under `Partas.Build`, then remove `open Partas.Build.Internal` from `Build/Program.fs` and the docs
pages as the check.

**Status: implemented on `claude/partas-build-patterns-jgrwr0-int-usability`.** Public aliases, not a move: an
`[<AutoOpen>] module ConsumerTypes` at the end of `Conductors.fs` abbreviates `StageContext`, `PipelineContext`,
`CommandSpec`, `StageParent`, `RuntimeContext`, `StepIndex`/`stepIndex` and every `Build*` alias (`BuildStageIsActive` and `BuildConditions` included). Moving the
records would change their full names, a binary break for already-compiled consumers; an abbreviation erases to the
same type, so code that opens `Internal` is unaffected. Compiler-established: record construction annotated as
`CommandSpec`, `{ ctx with … }`, matching the struct DU `StageParent.Pipeline` and `Operation` over
`RuntimeContext` all work through the abbreviations (`tests/Partas.Build.Tests/ConsumerSurfaceTests.fs`, which
opens only `Partas.Build`). The `StageContext` functions a step calls (`writeLine`, `getOutput`, `getVerbosity`,
`getNamePath`) lived only in `Internal`'s module; the public `Partas.Build.StageContext` module re-exports them.
`open Partas.Build.Internal` is gone from `Build/Program.fs`, `docs/content/index.fsx` and
`docs/content/Build/*.fsx`; the docs pages type-check under `dotnet fsi`. `Build/Program.fs` was compiled as a
scratch copy with the `Repo` type provider stubbed (the provider does not load in the container that did the work).

## 4. Agent discoverability

`PLAN-Discoverability.md`'s principle stands: an agent will run a command before it reads a document. The
commands exist (`--help`, `--explain`, `--version`, the summary table). Their output is for a human.

### 4.1 Machine-readable output

Everything an agent can observe is text or a Spectre table, and `Summary.render` elides cells to fit the
console width. Add `--format json` (name open) on every command:

- **`--explain --format json`**: the resolved stage tree, step labels, skip reasons, producers.
- **The run result**: `PipelineContext.Reports` (`ScopeReports`) and `Timings` already hold everything; they
  only need a serializer. Emit on stdout at the end of the run, or to a file named by `--report <path>`, so it
  does not interleave with step output.
- **`--schema`** (or a hidden `describe` command): the command tree with every option's name, aliases, type,
  default, choices and description. System.CommandLine has this information; it is only rendered as help.

**Status: implemented on claude/partas-build-patterns-jgrwr0-json.** `MachineOutput.fs`, `Explain.toJson`,
`RunResult.toJson`. Decisions and deviations:

- The flag is `--json` (§8 Q1), a plain `bool`, on every command; `--schema` is on every command and prints the
  subtree rooted at the command it is given to; `--report <path>` is on every command that runs pipelines.
  A command that already declares one of these names or aliases keeps its own option and goes without the
  library's (`reserve` in `Builders/Command.fs`): collision is resolved at construction, silently, rather than
  by System.CommandLine rejecting a duplicate. A stage that reads the library's own `MachineOutput.json` or
  `MachineOutput.report` registers it through its pipeline's inputs; `reserve` then finds it taken and the command
  declares it once.
- Every step position in the JSON counts from zero: `index` in the explain document, `step` and `path` in the
  run result, so a failure maps onto the explained step directly. The text form of `--explain` still prints an
  unlabelled step as `step 1`.
- The run result goes to stdout under `--json` *and* to a file under `--report` (§8 Q2): stdout as one line of
  compact JSON after the run, replacing the timing table, so a reader takes the last line; the file indented.
  Stage output is untouched, since capturing it is the stage's setting.
- Every document carries `formatVersion: 1`. JSON is written with `Utf8JsonWriter` directly rather than by
  reflection over the F# records, which do not serialize `voption`/DUs usefully. `System.Text.Json` is a package
  reference (8.0.5) on `netstandard2.0` only; `net8.0`/`net10.0` use the shared framework's.
- `choices` is what System.CommandLine offers for completion: `acceptOnlyFromAmong`, `mapFromAmong`,
  `addCompletions` and enums all appear there; booleans list none. A default whose factory reads its parse is
  reported absent.
- A default can be a secret (`Baked.NuGet.apiKey` defaults to `NUGET_API_KEY`). `Input.sensitive` marks the
  option; `--schema` then writes `"sensitive": true` and a present default as `"***"` (`null` stays `null`, so a
  consumer can still tell a secret is configured). Masking is opt-in: a default is a plain value, and nothing on
  it says where it came from. Text `--help` still prints the default, as it did before this branch.
- Not covered: a System.CommandLine parse error (unknown option, missing subcommand) happens before any command
  action, so it writes no JSON — the exit code (2) is the machine-readable part.

### 4.2 Exit codes

Today a parse/validation error and a stage failure both exit 1 (`Command.fs:93-110`). Proposed: `0` success,
`1` stage failure, `2` usage/validation error, `130` cancelled. An agent can then tell "I called it wrong" from
"the build is broken" without parsing stderr.

**Status: implemented on claude/partas-build-patterns-jgrwr0-int-core.** `ExitCode.Success`/`Failure`/`UsageError`/`Cancelled` in `RunResult.fs`.
Usage errors are a System.CommandLine parse error (unknown option, missing subcommand, failed validator) and a
failed `DependencyPlan.validate`. The parse-error mapping lives in `RootCommandDefinition.Invoke`, the one path
`rootCommand` and `Command.invoke` share; a subcommand invoked straight through System.CommandLine
(`cmd.Parse(…).Invoke()`, as many tests do) still exits 1 on a parse error, since System.CommandLine's own
`ParseErrorAction` answers it — but 2 on a failed dependency validation, which the command action returns.

### 4.3 `--explain` without side effects

Rendering evaluates every `IsActive`: `whenBranch` starts `git`, and `whenStage` runs its condition stage —
twice, per the handoff's deferred findings. There is also no exception guard. Proposed: a static mode (default
under `--format json`) that reports a structured condition as `unevaluated: whenBranch "main"` instead of
running it, and a guard that renders a failed condition as its exception message.

**Status: implemented on claude/partas-build-patterns-jgrwr0-json.** `ExplainMode.Evaluated | Static`;
`--explain` (text) stays evaluated, `--explain --json` is static; `Explain.renderWith`/`toJson` take the mode.
The double evaluation was confirmed — `status` called `IsActive`, then `skipReason` re-ran each reasoned
condition of a skipped stage, so a failing `when' stage` ran its condition stage twice — and is fixed: explain
walks `StageContext.Conditions` itself, each condition at most once, short-circuiting as `IsActive` does. That
changes one edge: the skip reason is now the *first* failing condition's (possibly none), where it used to be the
first failing condition that carried a reason. A throwing condition renders `condition failed: <message>`
(JSON `status: "error"`). Side-effecting conditions are recognised by a marker type, `EffectfulCondition`, made
through the public `Conditions.effectful description condition` so consumers can mark their own; a closure-keyed
weak table was tried first and lost the mark to inlining and to call-site eta-expansion (see CLAUDE.md).
`Conditions.whenBranches` now takes `#seq<string>` for the same reason — source-compatible, binary-breaking.

### 4.4 XML `<example>`s

There are none in `src` (Task 15's audit was never done). Priority targets: the `run` overloads, `input { }`,
`whenSome`, `Cmd.argWhenSome`/`argIf`, `stage`/`pipeline`/`command`. These are what an agent hovers or greps.

### 4.5 Point agents at what exists

Neither Xantham's `AGENTS.md` nor its `.claude/rules` mention `--help` or `--explain`, and Xantham gives no
command a `description`.

- Ship an agent snippet with the package (a `SKILL.md` or an `AGENTS.md` block under `docs/static/`, linked from
  `llms.txt`): "run `<command> --explain` before invoking; `--help` is generated from the stages and is
  authoritative".
- Promote the undescribed-command list printed under `--explain` to a warning on every run, or at least under
  `--ci`.
- Check whether `llms-full.txt` survived the move from fsdocs to Nacara; it is not in `docs/static`.

## 5. SageFs integration

SageFs (https://github.com/WillEhrendreich/SageFs) is a long-running F# Interactive daemon: one worker process
per session holding a warm `FsiEvaluationSession` over a loaded project, with hot reload, live testing and an
MCP server. Agents evaluate code through `send_fsharp_code`. It has **no plugin mechanism** — its MCP tools are
fixed — so the integration surface is F# code evaluated in a session, plus the per-session startup script
`.SageFs/init.fsx` (or `.SageFsrc`), run at the end of warm-up.

The payoff is large for fsi-script consumers: a full Xantham build today cold-compiles five `dotnet fsi`
processes (`build.fsx` plus four `run "dotnet fsi tools/… -- …"` steps), each with its own NuGet resolution.
A warm session pays that once.

### 5.1 Host behaviour that matters

From SageFs's source (`SageFs.Core/AppState.fs`, `Features/LiveTestingExecutors.fs`, `docs/`):

- **stdout**: each eval swaps `Console.Out` for a `StringWriter`, restores it afterwards, and strips all ANSI
  sequences from what it captured.
- **Cancellation**: `cancel_eval` calls `Thread.Interrupt()` on the eval thread. No `CancellationToken` is
  signalled. A thread blocked in `Async.RunSynchronously` receives `ThreadInterruptedException`.
- **Statics** live for the life of the worker; only `hard_reset_fsi_session` resets them. There is no
  `[<EntryPoint>]` and no meaningful argv.
- **Live testing** discovers Expecto tests, with a 5 s default per-test timeout; a timed-out test reports as
  Skipped.

### 5.2 What breaks in Partas.Build today

| Hazard | Where | Effect in a warm session |
|---|---|---|
| `rootCommandOfScript` is a module-level value calling `Args.script ()` | `Command.fs:599` | argv read once at module initialisation, and it is the host's argv |
| `Console.InputEncoding`/`OutputEncoding` set on every run | `Conductors.Runners.fs:629-630` | process-global mutation per eval; setting `InputEncoding` can throw without an attached console on Windows |
| Spectre `AnsiConsole` global | `Conductors.fs:396,504,534-549` | **confirmed** (§8 Q5): the singleton binds to the `Console.Out` current at first use, i.e. the first eval's `StringWriter`, and writes into a dead writer thereafter. |
| Environment snapshot in `PipelineContext.create` | `Conductors.fs:420` | a pipeline value bound at load time keeps the environment as it was then |
| Mutable `Timings`/`Reports`/`Producers` on the pipeline value, cleared per run | `Conductors.Runners.fs:631-635` | two concurrent runs of the same value race |
| Cancellation only through `CancellationToken` | `Process.fs` (`CmdRunner`) | `cancel_eval`'s `Thread.Interrupt` reaches no token: child process trees are orphaned |
| No structured result from the public API | `RootCommandBuilder.Run` returns `int` | an agent in the session gets an exit code and scraped, ANSI-stripped text |

### 5.3 Proposed changes

1. **`Command.invoke`**: an argv-free entry point for hosts.

   ```fsharp
   type RunResult = { ExitCode: int; Reports: ScopeReports; Timings: StageTiming list }
   Command.invoke : args: string list -> ?output: TextWriter -> ?cancellationToken: CancellationToken
                    -> RootCommandBuilder/CommandSpec -> RunResult
   ```

   It parses `args`, runs, and returns the structured result that §4.1 serializes. `RootCommandBuilder.Run`
   becomes `invoke` plus `ExitCode`.

   **Status: implemented on claude/partas-build-patterns-jgrwr0-int-core.** Deviations from the sketch above:

   - `invoke` takes a `RootCommandDefinition`, built by `Command.root { … }` — `rootCommand`'s operations, no
     argv, nothing parsed or run at construction (§8 Q4). `Command.invoke : string seq -> RootCommandDefinition
     -> RunResult` is the common case; the optional parameters live on the method
     `RootCommandDefinition.Invoke(args, ?output, ?error, ?cancellationToken)`, since a let-bound function takes
     none. `RootCommandBuilder.Run` is `Define` + `Invoke` + `.ExitCode`.
   - `RunResult = { ExitCode; Outcome: RunOutcome; Pipelines: PipelineRun list }`, with `Reports`/`Timings`/
     `Failures` members flattening across pipelines. A command runs several pipelines, and `ScopeReports`/
     `StageTimings` are the mutable per-pipeline collectors a second run clears, so each `PipelineRun` holds an
     immutable snapshot (`ScopeReport list`, pre-ordered `StageTiming list`) taken as the pipeline finishes.
   - `output` reaches what goes through System.CommandLine's `InvocationConfiguration` — help, `--version`,
     parse errors — plus `--explain`, the timing summary and the dependency diagnostic, which now write there
     too. Stage output still goes to Spectre's ambient console: rerouting it is §5.3.3's.
   - `cancellationToken` reaches the engine through the new `PipelineContext.runWith`, which links it into the
     pipeline's own timeout source; a cancelled invocation exits 130 and starts no further pipeline. The
     `Thread.Interrupt` bridge (§5.3.4) is the seam left: it needs only to cancel a source whose token it passes
     to `Invoke`.

2. **`rootCommandOfScript` stops capturing argv at module initialisation**: a function, or a builder whose
   `Run` reads `Args.script ()` when invoked.

   **Status: implemented on claude/partas-build-patterns-jgrwr0-host.** `RootCommandBuilder`'s primary constructor takes `unit -> string array`,
   called once per `Run`; `new(args: string array)` keeps `rootCommand argv { … }`, and `rootCommandOfScript`
   is `RootCommandBuilder Args.script`, still a value, so `rootCommandOfScript { … }` is unchanged. `rootCommand`
   gained a `string array` annotation: with two constructors, the unannotated inline `RootCommandBuilder args`
   is `FS0041`.

3. **Console hygiene**: set the encodings only when output is not redirected and only once per process; build
   the Spectre console per run from the current `Console.Out` (or the `output` passed to `invoke`), if §5.2's
   spike confirms the singleton problem.

   **Status: implemented on claude/partas-build-patterns-jgrwr0-host.** The spike confirmed the hazard (§8 Q5). `Terminal.fs` is now the only route to
   the console. Deviations:

   - Not a console *per run* but one per `Console.Out`: `Terminal.ansi ()` rebinds `AnsiConsole.Console` to the
     current `Console.Out` whenever `Console.Out` changed since its last call, which covers a writer swapped
     between runs and one swapped mid-run. It keeps a console something else assigned to `AnsiConsole.Console`
     since the last call (a user's, or the tests' `capturingOut`). Residual gap: when the first call the
     library ever makes finds a static console created earlier under a different `Console.Out` (another
     library used `AnsiConsole` in a previous SageFs eval), it keeps that console until `Console.Out` next
     changes.
   - Found in review: following `Console.Out` can deadlock under a host whose `Console.Out` takes a lock of its
     own and then writes to the real terminal. On Unix the runtime locks `Console.Out` for every terminal write,
     so Expecto's logger (Expecto lock, then `Console.Out`) and a pipeline thread writing through Expecto's
     `FuncTextWriter` (`Console.Out`, then Expecto lock) invert. The Release suite under a pseudo-terminal hung
     in 3 of 7 runs. `Console.WriteLine` from any second thread carries the same hazard, so the library cannot
     remove it while it follows `Console.Out`; SageFs's `StringWriter`s are not exposed. Two changes: each
     `Console.Out` writer keeps the console it was first seen with (a writer restored after a swap gets its
     console back rather than a new one over itself), and the test host pins `AnsiConsole.Console` to the real
     stdout before Expecto starts. A host with a writer like Expecto's passes `output` to `invoke` or pins the
     console the same way.
   - `invoke`'s `output`, when given, is an `AsyncLocal` run writer (`Terminal.withOutput`): the pipeline's own
     lines, `Console`-sink step lines, and — by forcing redirection in `CmdRunner.outputPolicy` — a
     `Console`-sink child process's output go there, as plain text. `withOutput` wraps the writer in
     `TextWriter.Synchronized`: `parallel'` stages and a redirected child's two reader callbacks write to it at
     once, and a caller's `StringWriter` is not thread-safe.
   - Found by the spike: without a terminal (SageFs's worker, a CI container) Spectre reports width `-1` and
     `MarkupLine` renders nothing at all, while tables and rules throw "Console width must be greater than
     zero". The library's consoles fall back to 80 columns (`Terminal.FallbackWidth`), and `Summary.render`
     sizes to `Terminal.width ()`.
   - `ensureUtf8` sets each encoding at most once per process, only for a stream that is not redirected, and
     swallows a failure. Setting `OutputEncoding` does not replace a `Console.Out` installed by `SetOut`.

4. **Bridge `Thread.Interrupt` to cancellation**: `invoke` runs the pipeline on a worker and waits; if the
   waiting thread receives `ThreadInterruptedException`, it cancels the run's `CancellationTokenSource`, which
   reuses the existing process-tree kill (registration-based, per CLAUDE.md), then rethrows. Test: interrupt a
   thread running a `sleep` stage and assert no surviving process, mirroring the existing `cmd` test.

   **Status: implemented on claude/partas-build-patterns-jgrwr0-host.** `RootCommandDefinition.Invoke` runs parse-and-invoke on a `LongRunning`
   task; the calling thread waits. On `ThreadInterruptedException` it cancels a `CancellationTokenSource`
   linked from the caller's `cancellationToken` (the one the invocation state carries), waits up to five
   seconds for the run to wind down, and rethrows; `Invoke` then returns no `RunResult`. The cmd test tracks the
   sleep processes it started by id and ignores zombies: in this container nothing reaps an orphaned grandchild,
   so a killed `sleep` lingers `<defunct>` and the existing count-based no-survivor tests fail here on the base
   branch too. The linked source is disposed in a `finally`, so a failed or interrupted invocation leaves no
   registration on a caller's long-lived token.

5. **Environment read at run time**: `PipelineContext.run` refreshes the ambient environment it starts from,
   keeping the pipeline's own `envVars` on top.

   **Status: implemented on claude/partas-build-patterns-jgrwr0-host.** `PipelineContext.create` now starts `EnvVars` empty — the variables the
   pipeline sets — and `runWith` layers them over the environment read as the run starts. `applyDefaults`'
   per-key rule becomes "a default key applies where the pipeline did not set that key", which also closes the
   old gap where a pipeline setting a key to its ambient value was indistinguishable from not setting it.
   Outside a run, `StageContext.tryGetEnvVar` (the `Partas.Build` one) falls back to the process environment
   and `PipelineContext.printError` checks it for `GITHUB_ENV`, since the pipeline's map no longer holds it.

6. **A reentrancy guard**: a run of a pipeline value already running either fails fast or runs against a copy
   of the mutable collections.

   **Status: implemented on claude/partas-build-patterns-jgrwr0-host.** Fail fast: `runWith` raises `InvalidOperationException` naming the pipeline,
   before clearing anything, when the same value (identified by the `ScopeReports` its record copies share) is
   already running. `PipelineContext.withRunState` gives a copy fresh collections that runs alongside. The
   command path does not isolate automatically: tests and callers read `pipeline.Reports` off the value a
   command ran, and `ExecutionSchedule.schedule` closes over `pipeline.Producers`, so a copy would have to be
   taken before scheduling and would hide the run from those readers. Two concurrent invocations reaching the
   same pipeline value therefore fail the second (System.CommandLine's exception handler, exit 1); an
   input-aware pipeline builds a fresh value per invocation and is unaffected. The command path enters the
   guard (`PipelineContext.Running.enter`) before the `try` whose `finally` records the run into the
   `RunResult` and prints the timing summary, then runs through `runEntered`: a refused invocation records no
   pipeline run and prints nothing of the run holding the value.

### 5.4 Consumer-side pattern (docs, not library code)

Document the shape for an fsi-script build that is SageFs-friendly:

- Split `build.fsx` into `build.defs.fsx` (options, stages, the `rootCommand` value, no side effects) and a
  thin `build.fsx` that `#load`s it and calls `exit (… )`.
- `.SageFs/init.fsx` `#load`s `build.defs.fsx` and binds `let build args = Command.invoke args Build.root`.
  An agent then calls `build ["test"; "--quick"]` through `send_fsharp_code` and reads a `RunResult`.
- Mount other scripts' commands by `#load` + `addCommand` instead of `run "dotnet fsi …"` (the W1 answer in
  `PLAN-Discoverability.md` §2 already says this works).
- Tag this repo's process-spawning tests (the `cmd` suite, `CompilerProbe`) so SageFs live testing classifies
  them as Integration rather than timing them out at 5 s.

### 5.5 Out of scope

A SageFs plugin or MCP tool of our own: SageFs composes its providers statically and has no runtime extension
point. If that changes, `--schema` (§4.1) and `Command.invoke` (§5.3) are the pieces such a tool would wrap.

## 6. Findings in consumers (not this repo's to fix)

Recorded so they are not lost; each belongs to its own repository.

**Xantham**

- **Tool scripts swallow failures.** `tools/generate-wire.fsx:308` and `tools/xantham-fixtures.fsx:186` end in
  a bare `rootCommand … { }` with no `exit (…)`, so the exit code is discarded and `build.fsx`'s
  `run "dotnet fsi tools/…"` steps succeed when the tool failed. Verified in source.
- **`publish` leaks the NuGet key** (`build.fsx:555-567`): plain `run $"… -k {apiKey.Value} …"` under
  `when' apiKey.IsSome`; should be `whenSome` + `runSensitive` (or §3.1's `nugetPush`). `failIfIgnored` fires
  only after restore → pack have run.
- **Version drift**: 0.4.0-alpha.3 / 0.3.0 against 0.6.5.
- `build.fsx:354-361` runs `powershell Move-Item/Remove-Item`; not portable to Linux CI.
- `build.fsx:232-236` has two equivalent branches, one evaluating `projects[0]` at construction time.
- `.github/workflows/push_master.yml` path filters exclude `*.fsx` and `build.fsx`: CI never runs on a build
  change. `docs.yml` builds Release, then runs `build.fsx -- docs`, which cleans and rebuilds.
- A non-quick run always runs `format` (`dotnet fantomas .`), rewriting the working tree — hostile to an agent
  that runs `test` to check its work. Separate `--no-format` from `--quick`.

**This repo, outside the library**

- `.mcp.json` points `fslangmcp` at `Partas.Testing.slnx`; the solution is `Partas.Build.slnx`.
- `Build/Program.fs:38-41` declares `formatFiles` and never uses it.
- `Program.fs:197,201` uses `Cmd.ofString $"""… {if ci then "--summary" else null} …"""`; `Cmd.argIf` is the
  intended form.
- `PLAN-Execution.md`'s header still reads "implementation not started".

**Status: all four implemented on `claude/partas-build-patterns-jgrwr0-int-usability`.** `Program.fs` builds the Expecto arguments through
`cmd $"… --" |> Cmd.argIf ci [ "--summary" ] |> Cmd.args [ … ]`. `PLAN-Execution-Tasks.md`'s own "Planning
only" status line and its unchecked T4/T5 boxes are equally stale and were left alone: whether each box is done
needs checking against the code.

## 7. Order of work

| # | Item | Axis | Size |
|---|---|---|---|
| 1 | `.mcp.json`, `printfn` in Baked, `Cmd.argIf` in `Program.fs` — **done** | hygiene | trivial |
| 2 | Task 14b: no consumer needs `Internal` — **done** | usability | medium |
| 3 | `run (fun _ -> string)` rename/obsolete; `desc`/`input` duplicates — **done** | usability | small |
| 4 | Exit codes (§4.2) | discoverability | small |
| 5 | `Command.invoke` + `RunResult` (§5.3.1) | SageFs, discoverability | medium |
| 6 | JSON for `--explain`, the run result, `--schema` (§4.1) — serializes 5's result — **done** | discoverability | medium |
| 7 | Console hygiene, argv capture, env at run time (§5.3.2-5.3.5) — after the Spectre spike | SageFs | medium |
| 8 | Thread-interrupt bridge + no-orphan test (§5.3.4) | SageFs | medium |
| 9 | Baked prefab stages (§3.1), `Build/Program.fs` rewritten on them as the acceptance test | usability | large |
| 10 | Side-effect-free `--explain` (§4.3) — **done** | discoverability | medium |
| 11 | XML `<example>`s, agent snippet, consumer pattern docs (§4.4, §4.5, §5.4) | discoverability | medium |

`Build/Program.fs` stays the acceptance test, as in `PLAN.md`: items 2, 3 and 9 are done when it compiles
without `open Partas.Build.Internal` and without the hand-rolled rows of §2's first table.

## 8. Open questions

1. JSON flag naming: `--format json` on every command, or `--json`? Does it collide with a consumer's own option
   (System.CommandLine rejects duplicate aliases at construction)?
   **Decided:** `--json`. `--format` is a likely name for a consumer's own code-formatting option; a bare flag
   also needs no value parsing. A command that declares either name keeps its own and goes without the library's.
2. Should §4.1's run result go to stdout or only to `--report <path>`? Stdout is simpler for agents but mixes with
   step output that is not captured.
   **Decided:** both. `--json` puts it on stdout as the last line, one line of compact JSON; `--report <path>`
   writes it, indented, to a file, with or without `--json`.
3. Prefab stages as functions or records (§3.1, W12).
4. Does `Command.invoke` take the `RootCommandBuilder` result, the `CommandSpec`, or both — and does it live in
   `Partas.Build` or a separate `Partas.Build.Hosting` namespace?
   **Decided:** neither. `rootCommand args { … }` runs at construction and `CommandSpec` lives in `Internal`, so
   `invoke` takes a third value, the `RootCommandDefinition` that `Command.root { … }` builds. It lives in
   `Partas.Build` (the `CommandBuilder` auto-open module, beside `Command.pipeline`), with `RunResult`,
   `RunOutcome`, `PipelineRun` and `ExitCode` in `Partas.Build` too: one entry point does not warrant a
   namespace, and none of it needs `open Partas.Build.Internal`.
5. Is the Spectre singleton hazard (§5.2) real? Spike: create `AnsiConsole` under one `Console.SetOut`, swap,
   write again, and see where the text lands.
   **Answered: yes.** Spectre.Console 0.57.2, net10.0: with `Console.SetOut a`, `AnsiConsole.MarkupLine "first"`,
   then `Console.SetOut b`, `AnsiConsole.MarkupLine "second"` and `Console.WriteLine "plain"`, `a` held
   `first\nsecond\n` and `b` held only `plain\n` — the static console stays on the writer current at its first
   use. The same spike showed that with no terminal attached, every Spectre console reports width `-1` and
   renders `MarkupLine` as nothing, the real stdout included; under a pseudo-terminal it is 80. Addressed in
   §5.3.3.
