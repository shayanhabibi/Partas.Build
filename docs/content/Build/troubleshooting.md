---
title: Troubleshooting
description: Fix common input, argument, and composition mistakes.
category: Build
order: 10
---

## A path with spaces becomes several arguments

Use `run (cmd $"dotnet build {path}")`. Direct string interpolation into `run` loses the argument boundary.

## A second `let!` does not compile

Bind inputs in one `let! … and! …` group. Derive dependent values after that group. The parser needs to know every input before reading their values.

## A custom operation under `if` or `match` does not compile

Choose a value first, then pass it to the operation:

```fsharp
open Partas.Build

let hasKey = false
let push = if hasKey then "dotnet nuget push package.nupkg" else "dotnet pack"
let publish = stage "publish" { run push }
```

A conditional that yields an entire stage is supported.

## `yield!` and custom operations conflict

Yield a list from the computation expression instead. See [Composition](composition.md).

## An option is missing from help

The selected command registers inputs its stages bind. Yield the input-bearing stage or pipeline into that command. Constructing an input elsewhere does not register it.

## A stage is skipped unexpectedly

Inspect it with `--explain`. Conditions conjoin; a second `whenBranch` narrows the stage to both branches. Use `whenAny` to express alternatives.

## Help or explain installs a tool

Move setup into an execution step. Input readers and module initialization should not perform workflow side effects. The [ShipIt extension](../extensions/shipit.md) supplies explicit setup commands.
