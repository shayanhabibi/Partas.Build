---
title: Composition
description: Reuse stages, assemble workflows, and share command trees across files.
category: Build
order: 5
---

Stages and pipelines are F# values. A reusable block can return a plain stage or an `InputSpec<StageContext>` when it needs CLI inputs.

## Define a block

```fsharp
open Partas.Build

let configuration =
    Input.option<string> "--configuration"
    |> Input.def "Release"

let compile project = input {
    let! config = configuration
    return stage $"compile {project}" {
        run (cmd $"dotnet build {project} -c {config}")
    }
}
```

## Assemble a workflow

```fsharp
let projects = [ "src/Core/Core.fsproj"; "src/App/App.fsproj" ]

let build = pipeline "build" {
    workingDir __SOURCE_DIRECTORY__
    stage "restore" { run "dotnet restore" }
    for project in projects do
        compile project
}

let root = Command.root {
    command "build" { build }
}
```

`build --help` registers `--configuration` once, even though several stages read it. The same block can be reused by another command.

## Nest stages

```fsharp
let libraries = stage "libraries" {
    timeout 300
    for project in projects do
        compile project
}
```

Place settings before loops. For individually yielded blocks, settings can appear before or after the block. Children inherit unset settings from the enclosing stage.

## Wrap blocks that carry inputs

A wrapper returning a stage can accept an input-bearing stage:

```fsharp
let group name (block: InputSpec<StageContext>) = stage name {
    block
}

let grouped = group "core" (compile "src/Core/Core.fsproj")
```

The wrapper retains the inner input set. See the [composition reference](composition-reference.md#blocks-that-take-blocks) for wrappers accepting lists, adding their own inputs, and compiler limits around nested specs.

## Yield lists

```fsharp
let blocks = [ for project in projects -> compile project ]

let listed = pipeline "compile" {
    [ yield! blocks ]
}
```

Use a list when F# rejects `yield!` mixed with custom operations. A `for` loop is usually the simpler form.

## Composition across files

Put reusable definitions in a file that constructs commands without invoking or exiting:

```fsharp
// tools/wire.defs.fsx
module Wire

open Partas.Build

let generate = command "wire" {
    stage "generate" { echo "generating" }
}
```

Load it from your entry-point script and add the command to the root:

```fsharp
// build.fsx; the package references precede this #load
#load "tools/wire.defs.fsx"
open Partas.Build

let root = Command.root {
    addCommand Wire.generate
}

exit (Command.invoke (Args.script ()) root).ExitCode
```

Keep command names unique among siblings. An explicit module name avoids guessing the identifier F# derives from a filename. For a reusable root shared with a session, follow [Hosting](hosting.md).

The [composition reference](composition-reference.md) includes larger examples and standalone script entry points.
