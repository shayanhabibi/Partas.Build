# PLAN-Discoverability

Design for the response to `FEEDBACK-Xantham.md`. Agreed 2026-09-02.

Companion to `PLAN.md`, which records how input binding works. This document records what changes as a
result of the first external consumer report, and — more importantly — why roughly half of that report
needed no code at all.

## 1. The diagnosis

`FEEDBACK-Xantham.md` is a consumer report written deliberately without reading this library's source:
README, IntelliSense, and four scripts' worth of scar tissue. That method is the finding. It contains two
failure modes wearing the same clothes, and they must not be treated alike.

**Features that exist and were never found.** The cost was a latent goldens-corruption bug (§2.5), a
twenty-line option declaration (§2.3), and four unnecessary process launches per `generate` run (§2.7).
None of these needed new code.

**Features that genuinely do not exist.** `--explain`, `retry`, `whenSome`, `Input.choices`, a root command
`name`.

The report's own summary asks for W1, W2 and W3 as its top three. **W1 and W3 already exist.** Shipping code
alone would build two things that are already built and still leave the next consumer to rediscover nothing.

### The principle that follows

An agent cannot be made to read documentation. It can be made to run a command and read the answer.

So the primary surface is **runtime self-description**: the library must be able to say what it is about to
do. Every documentation artifact in this plan is a fallback for when that is not enough. This inverts the
usual ordering, and it is the reason Pillar A leads.

## 2. Triage — evidence

Established by reading the source at `src/Partas.Build`, not by inference.

### Already built; the report is a discoverability failure

| Report item | What exists | Where |
|---|---|---|
| W1 mount commands across scripts | `Yield(subCommand: Command)`, `addCommand`, `addCommands`. A `#load`ed script binding `let generateCommands = command "generate" { … }` grafts in today. | `Builders/Command.fs:106,124,201` |
| W3 stage-scoped `env` | `envVars` on a stage, inherited by nested stages, applied to `ProcessStartInfo.Environment`. It never mutates the host process, so there is nothing to restore and §2.5's bug is unwritable with it. | `Builders/Stage.fs:613`, `Process.fs:240` |
| W5a secret inputs | `runSensitive`, `Cmd.secret`, `Cmd.sensitive`; every hole masked `***` wherever the library echoes. | `Process.fs:27,41` |
| W6b print output only on failure | `captureOutput` — holds output back and lifts it into the error message if a step fails. Exactly the "third mode" the report asks for. | `Builders/Stage.fs:353-361` |
| W8b `Input.parseWith` | `Input.tryParse`, returning `Result<'T, string>` as a parse diagnostic. | `System.CommandLine/Inputs.fs` |
| W4a `exe name [args]` | `Cmd.ofList`. | `Process.fs:121` |
| §2.8 blank root description | `description` works on `rootCommand`, inherited from `CommandBuilderBase`. Only the *name* is unreachable. | `Builders/Command.fs:160` |
| §2.9 `workingDir` inheritance | Inherits, by the `ParentContext` walk. | `Types.fs`, `StageContext.getWorkingDir` |
| §2.11 / §4.4 `pipeline` optional | `command { stage; stage }` is first-class; `pipelineOfCommandStages` wraps it. | `Builders/Command.fs:380` |
| §4.2 enumerations in prose | `acceptOnlyFromAmong`, `helpName`, `Input.hidden`. | `System.CommandLine/Inputs.fs` |
| W6a per-stage timings | Measured and printed in verbose output. Only the end-of-run *summary* is missing. | `Types.fs:732,789,909,919` |
| W7 (partial) | The `InputSpec` **module** is already under `Partas.Build`. Only the **type** is stranded in `Internal`. | `Types.fs:94` vs `Types.fs:500` |

### Genuinely missing

W2 `--explain`; W9 root command `name`; W10 `retry`; W4b conditional command-line flags; W5b a condition
that binds the value it tested; W6a the run summary table; W8a an option over a known set of typed values;
W7 the type's namespace; W11 buffered output under `parallel'`; W13's `rootCommand` args default and
`--version`.

## 3. Pillar A — the library describes itself

### A1. `--explain`

Registered automatically on every command by the library. Opt-in would be one more undiscoverable feature,
which is the failure this whole plan exists to answer. The cost is a reserved flag name across every CLI
built with Partas.Build; that is accepted.

After parsing, `CommandSpec.Pipelines` can be read against the `ParseResult` to materialise every
`PipelineContext` without running anything. `--explain` walks the resulting stage tree, evaluates each
stage's `IsActive`, and prints the tree instead of executing it. Exit code 0.

Two honesty constraints are part of the design, not concessions to it:

**Step rendering.** `Step` gains a label: `StepFn of label: string voption * fn: …`. `run "literal"` and
`run (cmd $"…")` populate it from `Cmd.toLogString`, which already masks secrets — so `--explain` is safe on
a publish command by construction, and W5's "redact in `--explain` output" is satisfied by the same mechanism
that satisfies it everywhere else. `run (fun ctx -> …)` carries `ValueNone` and renders as `<step N>`. The
tree shows what it knows and does not invent what it does not.

This is a breaking change to `Step`, which lives in `Partas.Build.Internal`. Acceptable.

**Skip reasons.** The report's mockup shows `skipped (--quick)`. `when' (not quick)` is an opaque `bool`;
there is no expression text to recover, and a reason field that guessed would be a field that lies.
Therefore: the structured conditions (`whenEnvVar`, `whenBranch`, `whenWindows`, `whenLinux`, `whenOSX`,
`whenStage`) report a real reason; bare `when'` reports `skipped` with no reason. The 80% that is honest.

Note that `whenBranch` shells out to git. Evaluating conditions during `--explain` therefore performs
read-only IO. This is acceptable and must be documented.

`--explain` additionally lists any command reachable from the root that has no description. This converts
W13's "warn on an undescribed command" into a report, rather than a breaking change to every existing script
— including the seventeen undescribed commands in the reporting repository.

### A2. `--version`

Reports the Partas.Build version the script is pinned to, so a bug report names a version. Also
auto-registered on the root command.

### A3. Run summary

At the end of every run, unconditionally: per-stage wall time and a clear statement of which stage failed
and with what. The timings are already measured; only the summary rendering is missing. This is the table
every build-script optimisation starts from, and its absence is why the reporting repository measured its
own build with a stopwatch.

## 4. Pillar B — close the gaps that make wrong code the easy code

Ordered by *prevents a bug* ahead of *saves keystrokes*.

### B1. `Input.choices` (W8a)

```fsharp
Input.choices<'T>     : string -> (string * 'T) list -> ActionInput<'T>
Input.choicesMany<'T> : string -> (string * 'T) list -> ActionInput<'T list>
Input.caseInsensitive : ActionInput<'T> -> ActionInput<'T>
```

Built on the existing `acceptOnlyFromAmong` for validation and completions, and `tryParse` for the typed
mapping. Replaces §2.3 — twenty lines that map names to paths and paths back to records, restate their
default twice, and throw an unhandled `KeyNotFoundException` on an unrecognised token. The failure mode
becomes a `System.CommandLine` validation message.

### B2. `whenSome` (W5b)

A plain function, not a custom operation — custom operations cannot sit under an `if`, and the shape needed
here produces a stage rather than modifying one:

```fsharp
whenSome : 'a option -> ('a -> StageContext) -> StageContext
whenOk   : Result<'a, 'b> -> ('a -> StageContext) -> StageContext
```

The returned stage is inactive when `None`/`Error`. This removes `.Value` under `when' x.IsSome` from the
one stage where a mistake is public and permanent, where today correctness rests on `when'`'s evaluation
order and a refactor that moves `when'` below the `run` compiles fine and throws in CI mid-release.

### B3. Conditional command lines (W4b)

```fsharp
Cmd.arg         : string -> Cmd -> Cmd
Cmd.args        : string list -> Cmd -> Cmd
Cmd.argIf       : bool -> string list -> Cmd -> Cmd
Cmd.argWhenSome : 'a option -> ('a -> string list) -> Cmd -> Cmd
Cmd.secretArg   : string -> Cmd -> Cmd
Cmd.secretArgIf : bool -> string list -> Cmd -> Cmd
```

Masking survives composition, which is why the secret variants exist rather than leaving callers to
reconstruct a `Secrets` set by index.

`cmd`'s quoting rule is correct and stays. The problem it creates is that "add a flag conditionally" — the
most common edit anyone makes to a command line — is expressible only by duplicating the whole line, so
three optional flags means eight branches and people abandon `cmd` for unquoted strings. That is the exact
failure the quoting existed to prevent. These combinators defend the quoting.

### B4. `retry` (W10)

Stage-level `retry n`, composing with the existing `timeout`. Network-touching stages hang rather than fail,
and a hung CI job costs a runner-hour and tells you nothing.

### B5. Root command identity (W9)

`name` on `rootCommand`, plus a default derived from the script filename — so the common case is fixed with
no API adopted at all, which matters for scripts that will never be edited again. Today `RootCommand()`
takes the executable name, and every script built this way ships help text calling the program `fsi`.

`usage` turned out to need the custom help action: `System.CommandLine` 2.0.11's `Command.Name` has no
setter (get-only on the base `Symbol` type, confirmed by reflecting on the installed assembly), and
`RootCommand`'s only constructor takes a `description`, not a `name`. The usage line is rewritten instead by
replacing the root's `--help` option's `Action` with one that runs the built-in `HelpAction` against a buffer,
substitutes the resolved name for the command's own, and only then writes the result — see
`nameHelpOutput` in `Command.fs`. `System.CommandLine.Help.HelpBuilder`/`HelpContext` are `internal` in this
version, which is why the fix works by post-processing rendered text rather than by customizing the help
layout directly.

### B6. `InputSpec<'T>` leaves `Internal` (W7)

Move the type from `Partas.Build.Internal` to `Partas.Build`. The module is already there. This deletes a
load-bearing `open Partas.Build.Internal` from a script that runs in the reporting repository's CI, and
removes the reasonable question every reader of that script currently has about whether they are using an
unsupported API.

The `Input` / `InputSpec` split itself stays — it is the applicative structure the whole design rests on
(`PLAN.md`, finding 5). What changes is that the seam gets a documented rule for which combinator lives on
which side, replacing the rote `|> InputSpec.ofInput |> InputSpec.map …` of §2.2.

### B7. `rootCommand` without the args slice (W13)

Default the args to everything after the first `--` in `Environment.GetCommandLineArgs()`.
`fsi.CommandLineArgs[1..]` appears in all three of the reporting repository's scripts and is pure ceremony.

### B8. Buffered output under `parallel'` (W11)

Buffer each parallel branch's output and flush it as a block on completion, so `parallel' 4` produces
readable logs rather than four interleaved npm installs.

## 5. Pillar C — documentation that reaches a consumer holding only the package

`docs/` contains roughly 2,500 lines covering `runSensitive`, `workingDir`, `envVars`, `InputSpec` and
`acceptOnlyFromAmong`. The consumer found none of it, because the package ships `README.md` and XML docs and
points at nothing else. The reachable surface was a README and autocomplete on names they did not know to
type.

- **README rewrite.** Lead with the option model — declared at the point of use, registered automatically,
  deduplicated by construction — which the report calls the best idea in the library and says should be what
  the README leads with. Demote `pipeline` out of the headline example: `command { stage; stage }` is what
  people actually write, and leading with `pipeline` cost one reader a year of wondering what they were
  missing.
- **Capability map** (`docs/CAPABILITIES.md`): every `[<CustomOperation>]` and every `Input` combinator, one
  line each. This single artifact would have prevented most of the report.
- **"Did you look for this?" table** in the README, keyed to the report's actual wrong turns: *"I need an
  env var for one stage"* to `envVars`; *"my command line contains a secret"* to `runSensitive`; *"I want a
  stage's output only if it fails"* to `captureOutput`; *"I want another script's commands"* to `#load` and
  yielding the `Command` value; *"my option has a fixed set of values"* to `Input.choices`.
- **`llms.txt` and `llms-full.txt`** published at the docs site root, so an agent handed a package name can
  be pointed at one URL.
- **XML doc audit.** IntelliSense is the surface the consumer actually used, making it the highest-traffic
  documentation in the project. Every feature this report proves was undiscoverable gets an `<example>`.
- **Cross-script composition guide** — the real W1 answer: bind the command tree as a value, gate the
  `rootCommand` invocation so a `#load` does not execute it, then `#load` and yield.

## 6. Pillar D — anti-wrongness machinery

The standing answer to "how do we prevent future agents being wrong", beyond this round of fixes:

- **Every documented claim gets an Expecto test.** The suite has 220 tests across three suites; doc claims
  join them, so a documentation statement cannot rot into a lie silently.
- **`--explain` is the escape hatch when documentation fails.** An agent that cannot find an API can still
  see the truth about the tree it built. This is the durable part: it degrades gracefully as the library
  grows past whatever the docs last said.
- **The capability map is reviewed against the actual `[<CustomOperation>]` set**, so a new operation absent
  from it is a review catch rather than a discovery three releases later.

## 7. Out of scope

- **W12 — prefabs as composable records.** `Baked.Pipelines.bumpArgument` takes two same-typed positional
  list arguments that are easy to transpose and say nothing at the call site. The ask is a named record and
  the ability to lift one stage out of a prefab. Deferred: it reshapes a layer with one prefab in real use,
  and it reads better once `--explain` can show what a prefab does. Revisit after this plan lands.
- **W1's weaker version** — a stage that declares it delegates to command *X* of script *Y*. The strong
  version already works; it needs documenting, not building.
- **Merging `Input` and `InputSpec`.** Only the namespace moves. Merging the types would dissolve the
  applicative structure that makes input collection work at all.

## 8. Sequencing and team

F# compile order couples these files, and `Types.fs` is touched by several workstreams. Naive parallelism
would be a merge disaster. Three waves, worktree-isolated:

**Wave 1, parallel.**
- *Inputs and command-line ergonomics* — B1, B2, B3, B5, B6, B7. Touches `System.CommandLine/Inputs.fs`,
  `Process.fs`, `Builders/Conditions.fs`, `Builders/Command.fs`. Disjoint from the execution engine.
- *Documentation of the already-resolved items* — the Pillar C work describing features that already exist.
  Needs no new code and can start immediately.

**Wave 2, parallel, after Wave 1 lands.**
- *Self-description* — A1, A2, A3. Requires the `Step` label change, hence the dependency.
- *Execution robustness* — B4 `retry`, B8 parallel buffering. `Builders/Stage.fs` and the runners.

**Wave 3.**
- Documentation second pass covering the new API; capability map completed against the final operation set.
- Verification: the full Expecto suite, and the acceptance test that matters — `dotnet run --project
  Build.fsproj -- test`. The `Build/` CLI is written against the library and is the first thing a breaking
  change breaks.

## 9. Acceptance criteria

1. `dotnet build src/Partas.Build` is clean in Debug **and Release** — Release because `FS1118` on an
   `inline` CE entry member applying a `Build*` alias appears only there.
2. `dotnet run --project Build.fsproj -- test` is green, with new tests covering every item in Pillars A
   and B.
3. `dotnet run --project Build.fsproj -- test --explain` prints the resolved stage tree, runs nothing, and
   exits 0.
4. Every §2 sore point in `FEEDBACK-Xantham.md` is answered by either a shipped API or a named documentation
   location — with no entry answered by "it was always there" alone.
5. `docs/CAPABILITIES.md` lists every `[<CustomOperation>]` and `Input` combinator in the library.
6. No public signature in a consumer script needs to name `Partas.Build.Internal`.
