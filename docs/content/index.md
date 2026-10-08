---
title: Partas.Build
description: Stage-owned inputs, generated CLIs, typed dependencies, and inspectable builds for people and agents.
layout: splash
order: 0
---

<section class="pb-hero">
<div>
<span class="pb-eyebrow">F# build workflows · For people and agents</span>
<h1>CLI & Build for happiness</h1>
<p class="pb-hero__summary">Let the flags and inputs your workflow needs decide the CLI interface, not the other way
around.</p>
<div class="pb-actions">
<a class="pb-button pb-button--primary" href="/Partas.Build/build/getting-started/">Get started →</a>
<a class="pb-button" href="/Partas.Build/reference/">API reference</a>
</div>
</div>
<div class="pb-demo" aria-label="A build command and its automatically registered configuration option">
<div class="pb-demo__label">Build.fs</div>

```fsharp frame=none
let build = input {
    let! config =
        Input.option<string> "--configuration"
        |> Input.def "Release"
    return stage "build" {
        run (cmd $"dotnet build -c {config}")
        }
    }
let root = Command.root {
        command "compile" { build }
    }
```

<div class="pb-demo__output">$ build compile --help<br/>&emsp;Options: --configuration &lt;value&gt; [default: Release]</div>
</div>
</section>

<section class="pb-feature">
<div class="pb-feature__copy">

## Inputs belong to the stage that needs them

Declare an option once, then bind it in every stage that uses it. Reusing the same input declaration deduplicates it across the command: two compile stages, one `--configuration` option.

The command collects inputs from its stages, including nested ones. Options, defaults, validation, and help follow the workflow you compose.

[Declare and bind inputs →](Build/inputs.md)

</div>
<div class="pb-feature__example">

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

let root = Command.root {
    command "build" {
        compile "src/Core/Core.fsproj"
        compile "src/App/App.fsproj"
    }
}
```

<p class="pb-example-result">Two stages share one input declaration. <code>build --help</code> lists <code>--configuration</code> once.</p>

</div>
</section>

<section class="pb-feature">
<div class="pb-feature__copy">

## Compose inputs. Generate the CLI before execution.

Bind independent inputs with `let!` and `and!`. This **applicative composition** keeps the complete input set available before parsing their values.

Static analysis of those declarations builds the command's options and `--help` without running stages. Add a reusable block and its inputs come with it; remove the block and its unused options disappear.

[Compose reusable workflows →](Build/composition.md)

</div>
<div class="pb-feature__example">

```fsharp
let quick = Input.option<bool> "--quick"

let build = input {
    let! config = configuration
    and! skipRestore = quick

    return pipeline "build" {
        stage "restore" {
            when' (not skipRestore) "--quick is set"
            run "dotnet restore"
        }
        stage "compile" {
            run (cmd $"dotnet build -c {config}")
        }
    }
}
```

<p class="pb-example-result">A command containing <code>build</code> gets both <code>--configuration</code> and <code>--quick</code>. No separate option registration.</p>

</div>
</section>

<section class="pb-feature">
<div class="pb-feature__copy">

## Readable for people. Inspectable by agents.

People get generated help, stage names, skip reasons, and failure reports. Agents get a command schema, a static execution plan, and structured run results from the same definition.

`--schema` describes the CLI. `--explain --json` describes the workflow without running steps or effectful conditions. `--json` emits a final result document with outcomes, failures, and timings; detected AI environments enable it by default.

[Inspect a build and consume JSON →](Build/agents.md)

</div>
<div class="pb-feature__example">

```shell
# What inputs does this command accept?
dotnet run --project Build.fsproj -- build --help

# Give an agent the command's schema.
dotnet run --project Build.fsproj -- build --schema

# Inspect the static plan without executing it.
dotnet run --project Build.fsproj -- build --explain --json

# Execute, then read the final JSON result.
dotnet run --project Build.fsproj -- build --json
```

<p class="pb-example-result">Inspect first, execute when ready. In a host, <code>Command.invoke</code> returns a typed <code>RunResult</code> directly.</p>

</div>
</section>

<section class="pb-feature">
<div class="pb-feature__copy">

## Model work as operations and dependencies

An `Operation<'T>` is deferred work with a typed result. Compose process execution with `Operation.map`, then give the work a named producer.

Consumers declare the producer they need. A shared producer runs once per invocation, before its consumers, and both receive the same result. Dependency validation checks placement before execution; a failed prerequisite skips its consumers with a reason.

[Typed producers and dependencies →](Build/CAPABILITIES.md#producers-and-dependencies)

</div>
<div class="pb-feature__example">

```fsharp
let readRevision =
    executeCapture (Cmd.ofList "git" [ "rev-parse"; "HEAD" ])
    |> Operation.map (fun result -> result.Stdout.Trim())

let revision = Producer.emptyDefine "revision" readRevision

let pack project =
    Stage.consuming $"pack {project}" (DependencySpec.require revision)
        (fun sha ->
            cmd $"dotnet pack {project} -p:RepositoryCommit={sha}"
            |> execute)

let release = pipeline "release" {
    pack "src/Core/Core.fsproj"
    pack "src/App/App.fsproj"
}
```

<p class="pb-example-result">One Git lookup, two consumers. The prerequisite is inferred from the dependency, rather than repeated in each stage.</p>

</div>
</section>

## Start with the workflow you need

::::cards
:::card title="Compose your workflow" href="/Partas.Build/build/composition/"
Nest stages, yield lists, and share commands across files. Keep a reusable root in a long-lived F# session.
:::
:::card title="Start with ready-made stages" href="/Partas.Build/build/baked/"
Baked supplies restore, build, pack, test, and publish stages. ShipIt adds changelog-driven release workflows.
:::
:::card title="Control execution" href="/Partas.Build/build/execution/"
Set conditions, parallelism, timeouts, cleanup, and failure policies where the work runs.
:::
::::

<section class="pb-band">

## From a script to a hosted build

Use a `.fsx` script or a build project. Keep a reusable root in a long-lived F# session and invoke it with typed results. Partas.Build targets `net10.0`, `net8.0`, and `netstandard2.0`.

[Installation](Build/installation.md) · [Hosting](Build/hosting.md) · [ShipIt extension](extensions/shipit.md) · [External annotations](external-annotations/index.md)

</section>
