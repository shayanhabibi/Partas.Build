(**
---
title: Composing reusable blocks
category: Build
order: 3
---
*)
(*** hide ***)
// The sources are #load-ed rather than #r-ing a built DLL, for two reasons: the guide then type-checks against
// the code as written instead of against the last build, and nothing holds a file lock — a #r-ed assembly stays
// loaded for the lifetime of the site watcher (`dotnet run --project Build.fsproj -- docs --watch`), which on
// Windows makes rebuilding the library fail.
// Keep this list in the same order as the <Compile> items in Partas.Build.fsproj.
#r "nuget: FSharp.Control.AsyncSeq, 4.15.0"
#r "nuget: FsToolkit.ErrorHandling, 5.2.0"
#r "nuget: System.CommandLine, 2.0.11"
#r "nuget: Spectre.Console, 0.57.2"


#load "../../../src/Partas.Build.Cmd/Execution.fs"
#load "../../../src/Partas.Build.Cmd/Program.fs"
#load "../../../src/Partas.Build/System.CommandLine/Aliases.fs"
#load "../../../src/Partas.Build/System.CommandLine/Inputs.fs"
#load "../../../src/Partas.Build/Exceptions.fs"
#load "../../../src/Partas.Build/Output.fs"
#load "../../../src/Partas.Build/Environment.fs"
#load "../../../src/Partas.Build/Timing.fs"
#load "../../../src/Partas.Build/Producer.fs"
#load "../../../src/Partas.Build/Failures.fs"
#load "../../../src/Partas.Build/Conductors.fs"
#load "../../../src/Partas.Build/Conductors.Runners.fs"
#load "../../../src/Partas.Build/Process.fs"
#load "../../../src/Partas.Build/Operations.fs"
#load "../../../src/Partas.Build/Dependencies.fs"
#load "../../../src/Partas.Build/DependencyPlan.fs"
#load "../../../src/Partas.Build/ExecutionState.fs"
#load "../../../src/Partas.Build/Builders/StageSettings.fs"
#load "../../../src/Partas.Build/Builders/Stage.fs"
#load "../../../src/Partas.Build/Builders/Conditions.fs"
#load "../../../src/Partas.Build/Builders/PipelineSettings.fs"
#load "../../../src/Partas.Build/Builders/Pipeline.fs"
#load "../../../src/Partas.Build/Builders/Inputs.fs"
#load "../../../src/Partas.Build/Explain.fs"
#load "../../../src/Partas.Build/Summary.fs"
#load "../../../src/Partas.Build/Builders/Command.fs"
#load "../../../src/Partas.Build.Baked/Program.fs"
#load "../../../src/Partas.Build.Baked/Common.fs"
#load "../../../src/Partas.Build.Baked/NuGet.fs"
#load "../../../src/Partas.Build.Baked/Dotnet.fs"
#load "../../../src/Partas.Build.Baked/SemVer.fs"


open Partas.Build

(**
# Composing reusable blocks

[The guide](index.fsx) introduces one stage at a time. This page builds a library of reusable *blocks* —
stages that carry their own CLI inputs — and assembles them into pipelines and commands.

Every snippet compiles when the docs build, except the two file listings under *Composition across files*,
which are separate scripts.

## The shape of a block

A block is a function returning a stage. It returns a plain `StageContext` when it needs no CLI flag, or an
`InputSpec<StageContext>` from an `input { }` CE when it does. Both are ordinary values and both are
yieldable anywhere a stage is.
*)

module Options =
    let config =
        Input.option<string> "--configuration"
        |> Input.alias "-c"
        |> Input.def "Release"
        |> Input.acceptOnlyFromAmong [ "Debug"; "Release" ]

    let quick =
        Input.option<bool> "--quick"
        |> Input.alias "-q"
        |> Input.description "Skip restores and cleaning"

    let verbose = Input.option<bool> "--verbose" |> Input.alias "-v"

module Blocks =
    /// No inputs: a plain `StageContext`.
    let clean (project: string) =
        stage $"clean {project}" { run (cmd $"dotnet clean {project}") }

    /// Reads `--quick`: an `InputSpec<StageContext>`.
    let restore (project: string) = input {
        let! quick = Options.quick

        return stage $"restore {project}" {
            when' (not quick)
            run (cmd $"dotnet restore {project}")
        }
    }

    /// Reads two options. Bind them in one `let! … and!` block.
    let build (project: string) = input {
        let! config = Options.config
        and! verbose = Options.verbose

        let level = if verbose then "detailed" else "minimal"

        return stage $"build {project}" {
            run (cmd $"dotnet build {project} -c {config} -v {level}")
        }
    }

(**
## Yielding blocks

A pipeline takes blocks in declaration order and unions the options they declare, whether a block is a bare
stage or a spec:
*)

let one =
    pipeline "one project" {
        workingDir __SOURCE_DIRECTORY__

        Blocks.clean "MyLib.fsproj"
        Blocks.restore "MyLib.fsproj"
        Blocks.build "MyLib.fsproj"
    }

(**
`one` is an `InputSpec<PipelineContext>` declaring `--quick`, `--configuration` and `--verbose` exactly once,
contributed by `build` and `restore`.

## Loops and lists

A `for` loop and a yielded list both work; they differ only in where the collection comes from:
*)

let projects = [ "MyLib.fsproj"; "MyLib.Tool.fsproj"; "MyLib.Tests.fsproj" ]

let looped =
    pipeline "all projects" {
        for project in projects do
            Blocks.build project
    }

let listed =
    pipeline "core" {
        [ Blocks.restore "MyLib.fsproj"
          Blocks.build "MyLib.fsproj" ]
    }

(**
Both forms union the inputs of every element: `looped` still declares `--configuration` and `--verbose` once
across three stages.

Reach for the list form when a custom operation would otherwise force `yield!`, which F# refuses to mix with
custom operations (`FS3086`):

```fsharp
// FS3086
pipeline "p" { yield! blocks; timeout 60.0 }

// this compiles
pipeline "p" { [ yield! blocks ] }
```

## Nesting

A stage nested inside another stage is one step of its parent, so blocks group with no separate concept, and
an input declared at any depth surfaces on the command running the pipeline.

Here the innermost stage, three levels down, is the only thing that names `--configuration`:
*)

let deep =
    command "ci" {
        description "Restore, build and test"

        pipeline "ci" {
            workingDir __SOURCE_DIRECTORY__
            timeoutForStage 600<second>

            stage "prepare" {
                Blocks.clean "MyLib.fsproj"
                Blocks.restore "MyLib.fsproj"
            }

            stage "compile" {
                stage "libraries" {
                    parallel' 2

                    for project in [ "MyLib.fsproj"; "MyLib.Tool.fsproj" ] do
                        Blocks.build project
                }

                stage "tests" { Blocks.build "MyLib.Tests.fsproj" }
            }
        }
    }

(**
`ci --help` lists `--quick`, `--configuration` and `--verbose`; the stages that read them registered them.

A setting placed *after* a nested block still applies to the enclosing stage, so ordering is free:
*)

let settingsAfter =
    stage "compile" {
        Blocks.build "MyLib.fsproj"
        timeout 300
        whenNot { envVar "SKIP_BUILD" }
    }

(**
## Blocks that take blocks

A block is a value, so a block factory can take other blocks as arguments — the usual way to build a house
style: a wrapper adding retries, timing, teardown or a condition to whatever it receives.
*)

/// Wraps stages in a named group with a shared timeout, and a teardown that always runs.
let group name (seconds: int) (stages: StageContext seq) =
    stage name {
        timeout seconds

        [ yield! stages
          yield stage $"{name} done" { echo $"finished {name}" } ]
    }

let grouped =
    pipeline "grouped" {
        group "prepare" 60 [ Blocks.clean "MyLib.fsproj" ]
    }

(**
When the wrapped stages carry inputs, the wrapper takes an `InputSpec` list and returns an `InputSpec`, joined
through the `input` CE:
*)

let inputGroup name (seconds: int) (blocks: InputSpec<StageContext> list) = input {
    let! stages = InputSpec.sequence blocks

    return stage name {
        timeout seconds
        stages
    }
}

let inputGrouped =
    pipeline "release" {
        inputGroup "compile" 600 [ Blocks.restore "MyLib.fsproj"; Blocks.build "MyLib.fsproj" ]
    }

(**
`InputSpec.sequence` turns a list of specs into one spec of a list, unioning the inputs. `InputSpec.traverse fn
items` does the same over a mapping. These are the two functions to reach for when writing this kind of wrapper.

## Adding an input of the wrapper's own

A wrapper can bind flags the wrapped blocks know nothing about, alongside the sequenced blocks:
*)

let skipTests = Input.option<bool> "--skip-tests" |> Input.description "Build the tests but do not run them"

let testGroup (blocks: InputSpec<StageContext> list) = input {
    let! stages = InputSpec.sequence blocks
    and! skip = skipTests

    return stage "test" {
        when' (not skip)
        stages
        run "dotnet test --no-build"
    }
}

let tested =
    command "test" {
        description "Build and test"

        pipeline "test" {
            testGroup [ Blocks.restore "MyLib.fsproj"; Blocks.build "MyLib.fsproj" ]
        }
    }

(**
`test --help` now lists `--skip-tests` next to the three the blocks declared.

## What does not compose: a block returning a block's spec

One shape the CE cannot express is worth recognising on sight. A block that binds inputs returns an
`InputSpec<StageContext>`. A *second* `input { }` that builds one of those inside its own `return` produces an
`InputSpec<InputSpec<StageContext>>`, which nothing downstream accepts:

```fsharp
// Does not work. `bumpBlock` is itself an `input { }`, so `return` wraps a spec inside a spec.
let bumpFromArgument project = input {
    let! config = Options.config
    return bumpBlock (InputSpec.ofInput Sources.bumpArgument) project
}
```

There is no `InputSpec.flatten`, and no sound `flatten` can exist. Flattening would read the inner spec's `Inputs`, which exist only once its
`Read` runs, and `Read` needs the `ParseResult` those inputs configure — the circularity `InputSpec` exists to
break, and why `input` has no `Bind`: a sequential `let!` fails with `FS0708` instead of compiling into an
option set that cannot be registered.

**Binding is a layer boundary**: one layer binds, the layers above harvest. When two blocks share a body but
differ in where a value comes from, pass the *source* in as an `InputSpec` instead of passing a read value out
as one. The shared body keeps one `let!`/`and!` group; callers vary only the spec they hand over:
*)

module Sources =
    /// The bump kind as a positional argument — `bump minor`.
    let bumpArgument = Input.argument<string> "bump" |> Input.def "patch"

    /// The same value as an option — `release --bump minor`.
    let bumpOption = Input.optionMaybe<string> "--bump"

/// The shared body. `bumpSource` arrives as a spec, so it joins the one bind group like any other source.
let bumpBlock (bumpSource: InputSpec<string>) (project: string) = input {
    let! bump = bumpSource
    and! config = Options.config

    return stage $"bump {project}" {
        run (cmd $"dotnet build {project} -c {config} /p:Bump={bump}")
    }
}

let bumpFromArgument project =
    bumpBlock (InputSpec.ofInput Sources.bumpArgument) project

let bumpFromOption project =
    let source = InputSpec.ofInput Sources.bumpOption |> InputSpec.map (Option.defaultValue "patch")
    bumpBlock source project

(**
`InputSpec.ofInput` lifts a bare `ActionInput` into a spec; `InputSpec.map` adapts its value, so a source can
be defaulted or reshaped before being handed over. Both commands below declare `--configuration`; one declares
the argument, the other declares `--bump`:
*)

let bumping =
    [ command "bump" { bumpFromArgument "MyLib.fsproj" }
      command "release" { bumpFromOption "MyLib.fsproj" } ]

(**
The same rule covers a helper needing no source of its own: give it the already-read values as plain
arguments and let the caller bind. An `input { }` nested inside a `return` is always the error.

## Commands over stages

A command needs no explicit `pipeline`. Stages yielded straight into it become one implicit pipeline that
takes the command's name and description:
*)

let flat =
    command "build" {
        description "Build the solution"

        Blocks.restore "MyLib.sln"
        Blocks.build "MyLib.sln"
    }

(**
Consecutive stages share that one pipeline, its settings, its run and its `whenStage` cross-references. A
command can also mix implicit and explicit pipelines; declaration order is preserved.

`addInput` covers the remainder: a flag the command should expose that no stage binds.

A command also takes the pipeline settings themselves — `workingDir`, `envVars`, the timeouts, the output
operations, the hooks, `post`, `verbosity` — and hands them to every pipeline it runs, including the implicit
one. They are defaults: a pipeline that sets the same thing keeps its own value. See
[command-level defaults](index.fsx#command-level-defaults).

## Conditional assembly

An `if` with no `else` is fine around a whole stage or block; the untaken branch contributes nothing. Custom
operations are the exception, since F# forbids those under an `if`.
*)

let includeDocs = System.Environment.GetEnvironmentVariable "DOCS" = "1"

let conditional =
    pipeline "release" {
        Blocks.build "MyLib.fsproj"

        if includeDocs then
            stage "docs" { run "dotnet run --project docs/docs.fsproj -- build" }
    }

(**
Bind and branch inside the `input` CE for a condition known only after parsing; it is ordinary F# with no
such restriction:
*)

let maybeClean = input {
    let! quick = Options.quick

    return
        if quick then stage "skip clean" { echo "skipping clean" }
        else Blocks.clean "MyLib.fsproj"
}

(**
## Putting it together

A small, complete build assembled entirely from blocks:
*)

let mainCommand argv =
    rootCommand argv {
        description "MyLib build"

        addCommands [
            command "build" {
                description "Restore and build"

                Command.pipeline {
                    workingDir __SOURCE_DIRECTORY__

                    for project in projects do
                        Blocks.build project
                }
            }

            tested

            command "release" {
                description "Pack and push"

                pipeline "release" {
                    inputGroup "compile" 600 [ Blocks.build "MyLib.fsproj" ]

                    stage "pack" {
                        whenBranch "master"
                        captureOutput
                        run "dotnet pack MyLib.fsproj --no-build"
                    }

                    post [ stage "notify" { echo "released" } ]
                }
            }
        ]
    }

(**
Each command lists only the flags its own stages read: `build` gets `--configuration` and `--verbose`, `test`
adds `--skip-tests` and `--quick`, `release` gets what its blocks declare.

## Composition across files

A `Command` is an ordinary value and `Yield` takes one. A script that owns a slice of the build exposes its
commands as a binding; another script `#load`s the file and yields the binding.

Two rules make it work:

1. **The command tree is a value.** Bind it with `let`; do not `exit` it at the point of definition.
2. **The `rootCommand` invocation is gated.** `#load` executes the loaded script top to bottom, so an
   ungated `exit (rootCommand … )` takes over the loading script's process. `Args.scriptName ()` returns the
   filename the process launched with — the loaded script's own name only when it is the one running.

```fsharp
// tools/generate-wire.fsx
#load "../prelude.fsx"
open Partas.Build

module Options =
    let target =
        Input.option<string> "--target"
        |> Input.mapFromAmong [ "node", "node"; "browser", "browser" ]
        |> Input.def "node"
        |> Input.description "Runtime the wire layer is generated for"

module Stages =
    let generate layer = input {
        let! target = Options.target
        return stage $"generate {layer}" { echo $"{layer} -> {target}" }
    }

let generateCommands =
    command "generate" {
        description "Regenerate a wire layer"
        command "ast" { Stages.generate "ast" }
        command "proto" { Stages.generate "proto" }
    }

if Args.scriptName () = ValueSome "generate-wire.fsx" then
    exit (rootCommandOfScript { generateCommands })
```

```fsharp
// build.fsx
#load "tools/generate-wire.fsx"
open Partas.Build

exit (
    rootCommandOfScript {
        description "The repository build"

        ``Generate-wire``.generateCommands

        command "test" { stage "test" { echo "testing" } }
    })
```

`dotnet fsi build.fsx -- generate ast --help` lists `--target` with its two legal values; `dotnet fsi
tools/generate-wire.fsx -- generate ast --help` prints the same thing, from the same declaration.

### The module name `#load` gives a file

F# derives the module name from the filename, capitalising the first letter and wrapping the whole in double
backticks if a character is illegal in an identifier. `tools/generate-wire.fsx` becomes `` `Generate-wire` ``,
not `GenerateWire` or `Generate_wire`. An `open` of the wrong guess fails to compile, naming a module that
does not exist.

### Names must be unique among siblings

`System.CommandLine` builds a lookup keyed by command name. Yielding the loaded `generate` command into
another command also called `generate` throws
`ArgumentException: An item with the same key has already been added. Key: generate`. Yield it at a level
where its name is free, or wrap it in a differently-named parent:

```fsharp
command "wire" {
    description "Everything wire-related"
    ``Generate-wire``.generateCommands
}
```

### What this replaces

Without it, four scripts collapse into one with a `--only <string>` flag whose legal values live only in its
description string: the four layer names spelled once
in the flag and once in a dispatching `match`, nothing checking the two agree, and four `fsi` startups each
resolving NuGet for a run touching all four. Composition gives four `command` bindings instead — `--help`
lists them because they exist, and one process resolves packages once.

## Reference

- [Guide](index.fsx) — steps, conditions, inputs, output, timeouts, `Baked`.
- [API reference](https://shayanhabibi.github.io/Partas.Build/reference/) — every custom operation, from its XML documentation.
*)
