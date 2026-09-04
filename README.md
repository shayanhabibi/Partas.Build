# Partas.Build

An F# build-pipeline DSL where a stage declares the CLI options it reads, and a command derives its
`System.CommandLine` option set from the stages it runs. Options, validation and help text are generated from
the pipeline definition instead of registered by hand. It runs from a `.fsx` script or a build project.

- **Documentation:** <https://shayanhabibi.github.io/Partas.Build>
- **Every operation, one line each:** [`docs/CAPABILITIES.md`](docs/CAPABILITIES.md)
  ([rendered](https://shayanhabibi.github.io/Partas.Build/CAPABILITIES.html))
- **Agents:** start at <https://shayanhabibi.github.io/Partas.Build/llms.txt>

> The entire [`FSharp.SystemCommandLine`](https://github.com/jordanmarr/FSharp.SystemCommandLine) was essentially just copy pasted directly into this repo. All credit to the original author.
> Much of the pipeline implementation is also copied from [`Fun.Build`](https://github.com/slaveOfTime/Fun.Build). All credit to the original author.

## Options are declared where they are read

A flag that a stage reads but the CLI does not accept is not expressible. Neither is a flag registered on a
command whose stages ignore it. Both are routine failures in hand-wired `System.CommandLine` setups; here the
option set of a command *is* the union of what its stages bind — the two agree by construction — and two
stages binding the same option register it once.

```fsharp
module Options =
    let quick =
        Input.option<bool> "--quick"
        |> Input.alias "-q"
        |> Input.desc "Skip restores and cleaning"

    let config =
        Input.option<string> "--configuration"
        |> Input.alias "-c"
        |> Input.def "Release"
        |> Input.helpName "Debug|Release"
        |> Input.acceptOnlyFromAmong [ "Debug"; "Release" ]
        |> Input.desc "Build configuration"

module Stages =
    let restore = input {
        let! quick = Options.quick

        return stage "restore" {
            when' (not quick)
            run "dotnet restore"
        }
    }

    let build = input {
        let! config = Options.config

        return stage "build" {
            run (cmd $"dotnet build -c {config}")
        }
    }

    let test = input {
        let! config = Options.config

        return stage "test" {
            run (cmd $"dotnet test -c {config} --no-build")
        }
    }

exit (
    rootCommandOfScript {
        description "The repository build"

        command "build" {
            description "Restore and build"
            Stages.restore
            Stages.build
        }

        command "test" {
            description "Restore, build and test"
            Stages.restore
            Stages.build
            Stages.test
        }
    })
```

The command registers nothing. `dotnet fsi build.fsx -- test --help`:

```
Description:
  Restore, build and test

Usage:
  build.fsx test [options]

Options:
  -q, --quick                          Skip restores and cleaning
  -c, --configuration <Debug|Release>  Build configuration [default: Release]
  -?, -h, --help                       Show help and usage information
```

`--configuration` is bound by two of the three stages and appears once. `--quick` reaches `test` only because
`Stages.restore` is in it: delete that one line and the flag leaves `test --help` in the same edit.

## Did you look for this?

| You want | Use                                                                                                                        |
|---|----------------------------------------------------------------------------------------------------------------------------|
| An environment variable for one stage and its children | `envVars` on the stage — it is applied to the child process, so your own environment is untouched and needs no restore     |
| A secret in a command line | `runSensitive $"..."`, or `Cmd.secretOption` — every hole is masked `***` wherever the library prints it                   |
| A stage's output only when it fails | `captureOutput`                                                                                                            |
| Another script's commands as subcommands | `#load` it and yield the `Command` value — see [Composition](https://shayanhabibi.github.io/Partas.Build/composition.html) |
| An option with a fixed set of legal values, each bound to a typed value | `Input.mapFromAmong`                                                                                                       |
| A flag added to a command line only sometimes | `Cmd.argIf`, or `Cmd.argWhenSome`                                                                                          |
| A working directory for a stage's children | `workingDir` on the parent — it is inherited                                                                               |
| A stage that exists only when an option has a value | `whenSome`, which yields no stage for `None` rather than an inactive one                                                   |
| A block of stages parameterised by an option someone else declares | Take an `InputSpec<'T>` parameter and `let!` it                                                                            |
| The root command to call itself something other than the script's filename | `name` on `rootCommand`                                                                                                    |

The right-hand column in full is [`docs/CAPABILITIES.md`](docs/CAPABILITIES.md)
([rendered](https://shayanhabibi.github.io/Partas.Build/CAPABILITIES.html)).

## Composition

`command { stage; stage }` is the common form. Consecutive stages yielded into a command become one pipeline
that takes the command's name and description, which is what a build script usually wants.

`pipeline "name" { }` is for the two cases that shape does not cover: running several pipelines under one
command, and giving a pipeline a name and description of its own. `Command.pipeline { }` is the middle ground
— an explicit block, so the pipeline-level settings have somewhere to go, still named after the command:

```fsharp
let test =
    command "test" {
        description "Builds and runs the test suite"
        Command.pipeline {
            workingDir root
            Prelude.restore
            ProjectManagement.buildAll
            Tests.execute
        }
    }
```

Stages nest to any depth — a stage inside a stage is one step of its parent — and a command tree is an
ordinary value, so a `Command` built in one script is yielded into another after a `#load`. See
[Composing reusable blocks](https://shayanhabibi.github.io/Partas.Build/composition.html).

## Motivation

I hate CICD/CLI plumbing.

At the same time, it saves me from headache when I return to projects later.

![meme](/public/programming-meme-2.jpg)

`System.CommandLine` is great, comes with lots of batteries, and there exists
a great enough wrapper for it with `FSharp.SystemCommandLine`.

`Fun.Build` looks good for a github yaml type vibe of making workflows. But a majority
of it is hampered by the outdated command line parsing, and lack of typing.

So I combined `Fun.Build` with the strong typing of `FSharp.SystemCommandLine` builders,
and dog fed it to this repos own CI/CD plumbing.

So this begs the question: do I get friends now?

> **No.** *This still sounds useless*

Rude.

# Development

## Build CLI

Every repository task runs through the `Build` project rather than a script, so
the tasks are typed, debuggable, and discoverable:

```shell
dotnet run --project Build.fsproj -- --help
```

| Command | What it does |
|---------|--------------|
| `build` | Restores and builds the solution |
| `test` | Builds and runs the Expecto suites |
| `publish` | Packs and pushes to NuGet (`--nuget-key`; falls back to the `local` feed) |
| `bump` | Rewrites `<Version>` in a project file (`-p <project>`) |
| `docs` | Builds the fsdocs site (`--watch` to serve it) |

Flags belong to the commands whose stages read them: `--quick` skips restores
and the clean, `--skip-tests` skips the suites, `--configuration` picks the
configuration. None of them is registered by hand — see *Adding a step*.

## Versioning

Versions live in the project files, not in a notes file or on the command line:

```shell
dotnet run --project Build.fsproj -- bump           -p build   # patch, the default
dotnet run --project Build.fsproj -- bump minor     -p build external-annotations
dotnet run --project Build.fsproj -- bump rc        -p build   # 0.2.0 -> 0.2.1-rc.1
dotnet run --project Build.fsproj -- bump 2.0.0-nightly.7 -p build
```

Each packable project carries a `<Version>` and an `<AssemblyVersion>`, and a
bump rewrites both — the second as `<major>.0.0.0`, so it only moves when the
major does. That is deliberate: an assembly's version is its identity to
everything already compiled against it, and moving it on a patch bump breaks
anything not rebuilt in the same pass.

`pack` passes no version property, so CI publishes what the project file says.
`bump` is skipped when `--ci` is set — which it is by default under GitHub
Actions — so a version is bumped locally and committed, never invented on a
runner. Add a project to `Project.allProjects` in `Build/Program.fs` to make it
a bump target and have it packed.

## Layout

```
Build.fsproj              the build CLI
Build/
  Program.fs              the repository paths, options, stages and commands
src/Partas.Build/         the library
docs/                     the fsdocs site
tests/                    the Expecto suites
```

### Adding a project

`Build/Program.fs` addresses the repository through
`Partas.TypeProvider.BuildHelper`, so paths are checked when the build project
compiles. After adding a project, register it in `Project.allProjects`, which is
simultaneously what `bump` can version and what `pack` packs:

```fsharp
module Project =
    let allProjects =
        [
            "build", Repo.Project.``Partas.Build``.Path
            "new-thing", Repo.Project.``Partas.NewThing``.Path
        ]
```

A typo, or a project renamed without updating the build, then fails at compile
time rather than halfway through a release.

### Adding a step

A step is a stage of a pipeline. A stage that needs a flag binds it in an
`input { }` block, which is also what makes the flag appear in `--help`:

```fsharp
let myStep = input {
    let! quick = Options.quick

    return stage "my step" {
        when' (not quick)
        run (cmd $"dotnet ... {Repo.Project.``Partas.Build``.Path}")
    }
}
```

Yield it into any command. The condition stays in the stage, so the command
carries no flags of its own, and adding the stage to a second command registers
`--quick` there too.
