(**
---
title: Partas.Build
order: 0
---
*)
(*** hide ***)
// #load-ed rather than #r-ed: the guide type-checks against the code as written, not the last build, and no
// #r-ed assembly holds a file lock — one held open by the site watcher (`dotnet run --project Build.fsproj --
// docs --watch`) breaks a rebuild of the library on Windows.
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
open Partas.Build.Internal

(**


<img src="\img\sun-ztu.jpeg" width="50%" />

Command line and build pipelines in F#. Composable, hints of elderberry, thick in tannins, a glorious vintage.

A pipeline DSL based on [Fun.Build](https://github.com/slaveOftime/Fun.Build), scaffolded over
[FSharp.SystemCommandLine](https://github.com/jordanmarr/FSharp.SystemCommandLine), for no-nonsense CLIs. CLI
inputs are declared where used and lifted into the command line help of the commands that run them, with
automatic validation and help.

## Layers

| Layer | CE | Produces |
|---|---|---|
| Step | `run`, `echo`, … | one action inside a stage |
| Stage | `stage "name" { }` | `StageContext` |
| Inputs | `inputs { }` | `InputSpec<'T>` |
| Pipeline | `pipeline "name" { }` | `PipelineContext` or `InputSpec<PipelineContext>` |
| Command | `command "name" { }` | `System.CommandLine.Command` |
| Root | `rootCommand argv { }` | `int` exit code — **it runs immediately** |

## A first pipeline
*)

let hello =
    pipeline "hello" {
        workingDir __SOURCE_DIRECTORY__

        stage "greet" {
            echo "building"
            run "dotnet --version"
        }
    }

(**

`step` -> `stage` -> `stage` -> ... -> `pipeline` -> `command` -> `rootCommand`. Nothing runs until the root
command does.

## Steps

A step is anything yielded inside a `stage`. `run` is heavily overloaded; three overloads matter:
*)

let steps =
    stage "steps" {
        // a whole command line, split on whitespace honouring quotes
        run "dotnet build --no-restore"

        // executable and arguments kept apart
        run "dotnet" "build --no-restore"

        // an F# function; also Async<_>, Task<_>, StageContext -> _ and Result-returning variants
        run (fun (ctx: StageContext) -> printfn "%s" ctx.Name)
    }

(**
### Interpolation: use `cmd`

`run $"..."` binds the **`string`** overload, which flattens the holes and re-splits the result on whitespace.
A path containing a space becomes two arguments. Route interpolation through `cmd` instead: it keeps each hole
as exactly one argument and lets the platform do the escaping:
*)

                                   // v------- will break if directly passed verbatim
let project = "src/My Project/My Project.fsproj"

let interpolated =
    stage "build" {
        run (cmd $"dotnet build {project}")   // one argument, space and all
    }

(**
`runSensitive` takes a `FormattableString` directly, needs no `cmd`, and masks every hole as `***` in the log
while passing the real value to the process:
*)

let password = "drowssap"

let login =
    stage "login" {
        runSensitive $"docker login -u me -p {password}"
    }

(**
It masks *every* hole. Build a `Cmd` by hand when only one argument is secret. `Secrets` is a set of argument
indices:
*)

let pushArgs = [ "nuget"; "push"; "bin/x.nupkg"; "--api-key"; password ]

let push =
    stage "push" {
        run { Cmd.ofList "dotnet" pushArgs with Secrets = Set.singleton 4 }
    }

(**
*New in >0.2.2*: wrap sensitive strings with `Cmd.secret` or `Cmd.sensitive`. Every command runner picks them
up and quotes any string containing spaces.
*)

let pushSecret =
    stage "push" {
        run $"dotnet nuget push bin/x.nupkg --api-key {Cmd.secret password}"
    }

(**
## Conditions

`when'` and its friends set whether a stage runs. They **conjoin**. A second condition narrows the first
rather than replacing it:
*)

let conditional =
    stage "release only" {
        whenBranch "master"
        whenNot { envVar "CI" }   // master AND not CI
        run "dotnet pack"
    }

(**
`whenAll`, `whenAny` and `whenNot` are CEs that combine leaf conditions (`branch`, `branches`, `envVar`,
`platformWindows`, `platformLinux`, `platformOSX`, and a literal `when'`). An empty `whenAll { }` is active,
the identity of `forall`. An empty `whenAny { }` is not.
*)

let combined =
    stage "publish" {
        whenAny {
            branch "master"
            envVar "FORCE_PUBLISH"
        }
        run "dotnet nuget push"
    }

(**
`when'` also accepts a whole `StageContext`. It runs for real, side effects and console output included, and
its success is the answer.

## Inputs

A stage that needs a CLI flag binds it in an `inputs` CE. It is then lifted into any command that asks for it,
with no further wiring:
*)

module Options =
    let quick =
        Input.option<bool> "--quick"
        |> Input.alias "-q"
        |> Input.desc "Skip restores and cleaning"

    let config =
        Input.option<string> "--configuration"
        |> Input.alias "-c"
        |> Input.def "Release"
        |> Input.acceptOnlyFromAmong [ "Debug"; "Release" ]

let restore =
    input {
        let! quick = Options.quick

        return stage "restore" {
            when' (not quick)
            run "dotnet restore"
        }
    }

(**
Bind several sources with `and!`, never with nested `let!`:
*)

let build =
    input {
        let! quick = Options.quick
        and! config = Options.config

        return stage "build" {
            when' (not quick)
            run (cmd $"dotnet build -c {config}")
        }
    }

(**
The CE rejects a second `let!`; bind every source in one `let! … and! …` block. Binding this way is what lifts
flags into the command line help without first evaluating pipelines.

### Harvesting upward

A pipeline or stage that asks for an input, or nests an `input` request, is wrapped in an `InputSpec<'T>`,
which tracks inputs and unions them by reference.

`--quick` and `--configuration` appear under `build --help` without being named anywhere but the
stages that read them:

*)

let buildCommand =
    command "build" {
        description "Restores and builds the solution"

        pipeline "build" {
            workingDir __SOURCE_DIRECTORY__
            restore
            build
        }
    }

(**
Flags sit on the commands whose stages read them, not on the root. `addInput` covers the remainder: flags no
pipeline asks for that a root command still wants to expose.

## Wiring the root

`rootCommand` parses and invokes immediately, returning the process exit code. It belongs in `main`:
*)

let main argv =
    rootCommand argv {
        description "My build"
        addCommands [ buildCommand ]
    }

(**
A command with no pipelines is a grouping node: it gets no action, so System.CommandLine reports the missing
subcommand and prints help instead of succeeding silently.

## Composition

### Stages nest

A stage can be yielded inside another stage, arbitrarily deep, with no separate grouping concept:
*)

let nested =
    stage "outer" {
        run "dotnet --version"

        stage "inner" {
            whenWindows
            run "dotnet --info"
        }
    }

(**
### Settings inherit

Settings resolve outward: stage, then parent stage, then pipeline. A pipeline-level `workingDir` or `envVars`
defaults every stage that does not override it:
*)

let inherited =
    pipeline "inherited" {
        workingDir __SOURCE_DIRECTORY__
        envVars [ ("CI", "true") ]
        timeoutForStage 300<second>

        stage "uses the pipeline's dir and env" { run "dotnet --info" }
        stage "overrides just the dir" {
            workingDir __SOURCE_DIRECTORY__
            run "dotnet --version"
        }
    }

(**
### Stages are values

A stage is an ordinary value, so reuse is ordinary F#. Return them from functions, put them in lists, iterate
over them:
*)

let testProject (name: string) =
    stage $"test {name}" { run (cmd $"dotnet test {name}") }

let testAll =
    pipeline "test" {
        for proj in [ "A.fsproj"; "B.fsproj" ] do
            testProject proj
    }

(**
The same works one layer up: a `pipeline` is a value, and a `command` can run several in declaration order.
[Composing reusable blocks](composition.fsx) covers nesting, lists of blocks, and stages that carry their own
inputs.

### Nameless Pipelines

A short CLI command often runs a single pipeline named the same as the command. `Command.pipeline` covers
this: it inherits its description and name from the command it is defined within, essentially `pipeline null
{ }`.

*)

let namelessPipe =
    command "build" {
        description "Build projects"
        Command.pipeline {
            stage "build" {
                run "dotnet build"
            }
        }
    }



(**
### Command-level defaults

A `command` also takes the pipeline settings: `workingDir`, `envVars`, `timeout`, `timeoutForStage`,
`timeoutForStep`, `acceptExitCodes`, `outputTo`, `silentOutput`, `captureOutput`, `redirectOutput`,
`noPrefixForStep`, `noStdRedirectForStep`, `runBeforeEachStage`, `runAfterEachStage`, `post`, `verbosity`,
`verbose` and `quiet`. Set on the command, these are **defaults for every pipeline the command runs**, saving
repetition across pipelines:
*)

let ciCommand =
    command "ci" {
        workingDir __SOURCE_DIRECTORY__
        envVars [ ("CI", "true") ]
        quiet

        pipeline "build" { stage "build" { run "dotnet build" } }
        pipeline "test" {
            verbosity Verbosity.Verbose        // this pipeline says its own piece; see below
            stage "test" { run "dotnet test" }
        }
    }

(**
A default never overwrites a pipeline's own setting: the pipeline wins regardless of write order. A default
written *below* the pipelines still applies, because defaults are folded in once the whole command is built,
not as each pipeline is yielded.

A setting counts as the pipeline's own once it differs from a freshly created pipeline's initial value:

- `workingDir`, the timeouts, `outputTo` and friends, `verbosity`: hand over as soon as the pipeline names them.
- `post`: hands over as soon as the pipeline declares a post stage.
- `acceptExitCodes`: hands over as soon as it widens the set beyond `0`.
- the hooks: hand over as soon as one is installed.
- `envVars`: merges per variable — keys the pipeline set stay, the rest arrive from the command.
- `noPrefixForStep`, `noStdRedirectForStep`: plain booleans with no "unset" state, so setting one to its
  existing value is indistinguishable from not setting it.

Stages are never touched: a command default fills in pipeline settings only.

## Advanced

### Parallelism

`parallel'` makes a stage run its steps concurrently. It takes a flag, a throttle, or a function returning
either: `StageContext -> bool`, `-> int voption`, `-> Choice<bool, int>`, `-> Choice<int, bool>`. Every
overload resolves to the same `int voption`:

| You write | Resolves to | Behaviour |
|---|---|---|
| nothing | `ValueNone` | sequential |
| `parallel' false` | `ValueNone` | sequential |
| `parallel' 1` | `ValueSome 1` | sequential |
| `parallel' n` (`n > 1`) | `ValueSome n` | at most `n` steps in flight |
| `parallel'`, `parallel' true` | `ValueSome -1` | unbounded |
| `parallel' 0`, `parallel' -1` | `ValueSome n`, `n < 1` | unbounded |

*)

let fanOut =
    stage "fan out" {
        parallel' 2
        run "dotnet build A.fsproj"
        run "dotnet build B.fsproj"
        run "dotnet build C.fsproj"
    }

(**
The bound is exact: a stage set to `2` never has a third step in flight.

To choose a mode at runtime, return the choice from a single condition rather than writing two operations:
*)

let adaptive =
    stage "fan out" {
        parallel' (fun (_: StageContext) -> if System.Environment.ProcessorCount > 4 then ValueSome 4 else ValueNone)
        run "dotnet build A.fsproj"
        run "dotnet build B.fsproj"
    }

(**
### Settings overwrite, conditions conjoin

The two halves of the stage CE compose differently. Mixing them up is the most common surprise. `parallel'`,
`workingDir`, `timeout` and the rest are **settings**: the last one written wins and an earlier one leaves no
trace.
*)

let lastWins =
    stage "settings" {
        parallel' 4
        parallel' false   // sequential; the 4 is gone, not combined with
        run "dotnet build"
    }

(**
`when'`, `whenBranch`, `whenWindows` and the rest are **conditions**: each narrows the stage to the logical AND
of everything declared so far, so a second condition can only make the stage run less often.
*)

let narrows =
    stage "conditions" {
        whenBranch "master"
        whenWindows       // master AND Windows, not Windows instead of master
        run "dotnet pack"
    }

(**
To widen a condition, write **one** `whenAny { }` containing both alternatives: a second operation would
narrow instead. To switch parallel modes, write **one** condition function returning the mode: a second
operation would discard the first.

### Timeouts and cancellation

Three scopes are settable on a stage or a pipeline: `timeout` (the stage or pipeline as a whole),
`timeoutForStage` and `timeoutForStep`, each accepting `int<second>`, `float` seconds, or a `TimeSpan`.

A timeout cancels the stage and kills the whole process tree it started, grandchildren included.

### Post stages

`post` stages run after the main stages whether or not the pipeline succeeded: the place for teardown.
*)

let withTeardown =
    pipeline "integration" {
        stage "up" { run "docker compose up -d" }
        stage "test" { run "dotnet test" }

        post [ stage "down" { run "docker compose down" } ]
    }

(**
### Failure control

- `continueStepsOnFailure` keeps a stage going after a failed step.
- `continueStageOnFailure` keeps the pipeline going after a failed stage.
- `continueOnStepFailure` sets both.
- `acceptExitCodes` widens what counts as success (the default is `0`).
- `failIfIgnored` turns a skipped stage into a failure.

### Hooks

`runBeforeEachStage` and `runAfterEachStage` take a `StageContext -> unit` and fire around every stage in the
pipeline.

### Where step output goes

By default a step's output goes straight to the console. `outputTo` redirects it, inherited by sub-stages like
any other setting:

| | |
|---|---|
| `silentOutput` | dropped |
| `captureOutput` | held, and lifted into the error message if a step fails |
| `redirectOutput (fun stream line -> …)` | handed over line by line, as it arrives |
| `outputTo sink` | any of the above as a `StageOutput` value, for when the choice is made at run time |

The common case is a test run: silent when it passes, and its own output as the reason when it does not.
*)

let quietTests =
    pipeline "test" {
        stage "test" {
            captureOutput
            run "dotnet test"
        }
    }

(**
`captureOutput` lifts stderr into the step's error, or everything written if there was none. A failing stage
still says why, on the console and in the GitHub Actions annotation.

Pass an `OutputCapture` to keep the lines regardless of outcome:

```fsharp
let log = OutputCapture.create()

let audited =
    pipeline "audit" {
        stage "scan" {
            captureOutput log
            run "dotnet list package --vulnerable"
        }

        post [ stage "report" { run (fun _ -> File.WriteAllText ("scan.log", OutputCapture.text log)) } ]
    }
```

- `OutputCapture.lines`: both streams, in the order they arrived.
- `OutputCapture.errors`: stderr only.
- `OutputCapture.text` / `OutputCapture.errorText`: the same, joined.
- `OutputCapture.failureText`: what a failure lifts.

It does not cover three things:

- The pipeline's own log (stage rules, command lines, timings) is `verbosity`, not `outputTo`. A stage wanting
  both quiet needs `quiet` *and* `silentOutput`.
- A bare `printfn` inside a step is not routable. Use `echo`, or `StageContext.writeLine ctx StdStream.Out`.
- `noStdRedirectForStep` overrides all of it: without redirection there is no stream to route.


## Baked: the batteries

`Partas.Build.Baked` is the layer of options every build CLI ends up writing anyway, ready made, documented,
and shipped as its own package. Each declaration is a `BuildOption<'T>`: `.option` the flag, `.argument` the
positional equivalent.

| | |
|---|---|
| `Baked.Dotnet.config` | `configuration`/`-c`, over `release`/`r`/`debug`/`d` case-insensitively, as `string option` |
| `Baked.NuGet.apiKey` | `nuget-key`/`--nuget`/`-k`, help name `APIKEY`, defaulting to `$NUGET_API_KEY` |
| `Baked.SemVer.bump` | `bump`, parsed to a `Bump` DU over `major\|minor\|patch\|alpha\|beta\|rc\|preview\|<SEMVER>`, defaulting to `patch` |
| `Baked.Common.isCI` | `--ci`, defaulting to true when the environment looks like CI |

`BuildOption.map`, `.mapOpt` and `.mapArg` apply an `Input.*` combinator to both forms or to one. Both forms
are `ActionInput` values, so they bind in an `inputs` CE exactly as a hand-rolled option does:
*)


let packaging =
    input {
        let! config = Baked.Dotnet.config.option
        and! key = Baked.NuGet.apiKey.option

        let config = Option.defaultValue "Release" config

        return stage "pack" {
            when' key.IsSome
            run (cmd $"dotnet pack -c {config}")
        }
    }

(**
### Versioning a project file

`Baked.SemVer.Version` is semantic-version arithmetic over the `Bump` DU. `Baked.SemVer.Version.IO` applies it
to a project file. The `bump` stage is ready made: it binds `--ci`, skips itself when set, and takes the
projects to edit as an `InputSpec<string list>`, so the CLI decides what `--project` accepts.

```fsharp
let bumpAsArgument = Baked.SemVer.Stages.bumpArgument Options.projects   // <command> minor -p MyLib
let bumpAsOption   = Baked.SemVer.Stages.bumpOption Options.projects     // <command> --bump minor -p MyLib
```

`Baked.SemVer.Version.IO.writeVersion` rewrites `<Version>` and `<AssemblyVersion>` in the first
`PropertyGroup`, adding either if absent, and returns the previous `<Version>`. It skips the `<?xml ?>`
declaration and byte-order mark `XDocument.Save` would otherwise add, so a bump reads as a one-line diff.

`<Version>` is the package version and moves however it is bumped. `<AssemblyVersion>` only takes the major.
Moving it on a patch bump breaks anything not rebuilt in the same pass, with `Could not load file or assembly
'<name>, Version=…'`.

Paired with `Baked.Common.isCI`, versions are bumped locally and committed, so CI packs whatever the project
file carries rather than inventing one at build time.

The arithmetic itself:

| from | bump | to |
|---|---|---|
| `1.2.3` | `patch` | `1.2.4` |
| `1.2.3` | `minor` | `1.3.0` |
| `1.2.3` | `major` | `2.0.0` |
| `1.2.3` | `alpha` | `1.2.4-alpha.1` |
| `1.2.4-alpha.1` | `alpha` | `1.2.4-alpha.2` |
| `1.2.4-alpha.2` | `rc` | `1.2.4-rc.1` |
| `1.2.4-rc.1` | `patch` | `1.2.4` |
| anything | `Target "2.0.0-nightly.7"` | `2.0.0-nightly.7` |

A `patch` on a pre-release *releases* it rather than moving past it. `major`/`minor` drop the tag outright.
`Target` is taken verbatim, unparsed.

## Antipatterns

**Interpolating straight into `run`.** `run $"dotnet build {path}"` picks the `string` overload and re-splits
on whitespace. Use `run (cmd $"...")`.

**Nested `let!` in `inputs`.** It does not compile, by design. Use `and!`. A source that depends on another's
value needs a single input with a richer type, or a runtime check inside a step.

**Custom operations under `if` or `match`.** F# forbids it. Build the value first, then apply the operation
unconditionally:

```fsharp
// won't compile
stage "publish" { if hasKey then run pushToNuget else run pushToLocal }

// do this
let push = if hasKey then pushToNuget else pushToLocal
stage "publish" { run push }
```

A whole *stage* under an `if` is fine: an untaken branch contributes nothing.

```fsharp
pipeline "ci" {
    stage "build" { run "dotnet build" }
    if not skipTests then stage "test" { run "dotnet test" }
}
```

**Mixing `yield!` with a custom operation in the same CE.** F# rejects it (`FS3086`). Yield a list instead:
`pipeline "p" { [ yield! blocks; yield extra ] }`, and put the settings on a stage inside.

**Registering options on the root so every command has them.** `build --help` lists `--configuration`
*because* a build stage binds it. Hand-registering reintroduces the drift the library exists to remove.

**Expecting a second condition to replace the first.** Conditions conjoin. Settings do not. A second
`whenBranch` narrows to both branches at once (so: never), where a second `parallel'` silently throws the
first away. To widen a condition, put the alternatives in one `whenAny { }`.

**`runSensitive` with a bound interpolation.** `runSensitive` takes a `FormattableString`, and F# only
converts a `string` when a single overload is in play. It has no `InputSpec` form: bind the value outside the
stage and use `runSensitive $"…"` inside it as normal.

<img src="\img\the-glass.jpeg" width="400"/>

## API reference

The [API reference](https://shayanhabibi.github.io/Partas.Build/reference/) is generated from the XML
documentation on each custom operation.
*)
