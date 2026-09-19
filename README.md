# Partas.Build

An F# build-pipeline DSL: a stage declares the CLI options it reads, and a command derives its
`System.CommandLine` option set from the stages it runs. Options, validation, and help text generate from the
pipeline definition instead of by hand. Runs from a `.fsx` script or a build project.

- **Documentation:** <https://shayanhabibi.github.io/Partas.Build>
- **Every operation, one line each:** [`docs/content/Build/CAPABILITIES.md`](docs/content/Build/CAPABILITIES.md)
  ([rendered](https://shayanhabibi.github.io/Partas.Build/build/capabilities/))
- **Agents:** start at <https://shayanhabibi.github.io/Partas.Build/llms.txt>

> The entire [`FSharp.SystemCommandLine`](https://github.com/jordanmarr/FSharp.SystemCommandLine) library is copied directly into this repo. All credit to the original author.
> Much of the pipeline implementation is copied from [`Fun.Build`](https://github.com/slaveOfTime/Fun.Build). All credit to the original author.

## Options are declared where they are read

A flag that a stage reads but the CLI does not accept is not expressible. Neither is a flag registered on a
command whose stages ignore it. Both are routine failures in hand-wired `System.CommandLine` setups. The
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

do exit (
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
| Another script's commands as subcommands | `#load` it and yield the `Command` value — see [Composition](https://shayanhabibi.github.io/Partas.Build/build/composition/) |
| An option with a fixed set of legal values, each bound to a typed value | `Input.mapFromAmong`                                                                                                       |
| A flag added to a command line only sometimes | `Cmd.argIf`, or `Cmd.argWhenSome`                                                                                          |
| A working directory for a stage's children | `workingDir` on the parent — it is inherited                                                                               |
| A stage that exists only when an option has a value | `whenSome`, which yields no stage for `None` rather than an inactive one                                                   |
| A block of stages parameterised by an option someone else declares | Take an `InputSpec<'T>` parameter and `let!` it                                                                            |
| The root command to call itself something other than the script's filename | `name` on `rootCommand`                                                                                                    |

The right-hand column in full is [`docs/content/Build/CAPABILITIES.md`](docs/content/Build/CAPABILITIES.md)
([rendered](https://shayanhabibi.github.io/Partas.Build/build/capabilities/)).

## Composition

`command { stage; stage }` is the common form. Consecutive stages yielded into a command become one pipeline
that takes the command's name and description — what a build script usually wants.

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
[Composing reusable blocks](https://shayanhabibi.github.io/Partas.Build/build/composition/).

## Producers, consumers and failure handlers

A `Producer<'T>` is a typed, named unit of deferred work with its own CLI inputs and its own prerequisites.
Declaring one registers its identity; nothing runs until a `consumes` stage requires it:

```fsharp
let tag = Input.option<string> "--tag" |> Input.def "v0.0.0"

let manifest =
    Producer.define "manifest" (InputSpec.ofInput tag) DependencySpec.empty (fun tag () ->
        Operation.ofAsync (fetchManifestAsync tag))

let publish =
    stage "publish" {
        retry 2
        onFailure (fun context -> printfn "%A" context.Primary)
        consumes (DependencySpec.require manifest) (fun manifest ->
            execute (cmd $"deploy --version {manifest.Version}"))
    }
```

A producer runs once per invocation and shares that one result with every consumer that requires it. Retrying
the consumer through `retry` re-runs the deploy alone, not the fetch. `onFailure` registers a handler on a
`stage` or a `pipeline` that runs once the scope's own retries are exhausted, and reads what a producer
published through `context.TryGetOutput`.

`execute` above streams the command's output and reports pass/fail. A step that needs the process's stdout as
a value reaches for `executeCapture` (fails on a rejected exit code, keeping the capture as evidence) or
`attemptCapture` (always answers the capture, rejected exit codes included, and leaves branching to the caller)
— see *Running a command from inside a step* in
[`docs/content/Build/CAPABILITIES.md`](docs/content/Build/CAPABILITIES.md#running-a-command-from-inside-a-step).

Work that does not belong in `InputSpec.Read` — whose job is to bind CLI values, not run them — moves to a
producer or a step. See *Migrating work out of `InputSpec.Read`* in
[`docs/content/Build/CAPABILITIES.md`](docs/content/Build/CAPABILITIES.md#migrating-work-out-of-inputspecread) for a worked
before/after, and the same file's *Producers and dependencies* and *Failure handlers* sections for the full
operation list, scope-retry ownership, and remaining limitations.

## Motivation

I hate CI/CD and CLI plumbing, but it saves me the headache of returning to old projects later.

![meme](public/programming-meme-2.jpg)

`System.CommandLine` is great and comes with batteries included; `FSharp.SystemCommandLine` wraps it well.
`Fun.Build` reads like GitHub Actions YAML for building workflows, but its command-line parsing is outdated
and untyped.

So this repo combines `Fun.Build`'s shape with `FSharp.SystemCommandLine`'s strong typing, and dogfoods the
result on its own CI/CD.

Do I get friends now?

> **No.** *This still sounds useless.*

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
| `docs` | Builds the Nacara site in `docs/` (`--watch` to serve it) |

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
major does. An assembly's version is its identity to everything already
compiled against it: moving it on a patch bump breaks anything not rebuilt in
the same pass.

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
src/Partas.Build.Cmd/     the command value and the process runner
src/Partas.Build.Baked/   ready-made options, stages and semver helpers
docs/                     the Nacara site (Site.fs, docs.fsproj, content/, blog/, static/)
tests/                    the Expecto suites
```

### Adding a project

`Build/Program.fs` addresses the repository through
`Partas.TypeProvider.BuildHelper`, so paths are checked when the build project
compiles. After adding a project, register it in `Project.allProjects`, which is
both what `bump` can version and what `pack` packs:

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
`input { }` block, which also makes the flag appear in `--help`:

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
