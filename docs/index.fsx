(**
---
title: Partas.Build
category: Documentation
index: 1
---
*)
(*** hide ***)
#r "nuget: FsToolkit.ErrorHandling, 5.2.0"
#r "nuget: Spectre.Console, 0.57.2"
#r "nuget: System.CommandLine, 2.0.11"
#r "../src/Partas.Build/bin/Release/net8.0/Partas.Build.dll"

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
    inputs {
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
    inputs {
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
        envVars [ "CI", "true" ]
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

## Advanced

### Parallelism

`parallel'` makes a stage run its steps concurrently. It takes a flag, a bounding num, or a function returning one or the other
`StageContext -> bool|int voption|Choice<bool|int,int|bool>`:
*)

// sequential - no parallelism
// unbounded - parallelism using threadpool
// bounded N - parallelism using N threads
let fanOut =
    stage "fan out" {
        parallel' false // sequential
        parallel' true // unbounded
        parallel' 0 // unbounded
        parallel' 1 // sequential
        parallel' 2 // bounded 2
        parallel' -1 // unbounded
        run "dotnet build A.fsproj"
        run "dotnet build B.fsproj"
    }

(**
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

**Expecting a second condition to replace the first.** Conditions conjoin. To widen rather than narrow, use a
single `whenAll` (whenAll { when' ...; whenNot { ... } })`.

**Marking a CE entry member `inline` when it applies a `Build*` alias.** Those aliases are plain function
types, not delegates; inlining an application of one fails Release builds with `FS1118` while Debug compiles
cleanly. `Run` members are the usual offenders.

## API reference

The [API reference](reference/index.html) is generated from the XML documentation on each custom operation.
*)
