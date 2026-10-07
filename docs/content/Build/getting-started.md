---
title: Getting started
description: Create a build command with a configuration option and generated help.
category: Build
order: 2
---

Create a build project or script using the [installation guide](installation.md).

## Define a stage

```fsharp
open Partas.Build

let compile = input {
    let! config =
        Input.option<string> "--configuration"
        |> Input.alias "-c"
        |> Input.def "Release"
        |> Input.acceptOnlyFromAmong [ "Debug"; "Release" ]

    return stage "compile" {
        run (cmd $"dotnet build -c {config}")
    }
}
```

The stage owns `--configuration`. `cmd` keeps each interpolated value as one argument.

## Add it to a root

```fsharp
let root = Command.root {
    description "My build"
    workingDir __SOURCE_DIRECTORY__
    command "build" {
        description "Compile the solution"
        compile
    }
}

let build args = Command.invoke args root
```

`Command.root` constructs the command tree without executing it. Relative process paths resolve against `workingDir`.

## Invoke it

For an executable project, place this entry point after the definitions:

```fsharp
[<EntryPoint>]
let main argv = (build argv).ExitCode
```

```shell
dotnet run --project Build.fsproj -- build --help
dotnet run --project Build.fsproj -- build --explain
dotnet run --project Build.fsproj -- build -c Debug
```

For a script, use this final line instead:

```fsharp
exit (build (Args.script ())).ExitCode
```

```shell
dotnet fsi build.fsx -- build --help
```

`--help` lists the stage's configuration option automatically. `--explain` prints the workflow without running the build. Exit code `0` means success, `1` a stage failed, `2` invalid input, and `130` cancellation.

Next: [bind more inputs](inputs.md), [compose stages](composition.md), or use [Baked's .NET stages](baked.md).
