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

open Partas.Build
open Partas.Build.Internal

(**
# Partas.Build

A [Fun.Build](https://github.com/slaveOftime/Fun.Build)-style pipeline DSL whose pipelines *declare the CLI
inputs they need*. Commands derive their [System.CommandLine](https://github.com/dotnet/command-line-api)
option set from the pipelines they run, so options, validation and `--help` are generated from the pipeline
definition instead of registered by hand.

The design follows from one constraint. To register an option you must know it exists; to read its value you
need a `ParseResult`, which only exists after registration. Input collection is therefore **applicative, not
monadic**: an `InputSpec<'T>` carries `Inputs: ActionInput list` alongside `Read: ParseResult -> 'T`, so the
input set is readable *without* a parse result. That is the entire trick.

This page is a literate script — every snippet is compiled when the docs are built, so it cannot drift from
the API.

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
`stage` yields into `pipeline`, `pipeline` yields into `command`, `command` is added to `rootCommand`. Nothing
executes until the root command is invoked.

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

let project = "src/My Project/My Project.fsproj"

let interpolated =
    stage "build" {
        run (cmd $"dotnet build {project}")   // one argument, space and all
    }

(**
`runSensitive` takes a `FormattableString` directly — no `cmd` needed — and masks every hole as `***` in the
log while passing the real value to the process:
*)

let password = "hunter2"

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

This is where the library departs from Fun.Build. A stage that needs a CLI flag binds it in an `inputs` CE:
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
`InputsBuilder` deliberately has **no `Bind`**. A sequential `let!` would let the second source depend on the
first's *value*, which is unknowable before parsing — so the input set would no longer be statically readable.
Omitting `Bind` turns that mistake into a compile error (`FS0708`) rather than a silently incomplete option
set.

### Harvesting upward

A pipeline containing at least one `InputSpec<StageContext>` becomes an `InputSpec<PipelineContext>`, unioning
the inputs of every stage. The command registers exactly those, so `--quick` and `--configuration` appear
under `build --help` without being named anywhere but the stages that read them:
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
A command with no pipelines is a grouping node: it gets no action, so System.CommandLine reports the missing
subcommand and prints help instead of succeeding silently.

## Composition

### Stages nest

`Step` is `StepFn | StepOfStage`, so a nested stage *is* one step of its parent and stages nest arbitrarily.
That is the whole composition story — there is no separate grouping concept:
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

Settings resolve by walking `ParentContext` upward — stage, then parent stage, then pipeline. A pipeline-level
`workingDir` or `envVars` is the default for every stage that does not override it:
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
order.

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
The bound is exact — a stage set to `2` never has a third step in flight. That is worth stating because it is
easy to implement otherwise: the per-step `Async.StartChild` that applies `timeoutForStep` also *starts* the
step, so if it happened while producing the step sequence the throttle would gate the waiting, not the
running, and one extra step would already be underway.

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

A timeout cancels the ambient `CancellationToken`, and the command runner kills the whole process tree from a
token registration. That detail matters: killing from an `Async.OnCancel` continuation instead kills the child
and silently leaves its grandchildren running.

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

**Registering options on the root so every command has them.** The point of the design is that `build --help`
lists `--configuration` *because* a build stage binds it. Hand-registering reintroduces the drift the library
exists to remove.

**Expecting a second condition to replace the first.** Conditions conjoin; settings do not. A second
`whenBranch` narrows to both branches at once (so: never), where a second `parallel'` silently throws the
first away. To widen a condition, put the alternatives in one `whenAny { }`.

**Marking a CE entry member `inline` when it applies a `Build*` alias.** Those aliases are plain function
types, not delegates; inlining an application of one fails Release builds with `FS1118` while Debug compiles
cleanly. `Run` members are the usual offenders.

## API reference

The [API reference](reference/index.html) is generated from the XML documentation on each custom operation.
*)
