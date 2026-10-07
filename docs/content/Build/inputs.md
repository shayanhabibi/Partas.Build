---
title: Inputs and help
description: Bind CLI options and arguments where they are used.
category: Build
order: 3
---

Declare an input once. Bind it in an `input` block that returns a stage or pipeline.

```fsharp
open Partas.Build

let quick =
    Input.option<bool> "--quick"
    |> Input.alias "-q"
    |> Input.description "Skip restore"

let configuration =
    Input.option<string> "--configuration"
    |> Input.def "Release"

let compile = input {
    let! skipRestore = quick
    and! config = configuration

    return pipeline "compile" {
        stage "restore" {
            when' (not skipRestore) "--quick is set"
            run "dotnet restore"
        }
        stage "build" { run (cmd $"dotnet build -c {config}") }
    }
}

let root = Command.root {
    command "build" { compile }
}
```

Both options appear in `build --help`. Another command receives them only if its stages bind them.

## Bind independent inputs together

Use one `let!` followed by `and!` for additional inputs. A second `let!` is rejected. The complete input set must be known before parsing.

If one value depends on another, derive it after binding. Keep runtime work inside a stage, rather than in the input reader.

## Choose the input shape

- `Input.option<'T> "--name"`: an option.
- `Input.argument<'T> "NAME"`: a positional argument.
- `Input.def value`: a default.
- `Input.acceptOnlyFromAmong values`: validation against a fixed set.
- `Input.map f`: transform a parsed value.
- `Input.sensitive`: hide the value in structured inspection.

See [Capabilities](CAPABILITIES.md#input-combinators) for the complete list and [Baked](baked.md) for ready-made inputs.
