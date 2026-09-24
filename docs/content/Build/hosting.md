---
title: Hosting a build in a long-lived session
category: Build
order: 5
---

# Hosting a build in a long-lived session

A build script run as `dotnet fsi build.fsx -- test` compiles the script, resolves its packages and starts a
runtime on every run, and every tool script it shells out to (`run "dotnet fsi tools/gen.fsx -- …"`) pays the same
again. A long-lived F# session — [SageFs](https://github.com/WillEhrendreich/SageFs), or any host holding an
`FsiEvaluationSession` — pays it once. The build then becomes a function the session calls, answering a
structured `RunResult` instead of an exit code and a screen of text.

This page is written against SageFs, but nothing in it depends on SageFs beyond the location of its startup script.

## The shape

Three files, where a plain script build has one:

| File | Holds | Side effects when loaded |
|---|---|---|
| `build.defs.fsx` | Options, stages, commands, and `let root = Command.root { … }` | None |
| `build.fsx` | `#load "build.defs.fsx"`, then one line that invokes `root` and exits | Runs the build, exits the process |
| `.SageFs/init.fsx` | `#load "build.defs.fsx"`, then a `build` function over `root` | None |

The split exists because a session must never evaluate the last line of `build.fsx`. `rootCommand argv { … }` and
`rootCommandOfScript { … }` run the build *as they are constructed*, and `exit` terminates the process — in a
session, the session's own worker. `Command.root { … }` takes the same operations and builds the command without
parsing or running anything.

### `build.defs.fsx`

```fsharp
module BuildDefs

#r "nuget: Partas.Build"
#r "nuget: Partas.Build.Baked"

open Partas.Build
open Partas.Build.Baked

let projects = [ "src/MyLib/MyLib.fsproj" ]

let root = Command.root {
    description "MyLib's build"
    workingDir __SOURCE_DIRECTORY__
    command "build" {
        description "Restores and builds"
        Command.pipeline {
            Stages.restore "MyLib.slnx"
            Stages.build projects
        }
    }
    command "test" {
        description "Builds and runs the tests"
        Command.pipeline {
            Stages.restore "MyLib.slnx"
            Stages.build projects
            Stages.expecto "tests/MyLib.Tests/MyLib.Tests.fsproj" [ "--sequenced" ]
        }
    }
}
```

`workingDir __SOURCE_DIRECTORY__` makes every relative path resolve against the repository, whatever the
session's current directory is. The explicit `module BuildDefs` gives the file a name that reads well from the
other two; without it, the module is named after the file.

### `build.fsx`

```fsharp
#load "build.defs.fsx"

open Partas.Build

exit (Command.invoke (Args.script ()) BuildDefs.root).ExitCode
```

`dotnet fsi build.fsx -- test --quick` behaves exactly as a single-file build did: same commands, same `--help`,
same exit codes.

### `.SageFs/init.fsx`

```fsharp
#load "build.defs.fsx"

open System
open Partas.Build

let build (args: string list) = BuildDefs.root.Invoke(args, output = Console.Out)
```

SageFs evaluates the *text* of this file at the end of a session's warm-up, so `#load` paths resolve against the
session's working directory — the repository — rather than against `.SageFs/`.

`output = Console.Out` is read on each call, which in SageFs is the writer capturing the current evaluation.
Given a writer, `Invoke` sends everything the run prints to it as plain text: help, `--explain`, the pipeline's
own lines, and the output of every child process whose stage writes to the console. Without it, a child process
inherits the worker's real standard output, and its lines never reach the evaluation's result.

## Calling it

From the session, or from an agent through SageFs's `send_fsharp_code`:

```fsharp
build [ "test"; "--explain" ]            // what would run, and what is skipped and why
let result = build [ "test"; "--quick" ]

result.Outcome                           // Succeeded | Failed | UsageError | Cancelled
result.ExitCode                          // 0 | 1 | 2 | 130

[ for failure in result.Failures -> failure.Label, FailureCause.describe failure.Cause ]
[ for timing in result.Timings -> timing.Name, timing.Elapsed ]
```

`build [ "test"; "--json" ]` ends its output with the JSON form of the same result, for a caller that reads
text rather than F# values.

## Mounting other scripts' commands

A build that runs a tool script as a child process —

```fsharp
stage "wire" { run "dotnet fsi tools/wire.fsx -- generate" }
```

— cold-starts `fsi` for it on every run, and loses the tool's result to an exit code. Split the tool the same way
and mount its command instead:

```fsharp
// tools/wire.defs.fsx
module Wire

#r "nuget: Partas.Build"
open Partas.Build

let generate = command "wire" {
    description "Regenerates the wire types"
    stage "generate" { run (cmd $"dotnet run --project tools/WireGen") }
}
```

```fsharp
// build.defs.fsx
#load "tools/wire.defs.fsx"

let root = Command.root {
    // …
    addCommand Wire.generate
}
```

`build [ "wire" ]` now runs in the session, `<build> --help` lists `wire` beside the build's own commands, and
`<build> wire --help` lists the tool's options.
The tool keeps a thin `tools/wire.fsx` of its own for standalone use. A tool that exposes its stages rather than
a whole command composes further: yield them into a pipeline of the build's own.

## Cancelling a run

SageFs's `cancel_eval` interrupts the thread running the evaluation (`Thread.Interrupt`); it signals no
`CancellationToken`. `Invoke` runs the build on a thread of its own and waits for it, so the interrupt reaches the
waiting thread, which:

1. cancels the run as a cancellation token would — the pipeline stops, and the whole process tree of every
   running command is killed, grandchildren included;
2. waits up to five seconds for the run to wind down;
3. raises `ThreadInterruptedException`, which ends the evaluation. No `RunResult` is returned.

A caller that owns a token passes it instead: `BuildDefs.root.Invoke(args, output = Console.Out, cancellationToken
= token)`, and a cancelled run returns a `RunResult` with `Outcome = Cancelled`.

What cancellation does not reach:

- **A step written as a plain F# function** — `run (fun ctx -> …)` — runs on until it returns, unless it reads the
  token itself (`Async.CancellationToken` inside an `async` step). An `async` step is stopped at its next
  asynchronous wait. A blocking function keeps its thread after the evaluation has ended.
- **Work already done.** Cancellation stops what is running; it undoes nothing.

## What stays shared across calls

A session keeps every value it evaluates. For a build, that means:

- **`root` is built once.** A pipeline written as `Command.pipeline { … }` or `pipeline "…" { … }` with no CLI
  inputs is one value, reused by every call. Such a value runs one run at a time: a second call reaching it while
  a first is still running — including a straggler step from an interrupted run — fails with exit code `1` and
  "Pipeline '…' is already running". Stages yielded straight into a `command`, and pipelines built inside an
  `input { }`, are built afresh for each call.
- **Option defaults are computed once**, when the module declaring them initialises. `Baked.Common.isCI` reads
  the CI environment variables and `Baked.NuGet.apiKey` reads `NUGET_API_KEY` at that moment, and keep those
  values for the life of the worker. Pass the flag explicitly, or restart the session
  (`hard_reset_fsi_session`), after changing them. The environment a *stage* runs with is read afresh at the
  start of every run.
- **Editing `build.defs.fsx`** and re-sending its `#load` defines a new `BuildDefs` module; the `build` function
  bound earlier still calls the old `root`. Re-send the whole of `.SageFs/init.fsx` instead.

The library keeps no other state between runs. It sets the console encodings at most once per process, and only
for a stream that is not redirected, and its console follows whichever writer `Console.Out` is when it writes.
Without a terminal — a SageFs worker has none — tables are laid out 80 columns wide.

## Live testing

SageFs discovers Expecto tests in a loaded project and runs them as code changes, with a five-second default
timeout per test. It classifies a test whose full name contains `integration` as an integration test, run only
on demand. A build's own test suites that start processes — as this repository's do — belong there:

```fsharp
[<Tests>]
let tests =
    testList "cmd" [
        // …
    ]
    |> testLabel "integration"
```
