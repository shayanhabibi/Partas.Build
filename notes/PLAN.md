# Plan — input binding and the Fun.Build / System.CommandLine merge

Working document. Kept at the repository root rather than under `docs/`, because
`fsdocs` renders every `.md` under `docs/` into the published site and this is
internal.

Status: **agreed in principle, not yet implemented.** To be revised before work
starts. Every claim marked *verified* was checked against the compiler.

## Goal

Let a pipeline declare the CLI inputs it needs, and let a command derive its
`System.CommandLine` option set from the pipelines it activates — so options,
validation and help text are generated from the pipeline definition instead of
being registered by hand alongside it.

## The core constraint

Collecting inputs before parsing and binding values during parsing is circular:
`GetValue` needs a `ParseResult`, which needs the option registered, which needs
the builder to have run.

Monadic binding cannot break the cycle — the set of inputs after a `let!` may
depend on the value bound, so it is not knowable statically. **Applicative**
binding can, because all sources are fixed before any value exists.

The cycle is therefore broken by construction rather than by a discovery pass.
An earlier proposal (probe-run the builder with placeholder values, register,
re-run to a fixpoint) is **rejected**: it makes stage-construction purity
load-bearing and silently misses inputs behind branches not taken.

## Verified findings

| # | Finding | Evidence |
|---|---|---|
| 1 | Current `Bind` re-parents the stage under itself, duplicating steps and `Id` | runtime: `e: 1 step` containing `e: 2 steps` |
| 2 | One `Bind` overload cannot type `let!` + a single trailing step | `FS0193 BuildStep` vs `BuildStage` |
| 3 | `Yield(_: obj)` silently discards nested stages | runtime: `c: 1 step`, nested stage absent |
| 4 | Omitting `Bind` **enforces** applicative use | `FS0708` on sequential `let!` |
| 5 | `Spec<Spec<'T>>` cannot be flattened — inner `Inputs` need a `ParseResult` | `FS0041` |
| 6 | `let!`/`and!` cannot precede a body of yielded stages; needs `return <expr>` | `FS0708` |
| 7 | `and!` has **no arity ceiling** — 14 bindings compile with only binary `MergeSources` | ran; 14 inputs collected |
| 8 | Pipeline-level declaration + command-level harvest works end to end | ran: 3 inputs registered pre-parse, 3 stages built post-parse |
| 9 | `StageBuilder.Run` could not be `inline`: applying a function-typed `BuildStage` defeats the optimiser | `FS1118` in Release only; Debug compiled fine |

(5) is why the binding site must be a **layer boundary**: one layer may bind,
and layers above may harvest. Binding at every layer is not expressible.

### The prescribed workaround for (5)

The shape that trips over (5) in practice is not a deliberate `Spec<Spec<'T>>`;
it is two blocks sharing a body. A block that binds inputs *is* a
`Spec<StageContext>`, so calling it from inside another `input { }`'s `return`
nests silently until the pipeline refuses to yield the result:

```fsharp
// `bumpImpl` is itself an `input { }`, so `return` wraps a spec inside a spec.
let bumpArgument allProjects projects = input {
    let! ci = Input.CI.isCI
    return bumpImpl (Some ci) allProjects projects
}
```

There is no flattening overload to add here, and no honest one to write: the
inner `Inputs` only exist once its `Read` has run, and `Read` needs the
`ParseResult` that those inputs configure. The absence of `Bind` is the same
constraint stated one layer up — `FS0708` on a sequential `let!` rather than an
option set that cannot be registered.

**Pass the source down; do not pass the value up.** Where two callers share a
body but differ in where a value comes from, the varying part travels *in* as a
`Spec` rather than out as one. The shared body then keeps its single
`let!`/`and!` group and the callers differ only in the spec they hand over:

```fsharp
let private bumpImpl (bumpSource: InputSpec<Bump>) allProjects (projects: ActionInput<string list>) = input {
    let! ci = Input.CI.isCI
    and! bump = bumpSource
    and! projects = projects
    return stage "bump" { ... }
}

let bumpArgument allProjects projects =
    bumpImpl (InputSpec.ofInput Argument.Versioning.bump) allProjects projects

let bumpOption allProjects projects =
    bumpImpl (InputSpec.ofInput Input.Versioning.bump |> InputSpec.map (Option.defaultValue Bump.Patch))
             allProjects projects
```

`InputsBuilder.Source(spec: InputSpec<'T>)` is what makes this need no new
library surface: a spec is already bindable on the right of `and!`. Where the
helper needs no source of its own, the degenerate form is the same rule — take
the read values as plain arguments and let the caller bind them.

Verified in a spike: `bumpArgument` registers `--ci` plus the `bump` and
`project` arguments before parsing, `bumpOption` registers `--ci`, `--bump` and
`--project`, and `bump --project a --bump minor --ci true` deactivates the stage,
so the bound values reach `when'`. `Baked.Pipelines` (`Baked.fs`) is the
worked example; `docs/composition.fsx` documents it for users.

## Design

One applicative primitive, used at every layer:

```fsharp
/// Which inputs are needed, and how to read them once parsed.
type Spec<'T> = { Inputs: ActionInput list; Read: ParseResult -> 'T }

module Spec =
    let ret v                        = { Inputs = []; Read = fun _ -> v }
    let map f s                      = { Inputs = s.Inputs; Read = s.Read >> f }
    let map2 f a b                   = { Inputs = a.Inputs @ b.Inputs
                                         Read = fun pr -> f (a.Read pr) (b.Read pr) }
    let ofInput (i: ActionInput<'T>) = { Inputs = [ i :> ActionInput ]; Read = i.GetValue }
```

`Inputs` is reachable with no `ParseResult`. That is the whole mechanism.

- **Stages are pure.** No `ActionContext`, no `ParseResult`. A stage needing a
  value is an ordinary function of it, and is testable in isolation.
- **The `inputs` CE binds.** `let!`/`and!` over `ActionInput<'T>`, producing a
  single `Spec<'T>`. Named bindings, so same-typed inputs cannot be transposed.
- **The pipeline CE harvests stages.** `Yield` accepts `StageContext` and
  `Spec<StageContext>`; `Combine` is `Spec.map2 (>>)`.
- **The command CE harvests pipelines**, registers the union of `Inputs` on its
  `Command`, then calls `Read` on the resulting `ParseResult`.

### Target API

```fsharp
// pure, reusable, no ParseResult in sight
let compileStage (cfg: string) = stage "compile" { run $"dotnet build -c {cfg}" }

let compile =
    inputs {
        let! cfg = Options.config
        and! q   = Options.quick
        return stage "compile" { run $"dotnet build -c {cfg} q={q}" }
    }

let buildPipeline =
    pipeline "build" {
        stage "restore" { run "dotnet restore" }   // declares nothing
        compile                                    // Spec<StageContext>; inputs unioned
        stage "pack" { run "dotnet pack" }
    }

command "build" {
    buildPipeline                                  // options registered from .Inputs
}
```

Rejected alternative: a bespoke `useInput { i1; i2; cfg }` CE. It reimplements
`MergeSources` by hand, binds **positionally** (transposing two `bool` inputs
compiles silently and misbehaves), and right-associating `Combine` yields
`Spec<'A * ('B * 'C)>`, needing flattening overloads to recover curried
application. `let!`/`and!` has none of these problems and no arity ceiling (7).

## Type changes

### Added

```fsharp
type Spec<'T> = { Inputs: ActionInput list; Read: ParseResult -> 'T }
```

### `Build*` aliases — drop the `ActionContext` parameter

These revert to Fun.Build's original signatures, which is the point: the
remaining port (`ConditionsBuilder`, `BuiltinCmds`, `Github`) then lands with
far less reshaping.

| Before | After |
|---|---|
| `BuildStage = ActionContext -> StageContext -> StageContext` | `StageContext -> StageContext` |
| `BuildStep = ActionContext -> StageContext -> StepIndex -> Async<Result<unit,string>>` | `StageContext -> StepIndex -> Async<Result<unit,string>>` |
| `BuildStageIsActive = ActionContext -> StageContext -> bool` | `StageContext -> bool` |
| `BuildPipeline = ActionContext -> PipelineContext -> PipelineContext` | `PipelineContext -> PipelineContext` |
| `BuildConditions = ActionContext -> ... -> ...` | drop leading `ActionContext` |
| `BuildEnvInfo = ActionContext -> EnvArg -> EnvArg` | `EnvArg -> EnvArg` |

`Step.StepFn` already carries no `ActionContext` and is unchanged.

### Field removals

- `StageContext`: drop `Inputs`, drop `Handler`.
- `PipelineContext`: drop `Inputs`, `CmdArgs`, `Handler`.
- `CommandSpec.Pipelines`: `PipelineContext list` → `Spec<PipelineContext> list`.

### Deletions

- `StageBuilder.Bind` — not needed at all once binding moves to `inputs`.
- `StageBuilder.Yield(_: obj)` — a catch-all `obj` overload converts every
  future type error into silent data loss (3). Must go regardless of the rest
  of this plan.
- Fun.Build's `CmdArg` and `whenCmd`, superseded by System.CommandLine, which
  now owns argument metadata *and* the help text Fun.Build hand-generated.
  `EnvArg`/`whenEnv` stay — env vars are not CLI inputs.

### Fixes required independently of the redesign

- `StageBuilder.Combine`/`For` over `BuildStageIsActive` are `// TODO` no-ops
  and currently drop stage conditions on the floor.
- `Run` returns a deferred stage today, so the `Yield`/`Delay`/`For`/`YieldFrom`
  overloads typed against `StageContext` never match. Purifying stages resolves
  this; verify no overload is left stranded.

## The command runner (`run "tool args"`)

Five `run`/`runSensitive` overloads in `StageBuilder` — the ones taking an exe
and args, or a `FormattableString` — are `// TODO` returning `id`, so they
silently discard the step. Filling them in is the last thing Phase 7 needs.

**Decided: no `Fake.Core.Process` dependency.** The question was whether Fake's
process utilities beat porting Fun.Build's `ProcessExtensions.fs` +
`BuiltinCmds.fs`. Fake genuinely covers three of the seven jobs a `run` step
does — `Arguments` escapes correctly per platform, `ProcessUtils.tryFindFileOnPath`
replaces Fun.Build's hand-rolled `Process.GetQualifiedFileName`, and
`CreateProcess.withOutputEvents` streams lines so a prefix can be applied while
capturing. The objection that Fake needs a Fake execution context is **false**:
spiked `CreateProcess.fromRawCommandLine "dotnet" "--version" |> Proc.run` with
no context set, and it returned cleanly with no `FakeVar` exception and no stray
trace output.

It loses on the other four. Fake has nothing for secret masking. Working
directory and environment come from walking `ParentContext` upward, which is
this library's model, not Fake's. And cancellation — the hard one — `CreateProcess`
takes no `CancellationToken`, so timeout and cancellation mean dropping to the
raw `Process` and writing the `Async.OnCancel` + platform-specific terminate
anyway; Fake's `killAllCreatedProcesses` is a global process-tracking model that
fights per-stage cancellation rather than serving it. So the dependency would put
six transitive packages (Fake.Core.{Environment,FakeVar,String,Trace},
Fake.IO.FileSystem, System.Collections.Immutable) onto a *shipped* library to
avoid ~120 lines, while leaving the ~80 subtle ones still to write —
and Fake.Core.Trace brings a console-output model duplicating the Spectre one.
`Build.fsproj` keeps its own Fake references regardless: Phase 7 can call
`DotNet.exec` inside a `run (fun ctx -> ...)` step.

**Decided: the internal representation is structured, not a string.** Fun.Build
splits a command string on the first space (quote-aware) and hands the remainder
to `ProcessStartInfo.Arguments` as an opaque blob, so quoting is the caller's
problem and platform-dependent. Instead, a `Cmd` carries an executable and a
`string list` of arguments, escaped once when the `ProcessStartInfo` is built.
The string overloads parse into it and remain a lossy convenience.

That makes `FormattableString` the *good* overload rather than a curiosity:

```fsharp
runSensitive $"docker login -u {user} -p {password}"
```

Each interpolation hole is exactly one argument — never re-split, never
re-escaped, so a value containing spaces or quotes just works — and the same
hole positions are what get replaced with `***` when the command is logged.
Escaping and masking come from one mechanism instead of two. Plain `run` on a
`FormattableString` behaves identically minus the masking.

Shape: one core runner, `StageContext -> Cmd -> Async<int>`, plus thin adapters
for `exe`, `exe + args`, `StageContext -> string`, `StageContext -> Async<string>`
and `FormattableString`.

## Goal posts

**Phase 0 — unblock the build.**
`tests/Partas.Build.Tests/Tests.fs` is template scaffolding referencing a
nonexistent `Say.hello`; the solution build and `-- test` both fail on it.
*Done when:* `dotnet run --project Build.fsproj -- test` is green.
*Status:* done. `Tests.fs` replaced by `Helpers.fs` + `InputsTests.fs` +
`StageTests.fs` + `PipelineTests.fs`, 21 tests, green.

Doing this surfaced (9): the suite builds Release, and every `stage { }` in the
library failed to compile there. Fun.Build declares `BuildStage` and friends as
**delegates** precisely so `InlineIfLambda` can fire; this port made them plain
function type aliases, and inlining an application of one is what the optimiser
choked on. `Run` is now non-`inline` — it applies a closure once at construction,
so nothing is lost — but the same trap is latent in any other CE entry point
taking a `Build*` alias.

**Decided: the `Build*` aliases stay plain function types.** Measured, Release:
building a 5-stage/25-step pipeline costs 24.3 us and 78.6 KB, paid once per
process, so `InlineIfLambda` — and therefore delegates — optimise nothing that
matters here. Delegates would buy closer parity with Fun.Build for the unported
`ConditionsBuilder`/`BuiltinCmds`/`Github`, at the cost of pushing `.Invoke`
through the `InputSpec` layer, where `map2 (>>)` and the setting mirrors are
currently one-liners. The `FS1118` trap is caged instead: `-- test` builds
Release, so it fails the suite rather than a consumer's package, and the rule is
mechanical — do not mark a CE entry member `inline` when it applies a `Build*`
alias. Revisit only if the Phase 5 conditions port fights the function types.

**Phase 1 — `Spec` + the `inputs` CE.**
`Spec` module and `InputsBuilder` (`Source` ×2, `MergeSources`, `BindReturn`,
`Return`). Binary `MergeSources` only; `MergeSources3/4/5` are an allocation
optimisation to add later, not a capability.
*Done when:* a test asserts N `and!` bindings collect N inputs before parsing,
and that `Read` returns the parsed values.
*Status:* done, covered by `InputsTests.fs`. `MergeSources3/4/5` were added
after all; deduplication happens in `InputSpec.union`, called from `map2` and
from every `MergeSources`, so it cannot be bypassed by the direct record
construction those overloads do.

**Phase 2 — purify stages.**
Drop `ActionContext` from the `Build*` aliases, remove the fields above, delete
`Bind` and `Yield(_: obj)`, fix the `BuildStageIsActive` TODOs.
*Done when:* a nested stage survives into its parent's `Steps` (regression test
for (3)), and a stage builds with no `ParseResult` anywhere in the test.
*Status:* done, covered by `StageTests.fs`. `Build*` aliases purified, `Bind`
and `Yield(_: obj)` gone, `PipelineContext.CmdArgs` dropped, and the
`BuildStageIsActive` `Combine`/`For` overloads now route through
`StageContext.buildStageIsActive`, which **conjoins** conditions onto whatever
the stage already had rather than replacing them (Fun.Build's semantics, minus
`Mode`). Spiked: condition-before-steps, condition-after-steps, two conditions
(`true && false = false`), no condition, and a condition alongside a nested
stage — all correct, nested stage retained.

**Phase 3 — `PipelineBuilder`** in `Builders.fs`: `Yield(StageContext)`,
`Yield(Spec<StageContext>)`, `Combine = Spec.map2 (>>)`, `Delay`, `Zero`, `Run`.
*Done when:* a pipeline mixing input-free and input-declaring stages reports the
correct `Inputs` before parsing and the correct stage order after.
*Status:* done in `Builders/Pipeline.fs`, covered by `PipelineTests.fs`. Implemented as an
overload pair rather than a single type: a pipeline whose stages declare nothing
stays a `PipelineContext` and needs no `ParseResult`; one `InputSpec<StageContext>`
anywhere in the body collapses the whole pipeline to `InputSpec<PipelineContext>`.
Each `Yield`/`Delay`/`Combine`/`For` therefore has an `InputSpec` twin, and so
does every `CustomOperation` — without the latter, placing a setting *after* a
declaring stage is a wall of `FS0041` rather than a no-op. Spiked: 3 inputs
harvested pre-parse from two declaring stages, stage order preserved across the
mix, re-parenting applied, and settings honoured on both sides of a declaring
stage.

**Phase 4 — `CommandBuilder`.** Harvest `Spec<PipelineContext>.Inputs`, dedupe,
register on the `Command`, invoke `Read` on the `ParseResult`.
*Done when:* `--help` lists options that no code registered by hand.
*Status:* done in `Builders/Command.fs`, covered by `CommandTests.fs`. `CommandSpec` was
reshaped (`Name`/`Aliases`/`Hidden`/`ExtraInputs`/`SubCommands`, plus the two configurations)
and its old `RootCommand` field dropped; `CommandSpec.inputs` is the harvest, deduping
`ExtraInputs` against every pipeline's. That field had to be named `ExtraInputs` rather than
`Inputs`: an `Inputs` field on both `CommandSpec` and `InputSpec` makes `{ Inputs = ...; Read = ... }`
resolve to the wrong record (`FS3566`, then `FS0039`) throughout `Types.fs` and `Pipeline.fs`.
`command` and `rootCommand` share a `CommandBuilderBase` — custom operations *are* inherited,
so only `Run` differs (`Command` vs an exit code), which also avoids hiding a base member.
A command with no pipelines gets no action, so System.CommandLine prints help for a bare
grouping node instead of silently succeeding.

Spiked end to end: a stage reading `--configuration`/`--quick` put both in `build --help`
with nothing registered by hand, and the parsed values reached the step. Two runner bugs
surfaced only once a pipeline actually ran from a command, both now fixed:
`StageContext.run` returned `stage.ContinueStepsOnFailure` as its success flag instead of
Fun.Build's `stage.ContinueStageOnFailure || isSuccess`, so every stage reported failure and
fail-fast stopped the pipeline after the first one; and the stage banner used the Spectre
colour `turquoise1`, which does not exist, throwing on the first stage of every run.

**Phase 5 — port the conditions layer** (`whenAll`/`whenAny`/`whenNot`/
`whenEnv`/`whenStage`), minus `whenCmd`.
*Status:* done in `Builders/Conditions.fs`, covered by `ConditionsTests.fs`. The
leaf conditions live in a `Conditions` module as plain `StageContext -> bool`
functions; the CEs and the `StageBuilder` operations are both thin wrappers over
them. Dropping `Mode` collapsed each one to the branch Fun.Build evaluates under
`Mode.Execution` — the help-printing and verification branches, the indent
contexts and `makeCommandOption` all disappear, which is most of the 706 lines.

Two deliberate departures. Fun.Build folds with `Seq.reduce`, which **throws** on
an empty body; this port uses `List.forall`/`List.exists`, so an empty body is the
identity of its fold — `whenAll { }` and `whenNot { }` are active, `whenAny { }` is
not. And there is no `cmdArg` operation to go with the dropped `whenCmd`: a stage
that branches on a flag binds it in an `inputs` CE and tests the bound value with
`when'`, which the tests exercise.

`whenBranch`/`whenBranches` shell out to `git branch --show-current` with a bare
`ProcessStartInfo`; Phase 6 retargets them onto `Cmd` so they inherit env vars and
cancellation. Pipeline-level condition operations are **not** ported: Fun.Build's
route through `buildPipelineVerification` into `PipelineContext.Verify`, which is
open question 3.

Two things the compiler settled. `WhenStageBuilder` inherits `StageBuilder` and
hides `Run` with a different return type (`BuildStageIsActive` rather than
`StageContext`) — that works here, unlike the base-member hiding Phase 4 avoided.
And the shorthand lambda `_.WithName name` is rejected (`FS3584`): the shorthand
only takes atomic member access, so a member with an argument needs a real lambda.

Spiked 21 checks before writing the tests: condition placement either side of the
steps, conjunction, all three folds with met/unmet/empty bodies, a `whenAny` nested
inside a `whenAll`, env-var values resolved through `ParentContext`, `whenStage` on
succeeding and failing stages, `when'` over a bound input, and the runner skipping
an inactive stage.


**Phase 6 — the command runner.** Fill in the five `// TODO` `run`/`runSensitive`
overloads per *The command runner* above: the `Cmd` type, the core
`StageContext -> Cmd -> Async<int>` runner, and the adapters.
*Done when:* a stage runs a real process, inherits the working directory and env
vars resolved through `ParentContext`, streams prefixed output, fails the stage
on a non-zero exit code, is killed by a stage timeout, and `runSensitive` logs
`***` where the interpolation holes were.
*Status:* done in `Process.fs` (`Cmd`, `Cmd.ofString`/`create`/`ofList`/
`ofFormattable`/`toLogString`, and `CmdRunner`), wired into the `run`/
`runSensitive` overloads and covered by `CmdTests.fs`. `Cmd` keeps the executable
and the arguments apart all the way to `ProcessStartInfo.ArgumentList`, so the
platform does the escaping and an interpolation hole is exactly one argument
whatever it contains.

One design defect only a spike could find: **`run $"..."` binds to the `string`
overload**, not the `FormattableString` one. An interpolated string is a `string`
unless the expected type says otherwise, and with both overloads present the
`string` one wins, silently flattening the holes and re-splitting them on
whitespace — the log printed `cmd /c type marker with space.txt` and the command
failed three times over. The `FormattableString` overload of `run` is therefore
**removed**; the structured form is reached through the `cmd` helper —
`run (cmd $"dotnet build {project}")` — which has no competing overload to lose
to. `runSensitive` keeps `FormattableString` for the same reason: it has no string
overload at all, so masking and one-hole-one-argument come from the same
mechanism.

Two departures from Fun.Build. Cancellation kills the whole process tree
(`Process.Kill(entireProcessTree = true)`) instead of asking for a graceful exit
through `CloseMainWindow`/a `SIGTERM` P/Invoke, neither of which reaches a console
child's own children. And the kill is issued from a **`CancellationToken`
registration, never from `Async.OnCancel`**: measured three ways, a tree kill
issued from a cancellation continuation kills the child and silently leaves the
grandchildren alive, so a stage that timed out on `cmd /c ping -n 30` went on
pinging for the full 30s after the pipeline had reported failure. The runner takes
the ambient token from `Async.CancellationToken` — that is the one carrying the
stage timeout — and registers on both it and the step's own token behind an
`Interlocked` guard. `CmdTests` asserts the process count is back to where it
started 1.5s after the timeout, which is the assertion that fails without this.

Two transcription bugs in `Types.fs` surfaced while reading the runner's output:
`getNoPrefixForStep` and `getNoStdRedirectForStep` both tested `not
ctx.NoPrefixForStep -> true`, inverting Fun.Build, so an explicit
`noPrefixForStep false` was ignored and redirection could never be enabled. Both
now test the flag directly and fall through to the parent, which is what the
pipeline-level flags are for.

`whenBranch`/`whenBranches` still shell out with a bare `ProcessStartInfo`;
retargeting them onto `Cmd` is a loose end, not a blocker.

**Phase 7 — dogfood.** Rewrite this repository's own `Build/` CLI on
Partas.Build, retiring `ActionPath`. This is the real acceptance test: if the
four existing commands cannot be expressed, the design is wrong.
*Status:* done. `Build/ActionPath.fs` is deleted, `Build.fsproj` drops
`FSharp.SystemCommandLine` for a project reference to `src/Partas.Build`, and
all four commands are pipelines whose stages bind their own flags. The acceptance
test passes on its own terms: `dotnet run --project Build.fsproj -- test` runs
restore → clean → build → build tests → test and exits 0, `build --help` lists
`--quick` and `--configuration` although nothing registers them, and
`build -c Bogus` still exits 1 on System.CommandLine's own validation.

Three things the rewrite settled that reading could not.

**`ActionContext` is dead here** (open question 1). Not one stage needed it: a
step that wants a flag has its *value*, bound before the pipeline was built, so
`Input.context` never appears in the new `Build/Program.fs`. It stays in the
vendored input layer for command-level handlers that are not pipelines, but
nothing in this repository uses it any more.

**A branch inside a stage has to build a value, not pick an operation.** Custom
operations cannot appear under an `if` or a `match`, so `--nuget-key` present
versus absent is decided *outside* the CE and yields a `Cmd`:
`Cmd.ofFormattable true $"… --api-key {key} …"` in one branch, `Cmd.ofString`
in the other, and the stage is `run push` either way. That is `runSensitive`'s
own mechanism reached directly, and it reads better than two near-identical
stages guarded by opposite conditions. `Documentation.generate` does the same
with a plain string for `fsdocs watch` versus `fsdocs build`.

**The Fake process wrappers went with `ActionPath`.** `Spec.fs`'s `dotnet`/
`gitCi` helpers built a `CreateProcess`, which the stage runner now supersedes —
it inherits the working directory and env vars through `ParentContext`, prefixes
the output, and is cancellable. Fake earns its place for what it is good at and
nothing else: `ReleaseNotes.load` for the version, `!!`/`Shell.cleanDirs` for
the clean, `Fake.Tools.Git` for the release helpers. `DotNet.build`/`pack`/
`nugetPush` are gone in favour of `run (cmd $"dotnet …")`, and the Expecto
suite is run as the executable it is (`dotnet run --project … --no-build`)
rather than discovered by globbing for `*.Tests.dll`.

One incidental fix: the project reference drags `FSharp.Core 10.1.400` in, so
`Build.fsproj`'s pin at `10.0.102` became an NU1605 downgrade and is now
`10.1.400`.

## Open questions for the revision session

1. **Does `ActionContext` survive?** It disappears from every `Build*` alias.
   Steps receive cancellation through the runner's token, so `Input.context`
   may become dead. Keep it only if command-level handlers need it.
2. **`PipelineContext.RemainingCmdArgs`** — Fun.Build's passthrough.
   System.CommandLine exposes `ParseResult.UnmatchedTokens`. Keep, retarget,
   or drop.
3. **Fun.Build's `Mode`** (`Execution | CommandHelp | Verification`) is not
   ported. `CommandHelp` is clearly redundant now. Is `Verification` still
   wanted, and if so what does it check? `PipelineContext.Verify` is currently
   a placeholder and `buildPipelineVerification` is commented out.
4. **Dedup key for registration.** *Settled by Phase 4.* Dedup is by
   `ActionInput` reference, so the same `let`-bound input asked for by two
   pipelines registers once; two separately created options of the same name
   stay distinct and System.CommandLine reports the clash. Two `ActionInput`
   wrappers around one `Option` would still register twice — no case for it has
   arisen. `Input.recursive` composes with this rather than competing: spiked a
   recursive option registered on the root *and* declared by a subcommand's
   stage, and it parses and reads correctly on both.
5. **Naming.** `Spec<'T>` collides conceptually with `CommandSpec` and with
   `Build/Spec.fs` in the build CLI. `Declared<'T>`, `Needs<'T>`, `Inputs<'T>`?
