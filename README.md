# Partas.Build

## ⚠️ IMPORTANT: NEW RELEASES ARE ON A TEMPORARY NUGET FEED ⚠️

> [!WARNING]
> **Use the Cloudsmith feed below for new releases of Partas.Build and its companion packages.**
> I currently cannot access the old nuget.org account and am waiting for NuGet support to transfer
> package ownership to my new account. While that transfer is pending, new releases are published
> to **Cloudsmith temporarily**, under the existing package IDs. Older releases remain on nuget.org.
>
> **Feed:** <https://nuget.cloudsmith.io/shayanhabibi/shayanhabibi-partas-build/v3/index.json>
>
> Public restores require **no account or API key**. Once ownership is restored, publishing will
> return to nuget.org and this notice will be updated.

For `.fsx` scripts, add this directive before any `#r "nuget: ..."` package references:

```fsharp
#i "nuget: https://nuget.cloudsmith.io/shayanhabibi/shayanhabibi-partas-build/v3/index.json"
```

Add the temporary source alongside nuget.org so dependencies can still be restored:

```shell
dotnet nuget add source https://nuget.cloudsmith.io/shayanhabibi/shayanhabibi-partas-build/v3/index.json --name partas-build-temporary
```

For shared projects and CI, add that URL to the repository's `NuGet.Config` as well. If your
configuration uses package source mapping, map the Partas package IDs to this source.

Package hosting is provided by [Cloudsmith](https://cloudsmith.com).

An F# build-pipeline DSL: a stage declares the CLI options it reads, and a command derives its
`System.CommandLine` option set from the stages it runs. Options, validation, and help text generate from the
pipeline definition instead of by hand. Runs from a `.fsx` script or a build project.

- **Documentation:** <https://shayanhabibi.github.io/Partas.Build>
- **Every operation, one line each:** [`docs/content/Build/CAPABILITIES.md`](docs/content/Build/CAPABILITIES.md)
  ([rendered](https://shayanhabibi.github.io/Partas.Build/build/capabilities/))
- **Agents:** start at <https://shayanhabibi.github.io/Partas.Build/llms.txt>; a consumer repository can paste
  [the agent snippet](https://shayanhabibi.github.io/Partas.Build/AGENTS-snippet.md) into its own `AGENTS.md`

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
        |> Input.description "Skip restores and cleaning"

    let config =
        Input.option<string> "--configuration"
        |> Input.alias "-c"
        |> Input.def "Release"
        |> Input.helpName "Debug|Release"
        |> Input.acceptOnlyFromAmong [ "Debug"; "Release" ]
        |> Input.description "Build configuration"

module Stages =
    let restore = input {
        let! quick = Options.quick

        return stage "restore" {
            when' (not quick) "--quick is set"   // the reason --explain prints when the stage is skipped
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
| Restore, clean, build, pack, Expecto and NuGet push stages, ready made | `Partas.Build.Baked`'s `Stages` — each brings its own `--quick`/`--configuration`/`--skip-tests`/`--nuget-key`                |
| What a command would run, without running it | `<command> --explain`; `--explain --json` for the tree as JSON, `--schema` for the options                                  |
| A run's outcome for a script or an agent to read | `--json` (the last line of output) or `--report <path>`; exit codes `0` success, `1` failure, `2` usage error, `130` cancelled |
| The build as a function in a warm F# session (SageFs) | `Command.root { }` and `Command.invoke`, which answer a `RunResult` — see [Hosting](https://shayanhabibi.github.io/Partas.Build/build/hosting/) |

The right-hand column in full is [`docs/content/Build/CAPABILITIES.md`](docs/content/Build/CAPABILITIES.md)
([rendered](https://shayanhabibi.github.io/Partas.Build/build/capabilities/)).

`--json` defaults to true when the invocation environment identifies an AI coding harness. Detection follows
the environment rules in [is-ai-agent](https://github.com/sdairs/is-ai-agent#detection-order), including `AGENT`,
`AI_AGENT` and vendor markers; it does not probe the filesystem or inspect credentials. These inherited markers
are a convenience signal, not proof that an AI initiated the command.

Use `--json false` for text on one invocation, or set `PARTAS_BUILD_DISABLE_AI=1` to disable automatic agent
defaults. The override is enabled by any nonblank value except `0`, `false`, `no` or `off` (trimmed and
case-insensitive). Explicit `--json` still works with the override. `--explain` and `--schema` remain explicit:
detection changes the output format, never whether the build runs. Child process arguments are unaffected.

`AiEnvironment.detect ()` returns a `Lazy<bool>` that reads the environment only when `.Value` is requested.
Each JSON default creates a fresh detection, so a reusable command in a long-lived host sees environment
changes between invocations; a saved lazy result retains its first value. `AiEnvironment.detectWith` accepts
an environment reader for captured environments and tests.

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

## GitHub Actions reporting

When `GITHUB_ACTIONS=true`, active top-level stages automatically appear as collapsible log groups. Nested
stages and parallel branches stay inside their enclosing group's log; parallel execution is preserved.
`quiet` disables group framing.

Commands also append a Markdown stage summary to `GITHUB_STEP_SUMMARY`, with each recorded stage's outcome,
duration and failure message. Summaries include nested and skipped stages, single-stage pipelines, and quiet
or failed runs. Each pipeline appends its own report. Summary write failures, including reaching GitHub's
1 MiB per-step limit, print a diagnostic without changing the build's exit code.

These features use the runner environment automatically; no workflow YAML changes are needed. Calling
`PipelineContext.run` directly provides log groups; automatic summaries are part of command invocation.

Build scripts can also emit structured notices, warnings and errors with an optional source location:

```fsharp
stage "validate" {
    runOperation (annotate {
        Annotation.warning "This setting is deprecated." with
            Title = ValueSome "Configuration warning"
            File = ValueSome "build.fsx"
            Line = ValueSome 12
            Column = ValueSome 5
    })
}
```

Use `Annotation.notice`, `Annotation.warning` or `Annotation.error`; each creates a record whose optional
fields can be supplied as above. `EndLine` and `EndColumn` describe a range and require their corresponding
start positions. Source positions are one-based; columns require `Line` and apply only within one line.
Annotations bypass output captures and quiet logging. Outside GitHub Actions,
they print readable diagnostics with the same metadata. An error annotation reports a problem; fail the
step separately when it should stop the build. Compiler output is not automatically parsed into annotations.

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

## Repository CI

Pull requests and pushes to `master` run the full build and test gate on Linux and Windows, plus the
documentation build. CI installs the SDK selected by `global.json`,
caches NuGet packages, and retains build/test logs for seven days. Stage groups, summaries and diagnostics
come from the same build CLI used locally.

After all checks pass, master runs publish packages to the temporary Cloudsmith feed using
`CLOUDSMITH_API_KEY` and deploy documentation to the `github-pages` environment. Pull requests never
publish or deploy. A missing publishing secret fails the publish job. Manual runs validate any selected
branch; only `master` can publish or deploy. The CLI accepts `publish --nuget-source URL` for an alternate
feed; its default remains nuget.org, with the `local` fallback when no key is supplied.
New commits cancel superseded PR runs while active master publications finish. GitHub Actions are pinned
to commit SHAs and maintained by weekly grouped Dependabot updates.

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
src/extensions/Partas.Build.EasyBuild.ShipIt/   ShipIt-backed bump, release inputs, commands and explicit setup
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
