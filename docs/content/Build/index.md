---
title: Overview
description: The building blocks of a Partas.Build command.
category: Build
order: 0
---

Partas.Build turns F# workflow definitions into command-line tools. A stage declares the inputs it reads; each command derives its options, validation, and help from those stages.

## Start with a command

[Install the packages](installation.md), then [create your first build](getting-started.md).

```fsharp
open Partas.Build

let root = Command.root {
    command "build" {
        stage "compile" { run "dotnet build" }
    }
}

let build args = Command.invoke args root
```

Construction runs nothing. `Command.invoke [ "build" ] root` parses the arguments and runs the selected workflow.

## The building blocks

- **Step**: one process or F# function inside a stage.
- **Stage**: named steps, conditions, and execution settings.
- **Input**: an option or argument bound where the workflow needs it.
- **Pipeline**: an ordered workflow of stages.
- **Command**: a CLI entry point containing stages or pipelines.
- **Root**: the command tree, invoked once or reused by a host.

## Choose your next task

- [Inputs and help](inputs.md): bind options with `let!` and `and!`.
- [Processes and secrets](steps.md): preserve argument boundaries and mask sensitive values.
- [Composition](composition.md): reuse stages and commands.
- [Execution](execution.md): conditions, parallelism, timeouts, and failure handling.
- [Baked stages](baked.md): common .NET build operations.
- [Agents and JSON](agents.md): inspect and run a build with structured output.

[Capabilities](CAPABILITIES.md) lists the operations. [Workflow reference](workflow-reference.md) retains the detailed examples, and the [API reference](https://shayanhabibi.github.io/Partas.Build/reference/) supplies full signatures.
