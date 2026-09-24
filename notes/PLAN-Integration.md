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
- **Duplicate names**: `Input.desc`/`Input.description` (`Inputs.fs:209,215`) and `input`/`inputs`
  (`Builders/Inputs.fs:61,64`). Consumers mix them. Keep one of each, `[<Obsolete>]` the other.
- **`when' bool` carries no reason.** Add `when' (reason: string, cond: bool)` or a `whenNotFlag` for the
  `when' (not quick)` idiom, so `--explain` names the flag that skipped the stage.
- **`printfn` in Baked**: replace with `StageContext.writeLine`.

### 3.3 Finish Task 14b

`StageContext`, `PipelineContext` and `CommandSpec` still live in `Partas.Build.Internal`. Acceptance criterion
6 of `PLAN-Discoverability.md` (no consumer signature needs `Internal`) is unmet. Move the types, or add public
aliases under `Partas.Build`, then remove `open Partas.Build.Internal` from `Build/Program.fs` and the docs
pages as the check.

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
| Spectre `AnsiConsole` global | `Conductors.fs:396,504,534-549` | **unverified**: the singleton may bind to the `Console.Out` current at first use, i.e. the first eval's `StringWriter`, and write into a dead writer thereafter. Spike before designing around it. |
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

3. **Console hygiene**: set the encodings only when output is not redirected and only once per process; build
   the Spectre console per run from the current `Console.Out` (or the `output` passed to `invoke`), if §5.2's
   spike confirms the singleton problem.

4. **Bridge `Thread.Interrupt` to cancellation**: `invoke` runs the pipeline on a worker and waits; if the
   waiting thread receives `ThreadInterruptedException`, it cancels the run's `CancellationTokenSource`, which
   reuses the existing process-tree kill (registration-based, per CLAUDE.md), then rethrows. Test: interrupt a
   thread running a `sleep` stage and assert no surviving process, mirroring the existing `cmd` test.

5. **Environment read at run time**: `PipelineContext.run` refreshes the ambient environment it starts from,
   keeping the pipeline's own `envVars` on top.

6. **A reentrancy guard**: a run of a pipeline value already running either fails fast or runs against a copy
   of the mutable collections.

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

## 7. Order of work

| # | Item | Axis | Size |
|---|---|---|---|
| 1 | `.mcp.json`, `printfn` in Baked, `Cmd.argIf` in `Program.fs` | hygiene | trivial |
| 2 | Task 14b: no consumer needs `Internal` | usability | medium |
| 3 | `run (fun _ -> string)` rename/obsolete; `desc`/`input` duplicates | usability | small |
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
