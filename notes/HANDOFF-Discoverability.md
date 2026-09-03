# Handoff — the discoverability branch

Status as of pausing. Branch `feedback/discoverability`, 30 commits ahead of `master`, nothing pushed.

## What this is

`PLAN-Discoverability.md` is the spec, `PLAN-Discoverability-Tasks.md` the implementation plan, both answering
`FEEDBACK-Xantham.md` — a consumer's report of what Partas.Build made them guess at. The plan is being executed under
`superpowers:subagent-driven-development`: a fresh implementer per task, a spec-and-quality review after each, fix
rounds until clean.

**The ledger is the recovery map**, not this file: `.superpowers/sdd/PLAN-Discoverability-Tasks/progress.md`. It
carries every task's outcome, all 32 rulings, and the deferred-findings pile. It is git-ignored, so `git clean -fdx`
destroys it. This document summarises it; the ledger is authoritative where they disagree.

The plan shipped with 16 tasks. It now has 17 — Ruling 28 inserted Task 14b. See *Task status*.

## Where the work stopped

**Task 14 of 17, fix round 2 committed as `0b41a22`. Its re-review was stopped mid-flight and is partial.**

The working tree is clean; the untracked files at the repo root (`PLAN.md`, `NOTES-ComputationExpressions.md`,
`PLAN-ExternalAnnotations.md`, `Partas.Build.sln.DotSettings.user`) predate this branch and are unrelated, and this
document is the fifth. No temporary worktrees survive — the last reviewer removed and pruned its own.

Everything through Task 13 is committed, reviewed and green.

### The partial re-review, and what it leaves

Findings at `.superpowers/sdd/PLAN-Discoverability-Tasks/task-14-findings-r3.md`, labelled partial. Every item is
classified, which is the point of reading it before resuming:

**Established by measurement — closed:**

- **Important 3** (§B8's defect had returned for `run (cmd …)` steps) is fixed. `Process.fs:326-327` now reads
  `(not noPrefix || not toConsole || stepBuffer.IsSome) && not (getNoStdRedirectForStep ctx)`. Probe at `0b41a22`,
  `parallel' 4` over four real child processes with the default console sink: four contiguous blocks, all 20 lines,
  none lost — against round-robin soup with 2 lines lost at `c6cbf48`.
- **Important 4** (a failing parallel step's annotation carried the wrong step's output) is fixed. `parallel' 2` +
  `captureOutput`, sibling exiting first: the error carries the failing step's own lines and none of the sibling's,
  while the shared capture demonstrably held the sibling's first.
- **`noStdRedirectForStep` still wins.** The new clause sits inside the first disjunction; the opt-out conjoins over
  all three. With the opt-out set, the probe gives round-robin.
- **Important 1's double print has not returned**, now that redirection is on for steps that previously were not
  redirected.

**Established by reading:** Minor 4 (`Types.fs:405-409`) is factually correct now, keeping one rule-2 "so" inference.
Minor 5 is addressed at all three sites (`Types.fs:1008`, `1010-1011`, `1029-1030`); a rule-5 deixis remains at
`1010-1011`.

**Not reached — this is the resume list:**

1. Task 13's `"a retry under parallel' leaves a concurrent sibling's lines in the shared capture"` was not re-run.
2. The `parallelism` suite was not re-run.
3. The two new `OutputTests.fs` tests were not shown red against `c6cbf48`.
4. **Whether the committed test satisfies Ruling 31's test *shape* is unadjudicated.** `OutputTests.fs:84-92` uses a
   real child process and a console sink, but sets `StepBuffer` by hand rather than obtaining it from a `parallel'`
   stage. Ruling 31 asked for the latter. This is a controller decision, not a reviewer one.
5. No systematic new-breakage hunt beyond the four probes; no out-of-scope observations collected.

One open question the reviewer surfaced and did not adjudicate: `Process.fs:324-325` says `noStdRedirectForStep`
"makes capture impossible, by design" but does not state the `parallel'` consequence — with the opt-out set,
buffering cannot happen and the output interleaves. Decide whether that gets a sentence.

### How Task 14 got here

`d8ff7f9` buffered each parallel step's output and flushed it under a lock. Two fix rounds followed, each catching a
regression the previous one introduced:

| Commit | What it fixed | What the next round found |
|---|---|---|
| `d8ff7f9` | §B8 — interleaved parallel output | overwriting `Output` made every parallel step look `Captured`, so a failing step printed twice |
| `c6cbf48` | moved the buffer to a `StepBuffer` field, leaving `Output` as the author's declaration | the redirect predicate was never updated, so `run (cmd …)` bypassed the buffer entirely — §B8's defect, back |
| `0b41a22` | the redirect predicate, and the `FailureText` lift reading the step buffer | *(re-review incomplete)* |

Both regressions were found by building the shape and running it, not by reading the diff. That is the practice worth
keeping on this task.

## Task status

| # | Task | State |
|---|---|---|
| 1-5 | `InputSpec` published, `Cmd` argument combinators, `Input.choices`, `whenSome`/`whenOk`, root command name and script-args default | complete |
| 6-8 | Documentation first pass — capability map, README option model, cross-script composition, `llms.txt` | complete, one fix round |
| 9 | Step carries its command line as a label | complete |
| 10 | `--explain` renders the resolved stage tree | complete, one fix round |
| 11 | `--version` reports the Partas.Build version | complete, clean |
| 12 | Per-stage timing summary | complete, one fix round |
| 13 | `retry` on a stage | complete, two fix rounds |
| 14 | Buffered output under `parallel'` | two fix rounds committed; **round-2 re-review partial** |
| 14b | No consumer signature needs an `Internal` type | not started; brief written |
| 15 | Documentation second pass | not started |
| 16 | Verification against the acceptance criteria | not started |

Wave 2 (Tasks 9-14) runs sequentially — each task edits `Types.fs`, so they cannot be parallelised. Task 14b must
precede Task 15, which edits the same four doc scripts.

### Why Task 14b exists

Task 16's Step 6 — "confirm no consumer signature needs `Internal`" — fails as written. Measured:

```
Build/Program.fs:17          open Partas.Build.Internal
docs/build-overview.fsx:30
docs/composition.fsx:30
docs/computation-expression-operations.fsx:32
docs/index.fsx:29
```

Spec §9 criterion 6 is unmet, not merely unverified, because `StageContext` is declared in `Partas.Build.Internal` and
a step lambda's annotation is the most common consumer signature there is. Task 16 changes no code, so the fix cannot
live there. **Ruling 28** inserted Task 14b: re-export the types consumers name, delete all five `open`s, and let the
compiler enumerate what still leaks.

The set was measured, not guessed — occurrences across `Build/*.fs` and `docs/*.fsx`: `StageContext` 50, `Cmd` 22,
`PipelineContext` 6, `OutputCapture` 5, `StageOutput` 4, `StdStream` 3, and zero for `CmdRunner`, `StageIndex`,
`StepIndex`, `CommandSpec`, `StageCondition` and every `Build*` alias. Whether a type abbreviation suffices for the two
`[<Struct; RequireQualifiedAccess>]` DUs is a compiler question the brief requires spiking.

Brief: `.superpowers/sdd/PLAN-Discoverability-Tasks/task-14b-brief.md`.

### What Task 15 absorbed

**Ruling 27** folded four deferred doc findings into Task 15 rather than the final triage, since it is already editing
those files: `llms.txt` pointing at two byte-identical pages; `docs/composition.fsx:468` hardcoding a filename; the
capability map's `run` row omitting overloads; and CLAUDE.md's stale references to `Build/Spec.fs`,
`Build/TargetOperators.fs`, the `inputs` CE and the test counts. CLAUDE.md is in scope despite not being under `docs/`
— the audience for this branch is the consuming agent as much as the consuming human.

Two corrections to Task 15's brief, measured: `docs/llms-full.txt` is not a source file (fsdocs 22 generates it and
`Build/Program.fs:207-224` prepends the curated `docs/llms.txt` header to both), and there are **zero** `<example>`
elements anywhere in the library today, so Step 5's grep check goes 0 → N.

## Resuming

1. Read the ledger's tail. Its last entries are Rulings 27-32, the partial re-review, and the pause.
2. Finish Task 14's re-review from the five not-reached items above, then adjudicate item 4 — it is yours, not a
   reviewer's.
3. Task 14b, then Task 15, then Task 16.
4. A whole-branch review on the most capable model, pointed at the deferred pile below.
5. `superpowers:finishing-a-development-branch`.

Task briefs are extracted per task by `scripts/task-brief PLAN-Discoverability-Tasks.md N`; review packages by
`scripts/review-package PLAN-Discoverability-Tasks.md BASE HEAD`. Both live in the
`superpowers:subagent-driven-development` skill directory. Task 14b's brief was written by hand and is already in
place.

### Three process notes worth keeping

- **Measure, do not reason.** Every regression on Tasks 12-14 was found by building the shape and running it; none
  was visible in the diff. Two of them were found only because the controller stated a specific doubt in the dispatch
  and told the reviewer to settle it by measurement.
- A reviewer reported writing its findings file and had not (Task 13 round 1). Every dispatch since requires the
  reviewer to verify the file exists on disk before replying. `task-13-findings-r1.md` is a reconstruction and is
  labelled as such.
- Four task briefs specified stale line numbers, a non-existent helper, or an F# compile-order cycle (Tasks 10, 12,
  14, 15). Scan a brief against the current tree before dispatching it — check `<Compile Include>` order in
  `src/Partas.Build/Partas.Build.fsproj` for any brief that creates a file.

## Deferred findings, for the final triage

Nothing here blocks the branch; all of it was classified Minor at review time and parked deliberately.

**Correctness and behaviour**

- Under `parallel'`, redirection is now always on, so a child's colours are lost even with `noPrefixForStep`, and a
  long noisy step shows nothing until it exits while holding its whole output in memory. Inherent to what §B8 asked.
- With `noStdRedirectForStep` under `parallel'`, buffering cannot happen and output interleaves; undocumented.
- Ruling 25's guard re-evaluates an ancestor's `IsParallel` condition — user code — once per retrying stage. A
  retrying stage under `parallel'` still accumulates its own attempts into a shared capture, by design.
- A timed-out or cancelled stage is indistinguishable from a failed one in the timing table.
- A parent stage whose sub-stage fails renders bare `failed`, because `stepErrorCts.Cancel()` fires before
  `handleExn` collects the child's exceptions. Pre-existing.
- `StageTimings` is shared mutable state on a pipeline value, and `Clear` is unguarded.
- `whenStage` conditions execute, and double-execute, under `--explain`.
- `choicesManyCI` is untested; `choicesWith`/`choicesManyWith` duplicate each other.
- `whenSome` in nested and command positions is spike-established only.
- `nameHelpOutput` does an unscoped substring replacement.
- `rootCommandOfScript` evaluates module initialisation; `Args.afterScript` mis-slices a compiled host.

**Presentation**

- `Summary.elideMiddle` keeps only a third as head, so a deep indent elides before the name.
- One long failure message halves the Stage column for every row.
- Nothing explains why a one-stage run has no timing table (Ruling 22 suppresses it).
- A command with neither pipelines nor subcommands renders as its name alone under `--explain`.
- `explain` has no exception handling.

**Structure and docs**

- `Explain.fs` holds three responsibilities: the renderer, `undescribed`, and `libraryVersion`.
- `addLabelledStepFn` has no call sites; four 161-character inline copies stand in for it.
- Doc comments added in Tasks 12-14 drift from `.claude/rules/comments.md` rules 2 and 5 — a "so" inference at
  `Types.fs:405-409`, deixis at `Types.fs:1010-1011`.

Ruling 27 moved the remaining documentation items into Task 15; Ruling 28 moved the `Internal` item into Task 14b.
Neither is listed here any more.

## Verifying the branch as it stands

```shell
dotnet run --project Build.fsproj -- test        # all three suites
dotnet build src/Partas.Build -c Release         # the only thing that catches FS1118
```

At `0b41a22` the implementer reported both green and the reviewer independently confirmed the Release build and the
`output` suite (15 passed). The `parallelism` suite and the full run at this commit are unconfirmed by anyone but the
implementer — re-running them is item 2 of the resume list.
