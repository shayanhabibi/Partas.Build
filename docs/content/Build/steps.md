---
title: Processes and secrets
description: Run processes and F# functions while keeping arguments and secrets intact.
category: Build
order: 4
---

## Preserve arguments

Use `cmd` for interpolated process commands. Each hole becomes one argument, including paths with spaces.

```fsharp
open Partas.Build

let project = "src/My Library/My Library.fsproj"

let compile = stage "compile" {
    run (cmd $"dotnet build {project}")
}
```

Plain `run "dotnet build"` is suitable for a fixed command. `run $"dotnet build {project}"` flattens the interpolation into a string and can split a path into several arguments.

## Mask secrets

`runSensitive` accepts an interpolation directly and masks every hole in the command label:

```fsharp
let token = "example-token"

let publish = stage "publish" {
    runSensitive $"dotnet nuget push package.nupkg --api-key {token}"
}
```

For selective masking, construct a `Cmd` and mark only the secret arguments. Masking command labels does not redact output printed by the child process.

## Run F# work

```fsharp
let report = stage "report" {
    run (fun (ctx: StageContext) -> printfn "Stage: %s" ctx.Name)
}
```

Use `echo` or `StageContext.writeLine` when output must follow the stage's configured writer. A bare `printfn` writes directly to the process console.

## Route process output

Stages can inherit or override an output sink:

```fsharp
let quietBuild = stage "compile" {
    silentOutput
    run "dotnet build"
}
```

`silentOutput` controls child output; `quiet` controls pipeline logging. `captureOutput`, `teeOutput`, `outputTo`, and `OutputCapture` support collection and routing. See the [workflow reference](workflow-reference.md#where-step-output-goes) for examples.
