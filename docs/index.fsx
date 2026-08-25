(**
---
title: Partas.Build
category: Documentation
index: 1
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
# Partas.Build

<img src=".\content\img\sun-ztu.jpeg" width="50%" />

Command line & build pipelines in F#. Composable, hints of elderberry - thick in tannins; a glorious vintage.

A pipeline DSL originally based off [Fun.Build](https://github.com/slaveOftime/Fun.Build) that scaffolds over
[FSharp.SystemCommandLine](https://github.com/jordanmarr/FSharp.SystemCommandLine) inputs and commands to build
no nonsense CLIs.

You declare CLI inputs where you use them, and they get lifted into the command line help section for the commands
the pipeline runs. Automatic validation and help generation.

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

`step` -> `stage` -> `stage` -> ... -> `pipeline` -> `command` -> `rootCommand`

No execution happens until the root command is run.

## Steps

A step is anything yielded inside a `stage`. `run` is heavily overloaded; the three worth knowing are:
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

`run $"..."` binds the **`string`** overload, which flattens the holes and then re-splits the result on
whitespace — a path containing a space becomes two arguments. Route interpolation through `cmd`, which keeps
each hole as exactly one argument and lets the platform do the escaping:
*)

                                   // v------- will break if directly passed verbatim
let project = "src/My Project/My Project.fsproj"

let interpolated =
    stage "build" {
        run (cmd $"dotnet build {project}")   // one argument, space and all
    }

(**
`runSensitive` takes a `FormattableString` directly — no `cmd` needed — and masks every hole as `***` in the
log while passing the real value to the process:
*)

let password = "drowssap"

let login =
    stage "login" {
        runSensitive $"docker login -u me -p {password}"
    }

(**
Because it masks *every* hole, build a `Cmd` by hand when only one argument is secret. `Secrets` is a set of
argument indices:
*)

let pushArgs = [ "nuget"; "push"; "bin/x.nupkg"; "--api-key"; password ]

let push =
    stage "push" {
        run { Cmd.ofList "dotnet" pushArgs with Secrets = Set.singleton 4 }
    }

(**
## Conditions

`when'` and friends set whether a stage runs. They **conjoin** — a second condition narrows the first rather
than replacing it:
*)

let conditional =
    stage "release only" {
        whenBranch "master"
        whenNot { envVar "CI" }   // master AND not CI
        run "dotnet pack"
    }

(**
`whenAll`, `whenAny` and `whenNot` are CEs that combine leaf conditions (`branch`, `branches`, `envVar`,
`platformWindows`, `platformLinux`, `platformOSX`, and a literal `when'`). An empty `whenAll { }` is active —
the identity of `forall` — while an empty `whenAny { }` is not.
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
`when'` also accepts a whole `StageContext`, which is *run for real* — side effects and console output
included — and its success taken as the answer.

## Inputs

A stage that needs a CLI flag binds it in an `inputs` CE. And then jus' fo'get abou' it! It will be lifted into
any command that asks for it. *Chef's kiss*:
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
The CE explicitly will not allow you to have a second `let!`. Bind every source in one `let! … and! …`
block. Otherwise we would not be able to lift the flags into the command line help without evaluating
pipelines.

### Harvesting upward

Whenever a pipeline or stage asks for an input, or has a nested `input` request, it becomes wrapped in
an `InputSpec<'T>`. These track inputs, and are unioned by reference.

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
Flags therefore sit on the commands whose stages read them, not on the root. `addInput` covers the
remainder — flags no pipeline asks for that a root command still wants to expose.

## Wiring the root

`rootCommand` parses and invokes immediately, returning the process exit code, so it belongs in `main`:
*)

let main argv =
    rootCommand argv {
        description "My build"
        addCommands [ buildCommand ]
    }

(**
A command with no pipelines is a grouping node: it gets no action (like me until my 20s), so System.CommandLine reports the missing
subcommand and prints help instead of succeeding silently.

## Composition

### Stages nest

A stage can be yielded inside another stage, arbitrarily deep.....

There is no separate grouping concept:
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

Settings resolve outward — stage, then parent stage, then pipeline. A pipeline-level `workingDir` or `envVars`
is the default for every stage that does not override it:
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
The same works one layer up: a `pipeline` is a value, and a `command` can run several of them in declaration
order. [Composing reusable blocks](composition.html) goes further — nesting, lists of blocks, and stages that
carry their own inputs.

### Nameless Pipelines

Short CLI programs/commands can often execute a single pipeline named the same as the command.
To help with this pattern there is `Command.pipeline`. This inherits its description and name from
the command it is defined within. It's essentially just `pipeline null { }`.

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
## Advanced

### Parallelism

`parallel'` makes a stage run its steps concurrently. It takes a flag, a throttle, or a function returning
either — `StageContext -> bool`, `-> int voption`, `-> Choice<bool, int>`, `-> Choice<int, bool>`. Every
overload lands on the same `int voption`:

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
The bound is exact — a stage set to `2` never has a third step in flight.

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

The two halves of the stage CE compose differently, and mixing them up is the most common surprise.
`parallel'`, `workingDir`, `timeout` and the rest are **settings**: the last one written wins, and an earlier
one leaves no trace.
*)

let lastWins =
    stage "settings" {
        parallel' 4
        parallel' false   // sequential; the 4 is gone, not combined with
        run "dotnet build"
    }

(**
`when'`, `whenBranch`, `whenWindows` and the rest are **conditions**: each one narrows the stage to the
logical AND of everything declared so far, so a second condition can only ever make the stage run less often.
*)

let narrows =
    stage "conditions" {
        whenBranch "master"
        whenWindows       // master AND Windows, not Windows instead of master
        run "dotnet pack"
    }

(**
Hence the two different escape hatches. To widen a condition, write **one** `whenAny { }` containing both
alternatives — a second operation would narrow. To switch between parallel modes, write **one** condition
function returning the mode — a second operation would discard the first.

### Timeouts and cancellation

Three scopes, settable on a stage or a pipeline: `timeout` (the stage or pipeline as a whole),
`timeoutForStage` and `timeoutForStep`. All three accept `int<second>`, `float` seconds, or a `TimeSpan`.

A timeout cancels the stage and kills the whole process tree it started, grandchildren included.

### Post stages

`post` stages run after the main stages whether or not the pipeline succeeded — the place for teardown:
*)

let withTeardown =
    pipeline "integration" {
        stage "up" { run "docker compose up -d" }
        stage "test" { run "dotnet test" }

        post [ stage "down" { run "docker compose down" } ]
    }

(**
### Failure control

`continueStepsOnFailure` keeps a stage going after a failed step; `continueStageOnFailure` keeps the pipeline
going after a failed stage; `continueOnStepFailure` sets both. `acceptExitCodes` widens what counts as success
(the default is `0`), and `failIfIgnored` turns a skipped stage into a failure.

### Hooks

`runBeforeEachStage` and `runAfterEachStage` take a `StageContext -> unit` and fire around every stage in the
pipeline.

### Where step output goes

By default a step's output goes straight to the console. `outputTo` sends it somewhere else, and the setting
is inherited by sub-stages the way every other setting is:

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
`captureOutput` lifts stderr if the process wrote any and everything it wrote otherwise, into the step's
error — so a failing stage still says why, on the console and in the GitHub Actions annotation.

Pass an `OutputCapture` to keep a handle on the lines whatever the outcome:

```fsharp
let log = OutputCapture()

let audited =
    pipeline "audit" {
        stage "scan" {
            captureOutput log
            run "dotnet list package --vulnerable"
        }

        post [ stage "report" { run (fun _ -> File.WriteAllText ("scan.log", log.Text)) } ]
    }
```

`Lines` is both streams in the order they arrived, `Errors` only stderr, `Text`/`ErrorText` the same joined,
and `FailureText` is what a failure lifts.

Three things it does not cover:

- The pipeline's own log — stage rules, command lines, timings — is `verbosity`, not `outputTo`. A stage that
  wants both quiet needs `quiet` *and* `silentOutput`.
- A bare `printfn` inside a step is not routable. Use `echo`, or `StageContext.writeLine ctx StdStream.Out`.
- `noStdRedirectForStep` overrides all of it: without redirection there is no stream to route.


## Baked: the batteries

`Partas.Build.Baked` is the layer of things every build CLI ends up writing anyway — the common options, ready made and described, under `Baked.Input` (options) and `Baked.Argument`
(positional arguments):

| | |
|---|---|
| `Baked.Input.DotNet.config` | `--configuration`/`-c`, parsed to a `Configuration` DU and restricted to `Debug`/`Release` |
| `Baked.Input.DotNet.configString` | the same option left as a string |
| `Baked.Input.NuGet.apiKey` | `--nuget-key`/`--nuget`, help name `APIKEY` |
| `Baked.Input.NuGet.apiKeyOrEnv` | the same, defaulting to `$NUGET_API_KEY` |
| `Baked.Input.Project.target [ … ]` | `--project`/`-p`, one or more, restricted to the names given |
| `Baked.Input.Versioning.bump` | `--bump`, parsed to a `Bump` DU |
| `Baked.Argument.Versioning.bump` | the same as a positional argument, defaulting to `patch` |
| `Baked.Input.CI.isCI` | `--ci`, defaulting to true when the environment looks like CI |

They are `ActionInput` values like any other, so they bind in an `inputs` CE exactly as a hand-rolled option
does:
*)

let packaging =
    input {
        let! config = Baked.Input.DotNet.configString
        and! key = Baked.Input.NuGet.apiKeyOrEnv

        let config = Option.defaultValue "Release" config

        return stage "pack" {
            when' key.IsSome
            run (cmd $"dotnet pack -c {config}")
        }
    }

(**
### Versioning a project file

`Baked.Version` is semantic-version arithmetic over the `Bump` DU, and `Baked.IO` applies it to a project file.
A bump command is then a stage that binds the two inputs and edits the projects it was given:

```fsharp
let bump =
    input {
        let! bump = Baked.Argument.Versioning.bump
        and! projects = Baked.Input.Project.target [ "MyLib"; "MyLib.Tool" ]
        and! ci = Baked.Input.CI.isCI

        return stage "bump" {
            when' (not ci)

            run (fun (_: StageContext) ->
                projects
                |> List.map (fun project ->
                    match Baked.IO.bumpVersion (pathOf project) bump with
                    | Ok (previous, next) -> printfn $"{project}: {previous} -> {next}"; Ok ()
                    | Error error -> Error error.Message)
                |> List.tryPick (function Error error -> Some (Error error) | Ok () -> None)
                |> Option.defaultValue (Ok ()))
        }
    }
```

`IO.writeVersion` rewrites `<Version>` and `<AssemblyVersion>` in the first `PropertyGroup`, adding either
element if it is absent, and answers what `<Version>` held before. It saves without the `<?xml ?>` declaration
and byte-order mark `XDocument.Save` would otherwise introduce, so a bump reads as a one-line diff.

The two properties are not the same string. `<Version>` is the package version and moves however you bump it;
`<AssemblyVersion>` only takes the major. Letting the assembly version move on a patch bump breaks anything not
rebuilt in the same pass with `Could not load file or assembly '<name>, Version=…'`.

Pair it with `Baked.Input.CI.isCI`, as above, and versions are bumped locally and committed rather than
invented on a runner: CI packs whatever the project file carries.

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

A `patch` on a pre-release *releases* it rather than moving past it, and `major`/`minor` drop the tag outright.
`Target` is taken verbatim, unparsed.

## Antipatterns


**Interpolating straight into `run`.** `run $"dotnet build {path}"` picks the `string` overload and re-splits
on whitespace. Use `run (cmd $"...")`.

**Nested `let!` in `inputs`.** It does not compile, by design — use `and!`. Wanting one source to depend on
another's value means you want a single input with a richer type, or a runtime check inside a step.

**Custom operations under `if` or `match`.** F# forbids it. Build the value first, then apply the operation
unconditionally:

```fsharp
// won't compile
stage "publish" { if hasKey then run pushToNuget else run pushToLocal }

// do this
let push = if hasKey then pushToNuget else pushToLocal
stage "publish" { run push }
```

A whole *stage* under an `if` is fine, though — an untaken branch simply contributes nothing:

```fsharp
pipeline "ci" {
    stage "build" { run "dotnet build" }
    if not skipTests then stage "test" { run "dotnet test" }
}
```

**Mixing `yield!` with a custom operation in the same CE.** F# rejects it (`FS3086`). Yield a list instead:
`pipeline "p" { [ yield! blocks; yield extra ] }`, and put the settings on a stage inside.

**Registering options on the root so every command has them.** The point of the design is that `build --help`
lists `--configuration` *because* a build stage binds it. Hand-registering reintroduces the drift the library
exists to remove.

**Expecting a second condition to replace the first.** Conditions conjoin; settings do not. A second
`whenBranch` narrows to both branches at once (so: never), where a second `parallel'` silently throws the
first away. To widen a condition, put the alternatives in one `whenAny { }`.

**`runSensitive` with a bound interpolation.** `runSensitive` takes a `FormattableString`, which F# will only
convert a `string` into when a single overload is in play. It has no `InputSpec` form: bind the value outside
the stage and use `runSensitive $"…"` inside it as normal.

<img src="content\img\the-glass.jpeg" width="400"/>

## API reference

The [API reference](reference/index.html) is generated from the XML documentation on each custom operation.
*)
