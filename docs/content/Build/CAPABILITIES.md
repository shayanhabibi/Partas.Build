---
title: Capabilities
category: Build
order: 1
---

# Capabilities

Every custom operation on the four builders, every `Input` combinator, and the `Cmd` argument helpers — one
line each. Use it to find the name; the [API reference](https://shayanhabibi.github.io/Partas.Build/reference/) has the full signature and
remarks for each, and [Composing reusable blocks](composition.fsx) has worked examples.

## How settings resolve

A stage setting is answered by walking upward: the stage itself, then its parent stage, then the parent's
parent, then the pipeline. The first level that set the thing wins, so `workingDir` on a pipeline covers every
stage under it and a nested stage overrides it for itself and its own children. This applies to `workingDir`,
`envVars`, the timeouts, `acceptExitCodes`, the output sink, `noPrefixForStep`, `noStdRedirectForStep` and
`verbosity`.

Conditions are the exception. `when'`, `whenEnvVar`, `whenBranch` and the platform operations **conjoin**: a
second condition on the same stage narrows it to the logical AND of both. Use `whenAny { }` to widen.

A command's copies of the pipeline settings are **defaults**, not overrides: they reach every pipeline the
command runs, but only where that pipeline left the setting alone, whichever order the two were written in.
`noPrefixForStep` and `noStdRedirectForStep` are plain bools with no unset state: a pipeline setting either one
to the same value `PipelineContext.create` already gives it reads back identically to a pipeline that never
touched it, so the command default overwrites it in that case too.

## Timeouts

Three names, and their meaning shifts with the builder they sit on.

| Builder | `timeout` | `timeoutForStage` | `timeoutForStep` |
|---|---|---|---|
| `stage` | this stage as a whole | — | each step of this stage |
| `pipeline` | the whole pipeline run | each stage's default | each step's default |
| `command` / `rootCommand` | pipeline default for the whole run | pipeline default for each stage | pipeline default for each step |

The unit each builder takes differs. On `pipeline` all three accept `int<second>`, `float` seconds or a
`TimeSpan`. On `stage` they accept plain `int` seconds, `float` seconds or a `TimeSpan`, and on
`command`/`rootCommand` `int` seconds or a `TimeSpan`.

## Stage operations

Available inside `stage`, and inside `whenStage`, which accepts everything `stage` does.

| Operation | What it does |
|---|---|
| `run` | Adds a step. Takes a literal command line, a `Cmd`, or a function of the `StageContext` returning `unit`, `int`, `Result<unit, string>`, a `Cmd`, an `Async<_>` or a `Task<_>` of any of those, optionally wrapped in `option` |
| `runSensitive` | Adds a step from an interpolated command line with every hole masked as `***` wherever the library prints it |
| `runOperation` | Adds a step from an `Operation<unit>`, with an optional label for `--explain`. Runs under the stage's working directory, environment, acceptable exit codes and output routing |
| `runHttpHealthCheck` | Adds a step that polls a URL until it answers or the stage is cancelled |
| `echo` | Adds a step that prints a message through the stage's output sink |
| `when'` | Runs the stage only when a `bool` holds, or only when a given `StageContext` succeeds |
| `whenEnvVar` | Runs the stage only when an environment variable is set, or set to a given value; also takes an `EnvArg` |
| `whenBranch` / `whenBranches` | Runs the stage only on the named git branch. Reads `git branch --show-current` in the stage's working directory; a missing git evaluates false rather than throwing |
| `whenWindows` / `whenLinux` / `whenOSX` | Runs the stage only on that platform. Pass `false` to invert |
| `whenPlatform` | The same over an `OSPlatform` value |
| `workingDir` | The directory this stage's child processes start in. Takes a `string` or a `DirectoryInfo` |
| `envVars` | Environment variables for this stage's child processes. Applied to `ProcessStartInfo`, so the host process's own environment is untouched |
| `timeout` | Cancels the stage after the given duration |
| `timeoutForStep` | Cancels any one step of the stage after the given duration |
| `retry` | Runs the stage's steps again after a failing attempt, up to the given count. `timeout` remains the budget for the whole stage, retries included |
| `parallel'` | Runs the stage's steps concurrently. `true`/`0`/`-1` unbounded, `1`/`false` sequential, `n` throttled to exactly `n` in flight; also takes a `StageContext -> _` condition |
| `consumes` | Adds a step that reads a `DependencySpec<'D>` and runs an `Operation<unit>` over its value, and adds the required producers to this stage's prerequisites. See [Producers and dependencies](#producers-and-dependencies) |
| `onFailure` | Registers a handler that runs once, after this stage's own `retry` attempts are exhausted. See [Failure handlers](#failure-handlers) |
| `acceptExitCodes` | The exit codes that count as success. Replaces the default `[0]` |
| `failIfIgnored` | Fails the pipeline when this stage is inactive, instead of skipping it |
| `failIfNoActiveSubStage` | Fails the pipeline when none of this stage's sub-stages is active |
| `continueStepsOnFailure` | Runs the remaining steps after one fails |
| `continueStageOnFailure` | Runs the remaining stages after this one fails |
| `continueOnStepFailure` | Both of the above at once |
| `outputTo` | Sends this stage's step output to a `StageOutput` — `Console`, `Silent`, `Captured` or `Redirect` |
| `silentOutput` | Drops this stage's step output. A failure still reports its exit code |
| `captureOutput` | Holds this stage's step output back and lifts it into the error message when a step fails. Takes an optional `OutputCapture` to keep the lines either way |
| `redirectOutput` | Hands each line to `StdStream -> string -> unit` as it arrives, from both streams' reader threads |
| `noPrefixForStep` | Stops each step's output being prefixed with its stage and step index |
| `noStdRedirectForStep` | Stops redirecting the child's stdout/stderr — the mechanism every output operation above depends on — and overrides all of them |
| `shuffleExecuteSequence` | Randomises step order at each run |
| `verbosity` | How much of the pipeline's own log this stage prints. Takes `Verbosity.Quiet`, `Normal` or `Verbose` |
| `verbose` / `quiet` | `verbosity Verbose` and `verbosity Quiet` |

A stage nested inside another stage is one step of its parent. Stages nest to any depth, and a block is just
a value that a `stage`, a `pipeline` or a `command` can yield.

### Running a command from inside a step

`run` and `runSensitive` cover the common case of a command whose own exit code is the whole result. A step
built with `run (fun ctx -> ...)` reaches for one of these `Operation<'T>` functions when it needs the
command's output as a value:

| Function | What it does |
|---|---|
| `execute cmd` | Runs `cmd`, streaming its output through the stage's own output routing. Fails on an exit code the stage does not accept; carries no captured text |
| `executeCapture cmd` | Runs `cmd`, capturing stdout and stderr instead of streaming them. Fails on an unaccepted exit code the same way `execute` does, with the captured `CommandResult` attached to the failure as evidence |
| `attemptCapture cmd` | Runs `cmd`, capturing stdout and stderr, and always answers the `CommandResult` — an unaccepted exit code included. A process-start failure and a cancellation remain outcomes of their own, never a `CommandResult`; only a process that ran to completion produces one, and branching on its exit code is the caller's |

Captured stdout and stderr are the child's raw bytes, ahead of any prefix or other display formatting, and are
application data that can hold secrets: printing a successful capture is the caller's decision, not something
`executeCapture` or `attemptCapture` does on its own.

## Pipeline operations

Available inside `pipeline "name" { }` and inside `Command.pipeline { }`, which takes the name and description
of the command that runs it.

| Operation | What it does |
|---|---|
| `description` | The pipeline's description. Discarded in `Command.pipeline { }`, which always takes the command's own name and description instead |
| `timeout` | Cancels the whole pipeline after the given duration |
| `timeoutForStage` | The default `timeout` of each stage |
| `timeoutForStep` | The default `timeoutForStep` of each stage |
| `workingDir` | The default working directory of every stage. Takes a `string` or a `DirectoryInfo` |
| `envVars` | Environment variables every stage inherits. Appends to the pipeline's map rather than replacing it |
| `acceptExitCodes` | The exit codes that count as success. Replaces the default `[0]` |
| `outputTo` | The default output sink of every stage |
| `silentOutput` | Drops every stage's step output |
| `captureOutput` | Holds every stage's step output back, lifting it into the error message on failure |
| `redirectOutput` | Hands every line of step output to `StdStream -> string -> unit` |
| `noPrefixForStep` | Stops step output being prefixed with the stage and step index |
| `noStdRedirectForStep` | Stops redirecting child stdout/stderr |
| `runBeforeEachStage` | A `StageContext -> unit` hook run before each stage. Replaces the previous hook |
| `runAfterEachStage` | A `StageContext -> unit` hook run after each stage. Replaces the previous hook |
| `post` | The stages that run after the main stages whether or not the pipeline succeeded — the teardown slot. Replaces any post stages already declared |
| `verbosity` | How much the pipeline prints. Takes `Verbosity.Quiet`, `Normal` or `Verbose` |
| `verbose` / `quiet` | `verbosity Verbose` and `verbosity Quiet` |
| `onFailure` | Registers a handler that runs once per failed run, after the handlers of every stage of that run. See [Failure handlers](#failure-handlers) |

Not carried by `command`/`rootCommand` as a pipeline default: a command's `onFailure` would have to reach a
failure of `InputSpec.Read` or of CLI parsing, ahead of every pipeline it runs, which nothing observes today.

## Producers and dependencies

A `Producer<'T>` is a typed, named unit of deferred work with its own CLI inputs and its own prerequisites.
Declaring one registers its identity and harvests those inputs; nothing runs until a consumer schedules it.

| Function | What it does |
|---|---|
| `Producer.define name inputs dependencies execute` | Declares a producer: its own `InputSpec<'I>`, a `DependencySpec<'D>` of prerequisites, and the work computing `'T` from both |
| `Producer.stage` | Places a producer at this exact point of a pipeline or parent stage, rather than leaving its placement implicit |
| `DependencySpec.empty` | A specification with no prerequisites |
| `DependencySpec.require producer` | A specification requiring one producer and reading its result |
| `DependencySpec.map fn spec` | The prerequisites of `spec`, its value read through `fn` |
| `DependencySpec.map2 fn first second` | The prerequisites and inputs of both specifications, unioned, their values read through `fn` |
| `DependencySpec.zip first second` | A specification requiring both producers and reading their results as a pair |
| `Stage.consuming name dependencies execute` | A stage whose one step is `execute` run over `dependencies`, with no CLI inputs of its own |
| `Stage.consumingWith name inputs dependencies execute` | The same, plus CLI inputs the stage itself declares |

A producer's handle carries an identity allocated when it is declared; two declarations sharing a name and
arguments are distinct producers with distinct results. Depending on the same handle from more than one
consumer runs it once per invocation and shares that one result. An unlisted producer required by a stage runs
immediately before that stage, after its own prerequisites; listing it explicitly (`Producer.stage`, or yielding
it into a pipeline) fixes its position instead. A consumer running under a `parallel'` or
`shuffleExecuteSequence` scope reads a value published before that scope began; placing a producer inside such
a scope is rejected at validation, naming the producer and the scope.

A required producer that is skipped or fails leaves its consumers skipped, carrying a dependency reason. An
`Option`/`ValueOption` result models an intentional absence, distinct from a producer that failed, that a
consumer can handle directly. Retrying a consumer through `retry` reuses the successful results of producers
outside the retried scope; a producer owned by the retried scope itself gets a fresh result on each attempt,
and a failed attempt leaves no value for a later attempt to read.

## Failure handlers

`onFailure` registers a `FailureContext -> unit` handler on a `stage` or a `pipeline`. It runs once per failed
execution of that scope, after the scope exhausts its `retry` attempts, and inner handlers run before outer
ones — a stage's handler before the pipeline's. A stage a retry recovers, or a cancelled one, keeps its handlers
back: a stage's own `timeout` and `timeoutForStep` are failures of that stage, while an ancestor's token, the
pipeline's, or the invocation's is a cancellation and runs no handler.

The handler reads `FailureContext.Primary`/`.Secondary` for the causes recorded and
`FailureContext.TryGetOutput producer` for a value the invocation has already published — a lookup restricted to
already-published values, answering `ValueNone` for a producer that has not run. An exception out of a handler
is one more cause of the same scope; the original failure stays the primary, and successful reporting preserves
it. A failing handler triggers no second run of itself.

Known limitations:

- A handler is synchronous and unbounded: there is no cleanup operation and no cleanup budget separate from the
  handler's own body.
- `onFailure` has no equivalent on `command`/`rootCommand`; a failure of `InputSpec.Read` or of CLI parsing
  reaches no handler.
- A stage with no `timeoutForStep` runs its steps under the attempt's own cancellation source rather than a
  budget of its own; a step still recorded as in flight when its scope unwinds is left with its sources
  undisposed rather than raced against a straggler that may still read them.
- `FailureCause.summarise`, used in `ScopeReports`, keeps only the first line of a multi-line capture — later
  lines are lost from the report, not merely hidden from the one-line rendering.
- `OperationFailedException`'s message is written by hand for each `FailureCause` case rather than through
  `FailureCause.describe`, so the two can drift.
- `whenStageSucceeds` (the body of `whenStage`) reads the policy-folded outcome of the condition stage: one
  carrying `continueStageOnFailure` reports itself as succeeded even where it failed.
- The gate that places an unlisted producer immediately before a `whenStage` consumer evaluates that consumer's
  condition stage a second time, in addition to the evaluation `whenStage` performs on its own.
- `PipelineContext.run`, called directly rather than through a command's own invocation, skips
  `DependencyPlan.validate`: an arrangement validation would reject — a producer inside a `parallel'` scope,
  say — runs instead of failing up front.

## Migrating work out of `InputSpec.Read`

`InputSpec<'T>.Read` is a projection: `ParseResult -> 'T`, called once per invocation to bind the CLI values a
stage declared. Effects belong in a step or in a producer, not in `Read` itself — a `Read` that shells out or
writes a file runs on every path that resolves inputs, `--help` and `--explain` included, since resolution
happens ahead of the check for either flag.

Before, doing the work inside `Read`:

```fsharp
let publish =
    input {
        let! tag = Input.option<string> "--tag" |> Input.def "v0.0.0"
        // Runs on every resolution of this input, --help and --explain included.
        let manifest = fetchManifest tag
        return stage "publish" {
            run (cmd $"deploy --version {manifest.Version}")
        }
    }
```

After, the same CLI option feeding a producer, and the stage consuming its typed result:

```fsharp
let tag = Input.option<string> "--tag" |> Input.def "v0.0.0"

let manifest: Producer<Manifest> =
    Producer.define "manifest" (InputSpec.ofInput tag) DependencySpec.empty (fun tag () ->
        Operation.ofAsync (fetchManifestAsync tag))

let publish =
    pipeline "release" {
        stage "publish" {
            retry 2
            onFailure (fun context ->
                context.TryGetOutput manifest
                |> ValueOption.iter (fun manifest -> printfn $"publish failed for {manifest.Version}"))
            consumes (DependencySpec.require manifest) (fun manifest ->
                execute (cmd $"deploy --version {manifest.Version}"))
        }
    }
```

`Read` now binds only the option; `--help` and `--explain` resolve it without running `fetchManifest` or
`deploy`, since a producer runs only where its consumer is scheduled and never on either of those paths. The
consumer's `retry` repeats the deploy alone — `manifest` is required, not retried, so a failing deploy re-reads
the same published value rather than re-fetching it — and its `onFailure` reads that same value back out of the
failure it is given. The CLI layer stays applicative: `tag` is still an ordinary `ActionInput<string>`, readable
without a `ParseResult`, exactly as before.

## Command operations

Available inside `command "name" { }` and `rootCommand argv { }` / `rootCommandOfScript { }`, except for the
three marked as root-only.

| Operation | What it does |
|---|---|
| `description` | The command's description, shown in help |
| `alias` / `aliases` | Alternative names for the command. These accumulate |
| `hidden` | Keeps the command out of help output |
| `addCommand` / `addCommands` | Adds subcommands. Yielding a `Command` value does the same |
| `addInput` / `addInputs` | Registers an option or argument no pipeline asks for. Options a stage binds are registered already |
| `timeout` | Pipeline default: the whole run. Takes `int` seconds or a `TimeSpan` |
| `timeoutForStage` | Pipeline default: each stage. Takes `int` seconds or a `TimeSpan` |
| `timeoutForStep` | Pipeline default: each step. Takes `int` seconds or a `TimeSpan` |
| `workingDir` | Pipeline default: the directory commands run in |
| `envVars` | Pipeline default, per key: a pipeline that sets one of these keys itself keeps its own value and the rest still apply |
| `acceptExitCodes` | Pipeline default: the exit codes that count as success |
| `outputTo` / `silentOutput` / `captureOutput` / `redirectOutput` | Pipeline default: where step output goes |
| `noPrefixForStep` / `noStdRedirectForStep` | Pipeline default: prefixing and child stream redirection |
| `runBeforeEachStage` / `runAfterEachStage` | Pipeline default: the per-stage hooks |
| `post` | Pipeline default: the teardown stages |
| `verbosity` / `verbose` / `quiet` | Pipeline default: how much the pipeline prints |
| `name` | **Root only.** What the root command calls itself in help and usage. Defaults to the script's filename |
| `parserConfiguration` | **Root only.** A `System.CommandLine` `ParserConfiguration` |
| `invocationConfiguration` | **Root only.** A `System.CommandLine` `InvocationConfiguration` |

A command yields stages directly — `command "test" { Stages.restore; Stages.test }` — and consecutive stages
become one implicit pipeline carrying the command's name and description. `Command.pipeline { }` is the same
pipeline written out, for when it needs the pipeline-level settings; `pipeline "name" { }` is for when several
pipelines run under one command, or when one needs a name of its own.

## Condition builders

`whenAll { }`, `whenAny { }` and `whenNot { }` take these; each yields a single condition to a stage. An empty
`whenAll`/`whenNot` is always active, an empty `whenAny` never is.

| Operation | What it does |
|---|---|
| `when'` | A `bool`, or a `StageContext` that must succeed |
| `envVar` | An environment variable by name, by name and value, or as an `EnvArg` |
| `branch` / `branches` | The current git branch |
| `platformWindows` / `platformLinux` / `platformOSX` | The running platform. Pass `false` to invert |
| `platform` | The same over an `OSPlatform` value |

`whenEnv { }` describes one environment variable in place of a wall of overloads, with `name`, `description`,
`value`, `acceptValues` and `optional`. `whenStage "name" { }` runs a stage for its result — everything
`stage` accepts is accepted there, and the stage runs for real, side effects included.

`whenSome value build` and `whenOk value build` are functions rather than operations. Each returns a
`StageContext list`: the stage built from the bound value, or `[]`. The absent case is an empty list, not an
inactive stage requiring a name. `build` receives the value already unwrapped, inside the condition that guards it.

## `Input` combinators

Declaring functions:

| Function                                                  | What it makes |
|-----------------------------------------------------------|---|
| `Input.option<'T> "--name"`                               | An option bound as `'T` |
| `Input.optionMaybe<'T> "--name"`                          | An option bound as `'T option`, `None` when absent |
| `Input.argument<'T> "name"`                               | A positional argument bound as `'T` |
| `Input.argumentMaybe<'T> "name"`                          | A positional argument bound as `'T option` |
| `Input.context`                                           | Injects the `ActionContext` — the `ParseResult` and a cancellation token |
| `Input.inject value`                                      | Injects a value that is not parsed from the command line |
| `Input.ofOption` / `Input.ofArgument`                     | Lifts a raw `System.CommandLine` `Option<'T>` / `Argument<'T>` |

Shaping combinators, all `ActionInput<'T> -> ActionInput<'T>` and all pipeable:

| Function                                                            | What it does                                                                                                              |
|---------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------|
| `Input.alias` / `Input.aliases`                                     | Adds alternative names. Options only                                                                                      |
| `Input.description`, `Input.desc`                                   | The help text                                                                                                             |
| `Input.helpName`                                                    | The value placeholder in help — `<Debug\|Release>`                                                                        |
| `Input.defaultValue`, `Input.def`                                   | The value used when the token is absent                                                                                   |
| `Input.defaultValueFactory`                                         | The same, computed from the `ArgumentResult`                                                                              |
| `Input.arity`                                                       | How many values are accepted: `ExactlyOne`, `OneOrMore`, `Zero`, `ZeroOrMore`, `ZeroOrOne`, or `ArgumentArity (min, max)` |
| `Input.required`                                                    | Marks an option required                                                                                                  |
| `Input.recursive`                                                   | Applies the option to the command and, recursively, its subcommands                                                       |
| `Input.hidden`                                                      | Keeps it out of help output                                                                                               |
| `Input.allowMultipleArgumentsPerToken`                              | Lets one identifier token carry several values                                                                            |
| `Input.acceptOnlyFromAmong`                                         | Restricts to a set of legal strings, ordinally                                                                            |
| `Input.addCompletion` / `Input.addCompletions`                      | Adds tab-completion suggestions without restricting what is accepted                                                      |
| `Input.mapFromAmong<'T> [ "key", value ]`                           | An option over a known set, each key bound to a typed value                                                               |
| `Input.mapFromAmongWith<'T> comparer`                               | `mapFromAmong` under an explicit `StringComparer`                                                                         |
| `Input.mapFromMany` / `mapFromManyWith`                             | The repeatable forms, binding `'T list`                                                                                   |
| `Input.acceptLegalFileNamesOnly` / `Input.acceptLegalFilePathsOnly` | Restricts to legal file names / paths                                                                                     |
| `Input.validate`                                                    | A `'T -> Result<unit, string>` check; `Error` becomes a CLI validation message                                            |
| `Input.validateFileExists` / `Input.validateDirectoryExists`        | The two common cases, over `FileInfo` / `DirectoryInfo`                                                                   |
| `Input.addValidator`                                                | A raw `SymbolResult -> unit` validator                                                                                    |
| `Input.customParser`                                                | An `ArgumentResult -> 'T` parser                                                                                          |
| `Input.tryParse`                                                    | An `ArgumentResult -> Result<'T, string>` parser; `Error` becomes a parse diagnostic instead of an exception              |
| `Input.editOption` / `Input.editArgument`                           | Reaches the underlying `Option<'T>` / `Argument<'T>` for anything not covered above                                       |

## `InputSpec<'T>`

`InputSpec<'T>` is public at `Partas.Build`. A stage factory parameterised by an option needs no
`open Partas.Build.Internal`:

```fsharp
let build (projects: InputSpec<string list>) = input {
    let! projects = projects
    and! config = Options.config
    ...
}
```

| Function | What it does |
|---|---|
| `InputSpec.ofInput` | Lifts an `ActionInput<'T>` into a spec |
| `InputSpec.ret` | A spec that reads nothing and returns a constant |
| `InputSpec.map` | Reshapes the value a spec reads |
| `InputSpec.map2` | Combines two specs, unioning their inputs |
| `InputSpec.sequence` | A list of specs into one spec of a list |
| `InputSpec.traverse` | `sequence` over the results of a mapping |
| `InputSpec.union` | Concatenates input lists, keeping the first occurrence of each |

The `input { let! … and! … return … }` CE is the usual way to build one. It is applicative: bind every source
in a single `let!`/`and!` group. A sequential second `let!` is a compile error (`FS0708`) because the input set
has to be readable before anything is parsed. An `input { }` nested inside another's `return` produces an
`InputSpec<InputSpec<_>>`, which nothing accepts — pass the *source* in as an `InputSpec` instead.

## `Cmd`

A `Cmd` keeps the executable and its arguments apart all the way to `ProcessStartInfo.ArgumentList`, so the
platform does the escaping.

| Function | What it does |
|---|---|
| `cmd $"dotnet build {project}"` | Each hole becomes exactly one argument, whatever it contains. `run $"..."` binds to the `string` overload and flattens the holes, so interpolate through `cmd` |
| `Cmd.ofString` | Splits a whole command line, honouring `"` and `'` |
| `Cmd.create exe args` | The executable exactly as given, plus an argument string split as `ofString` does |
| `Cmd.ofList exe args` | Both exactly as given |
| `Cmd.arg` / `Cmd.args` | Appends arguments exactly as given |
| `Cmd.argIf cond values` | Appends only when `cond` holds — one line instead of two whole command lines under an `if` |
| `Cmd.argWhenSome value render` | Appends the arguments rendered from a `Some`, and nothing from a `None` |
| `Cmd.secretArg` | Appends one argument whose value is masked wherever the command is printed |
| `Cmd.secretOption flag value` | Appends a visible flag and a masked value: `-k ***` |
| `Cmd.secretOptionWhenSome flag value` | The same when the value exists, appending nothing otherwise |
| `Cmd.secret` / `Cmd.sensitive` | Marks a string unprintable before it goes into a `cmd` hole |
| `Cmd.ofFormattable secret` | The interpolation reader behind `cmd` and `runSensitive` |
| `Cmd.toLogString` | How the command prints: secrets masked, whitespace-carrying arguments quoted |

## `Args`

The arguments a script was given, as distinct from the ones its host was given.

| Function | What it answers |
|---|---|
| `Args.script ()` | The running script's own arguments. `rootCommandOfScript { }` is `rootCommand (Args.script ()) { }` |
| `Args.scriptName ()` | The running script's filename, when it was launched as one |
| `Args.afterScript argv` | Everything after the `.fsx` in `argv`, or after `argv[0]` when there is none. A leading `--` is dropped |
| `Args.take argv` | Everything after the first `--` |
| `Args.nameOf argv` | The filename of the first `.fsx` in `argv` |

`dotnet fsi build.fsx -- test --quick` does not reach the process with its `--` intact: the `dotnet` driver
consumes one before `fsi` sees the command line. `Args.script` locates the script's own filename instead of
splitting on a separator.

## `Baked`

Ready-made declarations for the options every build CLI ends up wanting. They ship in their own package,
`Partas.Build.Baked`, under `Partas.Build.Baked`.

Each declaration is a `BuildOption<'T>` carrying both forms: `.option` is the flag, `.argument` the positional
equivalent. `BuildOption.map`, `.mapOpt` and `.mapArg` apply an `Input.*` combinator to both forms or to one.

| Value | What it declares |
|---|---|
| `Baked.NuGet.apiKey` | `nuget-key` (aliases `--nuget`, `-k`) as `string option`, defaulting to the `NUGET_API_KEY` environment variable |
| `Baked.Dotnet.config` | `configuration` (alias `-c`) as `string option`, over `release`/`r`/`debug`/`d` case-insensitively |
| `Baked.SemVer.bump` | `bump` as `Bump option`, over `major\|minor\|patch\|alpha\|beta\|rc\|preview\|<SEMVER>`, defaulting to `Patch` |
| `Baked.Common.isCI` | `--ci`, defaulting to true when any of the usual CI environment variables is set |

| Function | What it does |
|---|---|
| `Baked.SemVer.Version.apply bump version` | Semantic version arithmetic over a `Bump` |
| `Baked.SemVer.Version.assembly version` | The assembly version that goes with a package version: its major, and nothing else |
| `Baked.SemVer.Version.IO.writeVersion` / `setVersion` | Rewrites `<Version>` and `<AssemblyVersion>` in a project file |
| `Baked.SemVer.Version.IO.bumpVersion projPath bump` | Applies a bump to a project file in place, answering the versions before and after |
| `Baked.SemVer.Stages.bumpArgument projects` | A `bump` stage taking the bump kind as a positional argument |
| `Baked.SemVer.Stages.bumpOption projects` | The same with the bump kind as `--bump` |

## Reference

- [Overview](build-overview.fsx) — the layers, and a first pipeline.
- [Composing reusable blocks](composition.fsx) — blocks, nesting, and composition across files.
- [Stage CE run overloads](computation-expression-operations.fsx).
- [API reference](https://shayanhabibi.github.io/Partas.Build/reference/) — full signatures and remarks.
