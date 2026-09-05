(**
---
title: Composing reusable blocks
category: Build
order: 3
---
*)
(*** hide ***)
// The sources are #load-ed rather than #r-ing a built DLL, for two reasons: the guide then type-checks against
// the code as written instead of against the last build, and nothing holds a file lock — `fsdocs watch --eval`
// keeps a loaded assembly open for its whole lifetime, which on Windows makes rebuilding the library fail.
// Keep this list in the same order as the <Compile> items in Partas.Build.fsproj.
#r "nuget: FSharp.Control.AsyncSeq, 4.15.0"
#r "nuget: FsToolkit.ErrorHandling, 5.2.0"
#r "nuget: System.CommandLine, 2.0.11"
#r "nuget: Spectre.Console, 0.57.2"

#load "../../../src/Partas.Build/System.CommandLine/Aliases.fs"
#load "../../../src/Partas.Build/System.CommandLine/Inputs.fs"
#load "../../../src/Partas.Build/Types.fs"
#load "../../../src/Partas.Build/Process.fs"
#load "../../../src/Partas.Build/Builders/Stage.fs"
#load "../../../src/Partas.Build/Builders/Conditions.fs"
#load "../../../src/Partas.Build/Builders/Pipeline.fs"
#load "../../../src/Partas.Build/Builders/Inputs.fs"
#load "../../../src/Partas.Build/Explain.fs"
#load "../../../src/Partas.Build/Summary.fs"
#load "../../../src/Partas.Build/Builders/Command.fs"
#load "../../../src/Partas.Build/Baked.fs"

open Partas.Build
open Partas.Build.Internal

(**
# Composing reusable blocks

[The guide](index.html) introduces one stage at a time. This page is the other half: building a library of
reusable *blocks* — stages that carry their own CLI inputs — and assembling them into pipelines and commands.

Every snippet here is compiled when the docs are built, except the two file listings under
*Composition across files*, which are two separate scripts.

## The shape of a block

A block is a function returning a stage. If it needs no CLI flag it returns a `StageContext`; if it does, it
returns an `InputSpec<StageContext>` from an `input { }` CE. Both are ordinary values, and both are yieldable
anywhere a stage is.
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
        |> Input.desc "Skip restores and cleaning"

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

A pipeline takes them in declaration order and unions the options they declare. It does not matter that
`clean` is a bare stage and the other two are specs — mix them freely:
*)

let one =
    pipeline "one project" {
        workingDir __SOURCE_DIRECTORY__

        Blocks.clean "MyLib.fsproj"
        Blocks.restore "MyLib.fsproj"
        Blocks.build "MyLib.fsproj"
    }

(**
`one` is an `InputSpec<PipelineContext>` declaring `--quick`, `--configuration` and `--verbose` exactly once
each, because `build` and `restore` between them named those three.

## Loops and lists

A `for` loop over a collection works, and so does yielding a whole list. The two differ only in where the
collection comes from:
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
Both forms union the inputs of every element, so `looped` still declares `--configuration` and `--verbose`
once between three stages.

The list form is what to reach for when a custom operation would otherwise force `yield!`, which F# refuses to
mix with custom operations (`FS3086`):

```fsharp
// FS3086
pipeline "p" { yield! blocks; timeout 60.0 }

// this compiles
pipeline "p" { [ yield! blocks ] }
```

## Nesting, and inputs lifting through it

A stage nested inside another stage is one step of its parent, so blocks group without a separate concept —
and an input declared at any depth surfaces on the command that runs the pipeline.

Here the innermost stage is the only thing that names `--configuration`, and it is three levels down:
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
`ci --help` lists `--quick`, `--configuration` and `--verbose`. Nothing registered them; the stages that read
them did.

Settings placed *after* a nested block still apply to the enclosing stage, so ordering is free:
*)

let settingsAfter =
    stage "compile" {
        Blocks.build "MyLib.fsproj"
        timeout 300<second>
        whenNot { envVar "SKIP_BUILD" }
    }

(**
## Blocks that take blocks

Because a block is a value, a block factory can take other blocks as arguments. This is the usual way to build
a house style — a wrapper that adds retries, timing, teardown or a condition to whatever it is given:
*)

/// Wraps stages in a named group with a shared timeout, and a teardown that always runs.
let group name (seconds: int<second>) (stages: StageContext seq) =
    stage name {
        timeout seconds

        [ yield! stages
          yield stage $"{name} done" { echo $"finished {name}" } ]
    }

let grouped =
    pipeline "grouped" {
        group "prepare" 60<second> [ Blocks.clean "MyLib.fsproj" ]
    }

(**
When the stages being wrapped carry inputs, the wrapper takes an `InputSpec` list and returns an `InputSpec`.
The `input` CE is what joins them:
*)

let inputGroup name (seconds: int<second>) (blocks: InputSpec<StageContext> list) = input {
    let! stages = InputSpec.sequence blocks

    return stage name {
        timeout seconds
        stages
    }
}

let inputGrouped =
    pipeline "release" {
        inputGroup "compile" 600<second> [ Blocks.restore "MyLib.fsproj"; Blocks.build "MyLib.fsproj" ]
    }

(**
`InputSpec.sequence` turns a list of specs into one spec of a list, unioning the inputs; `InputSpec.traverse fn
items` is the same over a mapping. They are the two functions to know when writing this kind of wrapper.

## Adding an input of the wrapper's own

A wrapper can bind flags the wrapped blocks know nothing about. Bind them alongside the sequenced blocks:
*)

let skipTests = Input.option<bool> "--skip-tests" |> Input.desc "Build the tests but do not run them"

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

There is one shape the CE cannot express, and it is worth recognising on sight. A block that binds inputs
returns an `InputSpec<StageContext>`. If a *second* `input { }` builds one of those inside its own `return`,
the result is an `InputSpec<InputSpec<StageContext>>`, and nothing downstream accepts it:

```fsharp
// Does not work. `bumpBlock` is itself an `input { }`, so `return` wraps a spec inside a spec.
let bumpFromArgument project = input {
    let! config = Options.config
    return bumpBlock (InputSpec.ofInput Sources.bumpArgument) project
}
```

There is no `InputSpec.flatten`, and there cannot be a sound one. Flattening means reading the inner spec's
`Inputs`, which only exist once its `Read` has run, which needs the `ParseResult` that those very inputs were
supposed to configure. That circularity is the thing `InputSpec` exists to break, which is also why `input`
has no `Bind`: a sequential `let!` fails with `FS0708` rather than compiling into an option set that cannot
be registered.

The rule that follows is that **binding is a layer boundary**. One layer binds; the layers above harvest.
So when two blocks share a body but differ in where a value comes from, pass the *source* in as an
`InputSpec` rather than passing a read value out as one — the shared body keeps its single `let!`/`and!`
group and the callers vary only the spec they hand over:
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
`InputSpec.ofInput` lifts a bare `ActionInput` into a spec and `InputSpec.map` adapts its value, so a source
can be defaulted or reshaped before it is handed over. Both commands below declare `--configuration`; only
one of them declares the argument, and only the other declares `--bump`:
*)

let bumping =
    [ command "bump" { bumpFromArgument "MyLib.fsproj" }
      command "release" { bumpFromOption "MyLib.fsproj" } ]

(**
The same rule covers the simpler case where a helper needs no source of its own: give it the already-read
values as plain arguments and let the caller do all the binding. Either way, an `input { }` nested inside a
`return` is always the error.

## Commands over stages

A command does not need an explicit `pipeline`. Yield stages straight into it and they become one implicit
pipeline that takes the command's name and description:
*)

let flat =
    command "build" {
        description "Build the solution"

        Blocks.restore "MyLib.sln"
        Blocks.build "MyLib.sln"
    }

(**
Consecutive stages share that one pipeline — its settings, its run and its `whenStage` cross-references. A
command can also mix them with explicit pipelines, and declaration order is preserved.

`addInput` covers the remainder: a flag the command should expose that no stage happens to bind.

A command also takes the pipeline settings themselves - `workingDir`, `envVars`, the timeouts, the output
operations, the hooks, `post`, `verbosity` - and hands them to every pipeline it runs, including the implicit
one above. They are defaults: a pipeline that sets the same thing for itself keeps its own value. See
[command-level defaults](index.html#Command-level-defaults).

## Conditional assembly

An `if` with no `else` is fine around a whole stage or block — the untaken branch contributes nothing. (Custom
operations are the exception; F# forbids those under an `if`.)
*)

let includeDocs = System.Environment.GetEnvironmentVariable "DOCS" = "1"

let conditional =
    pipeline "release" {
        Blocks.build "MyLib.fsproj"

        if includeDocs then
            stage "docs" { run "dotnet fsdocs build" }
    }

(**
For a condition that is only known after parsing, bind it and branch inside the `input` CE, which is ordinary
F# and so has no such restriction:
*)

let maybeClean = input {
    let! quick = Options.quick

    return
        if quick then stage "skip clean" { echo "skipping clean" }
        else Blocks.clean "MyLib.fsproj"
}

(**
## Putting it together

A small but complete build, assembled entirely from blocks:
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
                    inputGroup "compile" 600<second> [ Blocks.build "MyLib.fsproj" ]

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

A `Command` is an ordinary value, and `Yield` takes one. So a script that owns a slice of the build exposes
its commands as a binding, and any other script `#load`s the file and yields the binding.

Two rules make it work:

1. **The command tree is a value.** Bind it with `let`; do not `exit` it at the point of definition.
2. **The `rootCommand` invocation is gated.** `#load` executes the loaded script top to bottom, so an
   ungated `exit (rootCommand … )` takes over the loading script's process. `Args.scriptName ()` answers the
   filename the process was launched with, which is the loaded script's own name only when it is the one
   being run.

```fsharp
// tools/generate-wire.fsx
#load "../prelude.fsx"
open Partas.Build

module Options =
    let target =
        Input.choices<string> "--target" [ "node", "node"; "browser", "browser" ]
        |> Input.def "node"
        |> Input.desc "Runtime the wire layer is generated for"

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

`dotnet fsi build.fsx -- generate ast --help` lists `--target` with its two legal values, and
`dotnet fsi tools/generate-wire.fsx -- generate ast --help` prints the same thing, from the same declaration.

### The module name `#load` gives a file

F# derives it from the filename: the first letter is capitalised, everything else is kept, and any character
illegal in an identifier forces double backticks. `tools/generate-wire.fsx` therefore becomes
`` `Generate-wire` ``, not `GenerateWire` and not `Generate_wire`. Check it once per file: an `open` of the
wrong guess fails to compile, naming the module you wrote rather than the one that exists.

### Names must be unique among siblings

`System.CommandLine` builds a lookup keyed by command name, so yielding the loaded `generate` command into
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

Without it, a build split across four scripts is one script with a `--only <string>` flag whose legal values
live in its description string, the four layer names spelled once in the flag and once in the `match` that
dispatches on it with nothing checking that the two agree, and four `fsi` startups with four NuGet resolutions
for a run that touches all four. Here the four names are the four `command` bindings, `--help` lists them
because they exist, and one process resolves packages once.

## Reference

- [Guide](index.html) — steps, conditions, inputs, output, timeouts, `Baked`.
- [API reference](reference/index.html) — every custom operation, from its XML documentation.
*)
