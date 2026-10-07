---
title: Execution
description: Control conditions, concurrency, timeouts, cleanup, and failures.
category: Build
order: 6
---

## Conditions narrow; settings override

Every condition on a stage must pass. Use one `whenAny` block when either condition should enable it:

```fsharp
open Partas.Build

let publish = stage "publish" {
    whenBranch "master"
    whenWindows
    run "dotnet pack"
}
```

This stage runs on Windows **and** master. A second condition never replaces the first. Supply a reason to `when'` so `--explain` can describe a skip.

Settings such as `workingDir` and `parallel'` use the last value at the same level. A child stage inherits unset settings from its parents and pipeline. Command settings fill in pipeline defaults.

## Run independent work concurrently

```fsharp
let compile = stage "compile" {
    parallel' 2
    run "dotnet build A.fsproj"
    run "dotnet build B.fsproj"
    run "dotnet build C.fsproj"
}
```

At most two steps run at once. Omit `parallel'` for sequential execution; use `parallel' true` for unbounded concurrency. Avoid parallel builds that write the same referenced project's outputs.

## Bound execution time

```fsharp
let test = stage "test" {
    timeout 60
    timeoutForStep 30
    run "dotnet test"
}
```

A timeout cancels the stage and kills its process tree. Pipelines additionally support `timeoutForStage`. A blocking F# function must cooperate with cancellation itself.

## Always clean up

```fsharp
let integration = pipeline "integration" {
    stage "start" { run "docker compose up -d" }
    stage "test" { run "dotnet test" }
    post [ stage "stop" { run "docker compose down" } ]
}
```

Post stages run after the main stages, including after failure.

## Decide how failure propagates

- `continueStepsOnFailure`: run the remaining steps.
- `continueStageOnFailure`: let the pipeline continue.
- `continueOnStepFailure`: apply both.
- `acceptExitCodes`: accept additional process exit codes.
- `failIfIgnored`: treat a skipped stage as failure.

For typed outputs and prerequisite ordering, use [producers and dependencies](CAPABILITIES.md#producers-and-dependencies). For recovery policies, see [failure handlers](CAPABILITIES.md#failure-handlers).
