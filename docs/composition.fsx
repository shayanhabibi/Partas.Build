(**
---
title: Composing reusable blocks
category: Documentation
index: 2
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

#load "../src/Partas.Build/System.CommandLine/Aliases.fs"
#load "../src/Partas.Build/System.CommandLine/Inputs.fs"
#load "../src/Partas.Build/Types.fs"
#load "../src/Partas.Build/Process.fs"
#load "../src/Partas.Build/Builders/Stage.fs"
#load "../src/Partas.Build/Builders/Conditions.fs"
#load "../src/Partas.Build/Builders/Pipeline.fs"
#load "../src/Partas.Build/Builders/Inputs.fs"
#load "../src/Partas.Build/Builders/Command.fs"
#load "../src/Partas.Build/Baked.fs"

open Partas.Build
open Partas.Build.Internal

(**
# Composing reusable blocks

[The guide](index.html) introduces one stage at a time. This page is the other half: building a library of
reusable *blocks* — stages that carry their own CLI inputs — and assembling them into pipelines and commands.

Every snippet here is compiled when the docs are built.

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

## Reference

- [Guide](index.html) — steps, conditions, inputs, output, timeouts, `Baked`.
- [API reference](reference/index.html) — every custom operation, from its XML documentation.
*)
