# Partas.Build: instructions for coding agents

Paste this block into a repository's `AGENTS.md`, `CLAUDE.md` or `.claude/rules/` when its build is written with
[Partas.Build](https://shayanhabibi.github.io/Partas.Build/). Replace `<build>` with how the repository runs its
build: `dotnet fsi build.fsx --`, `dotnet run --project Build.fsproj --`, or the name of a tool. The same file ships
at the root of the `Partas.Build` package (`~/.nuget/packages/partas.build/<version>/AGENTS-snippet.md`).

---

## Running the build

The build is a command-line program. Ask it what it does instead of reading its source.

1. `<build> --help` lists the commands. `<build> <command> --help` lists the options that command accepts.
   The help is generated from the stages the command runs, so it is complete and current: an option absent from
   it does not exist for that command.
2. `<build> <command> [options] --explain` prints the stages and steps the command would run with those options,
   and which stages are skipped and why. It runs nothing. Run it before a command you have not run before, and
   whenever an option's effect is unclear.
3. `<build> <command> [options] --explain --json` prints the same tree as JSON. Conditions with side effects
   (a `git` branch check, a condition stage) are reported as `"status": "unevaluated"` rather than run.
4. `<build> <command> --schema` prints the command, its options (name, aliases, type, default, choices) and its
   subcommands as JSON. A secret default prints as `"***"`.

## Exit codes

| Code | Meaning | What to do |
|---|---|---|
| `0` | Success; also `--help`, `--version`, `--explain`, `--schema` | Nothing |
| `1` | A stage failed, or the build raised an exception | Fix the code or the environment; read the run result for which step |
| `2` | Usage error: unknown option, missing command, failed validation, or an unsatisfiable stage dependency (also under `--explain`). No stage ran | Fix the command line; check `<command> --help` |
| `130` | Cancelled: the pipeline's own timeout expired, or the caller cancelled the run. A stage's timeout is a failure (`1`). Ctrl+C kills the process: the shell reports `130`, but no run result is written | Rerun, or raise the timeout |

## Reading the result of a run

Add `--json` to a run. The stages print as they otherwise would; the timing table is replaced by the run result,
which is the **last line** of output, one line of JSON:

```json
{"formatVersion":1,"exitCode":1,"outcome":"failed","pipelines":[{"name":"test",
 "reports":[{"name":"unit","address":"unit","path":[0],"outcome":"failed","error":"Exit code not acceptable.",
   "propagates":true,"failures":[{"step":0,"label":"dotnet test","cause":{"kind":"reported",
   "message":"Exit code not acceptable."}}],"nested":[]}],
 "timings":[{"name":"unit","depth":0,"elapsedMs":90.7,"outcome":"failed","error":"Exit code not acceptable."}]}]}
```

- `outcome` is `succeeded`, `failed`, `usageError` or `cancelled`, matching the exit code.
- `pipelines[].reports` holds one report per stage, nested as the stages nest (`nested`). A report's `outcome` is
  `succeeded`, `skipped` or `failed`; `propagates: false` marks a failure the build tolerated.
- `failures[].step` counts from zero and matches `index` in the `--explain --json` tree of the same stage;
  `label` is the step's command line with secrets masked. `cause.kind` is `command`, `start` (the executable
  could not be started), `raised` (an exception), `timedOut` or `reported`.
- `timings` lists every stage that finished, in order, with its `depth` and `elapsedMs`.

`--report <path>` writes the same document, indented, to a file, with or without `--json`. Take the result from
there when the run's output is long or interleaved.

## Rules

- Do not guess option names; read `<command> --help`.
- Do not edit the build to skip a failing stage. Read the failing step's `label` and `cause`, and fix the cause.
- A command listed under "Commands with no description" in `--explain` output has no help text; read its stages
  with `--explain` before running it.
