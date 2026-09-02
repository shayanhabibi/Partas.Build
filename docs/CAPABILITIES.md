---
title: Capabilities
category: Build
index: 1
---

# Capabilities

Every custom operation on the four builders, every `Input` combinator, and the `Cmd` argument helpers — one
line each. Use it to find the name; the [API reference](reference/index.html) has the full signature and
remarks for each, and [Composing reusable blocks](composition.html) has worked examples.

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

All three accept `int<second>`, `float` seconds, or a `TimeSpan`, except on `command`, which takes `int`
seconds or a `TimeSpan`.

## Stage operations

Available inside `stage`, and inside `whenStage`, which accepts everything `stage` does.

| Operation | What it does |
|---|---|
| `run` | Adds a step. Takes a literal command line, a `Cmd`, or a function of the `StageContext` returning `unit`, `int`, `Result<unit, string>`, a `Cmd`, an `Async<_>` or a `Task<_>` of any of those, optionally wrapped in `option` |
| `runSensitive` | Adds a step from an interpolated command line with every hole masked as `***` wherever the library prints it |
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
| `parallel'` | Runs the stage's steps concurrently. `true`/`0`/`-1` unbounded, `1`/`false` sequential, `n` throttled to exactly `n` in flight; also takes a `StageContext -> _` condition |
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

| Function | What it makes |
|---|---|
| `Input.option<'T> "--name"` | An option bound as `'T` |
| `Input.optionMaybe<'T> "--name"` | An option bound as `'T option`, `None` when absent |
| `Input.argument<'T> "name"` | A positional argument bound as `'T` |
| `Input.argumentMaybe<'T> "name"` | A positional argument bound as `'T option` |
| `Input.choices<'T> "--name" [ "key", value ]` | An option over a known set, each key bound to a typed value. Completions, validation, help text and the typed value from one declaration; an unrecognised token becomes a parse diagnostic listing the legal set |
| `Input.choicesCI` | `choices`, matched without regard to case |
| `Input.choicesWith comparer` | `choices` under an explicit `StringComparer` |
| `Input.choicesMany` / `choicesManyCI` / `choicesManyWith` | The repeatable forms, binding `'T list` |
| `Input.context` | Injects the `ActionContext` — the `ParseResult` and a cancellation token |
| `Input.inject value` | Injects a value that is not parsed from the command line |
| `Input.ofOption` / `Input.ofArgument` | Lifts a raw `System.CommandLine` `Option<'T>` / `Argument<'T>` |

Shaping combinators, all `ActionInput<'T> -> ActionInput<'T>` and all pipeable:

| Function | What it does |
|---|---|
| `Input.alias` / `Input.aliases` | Adds alternative names. Options only |
| `Input.description`, `Input.desc` | The help text |
| `Input.helpName` | The value placeholder in help — `<Debug\|Release>` |
| `Input.defaultValue`, `Input.def` | The value used when the token is absent |
| `Input.defaultValueFactory` | The same, computed from the `ArgumentResult` |
| `Input.arity` | How many values are accepted: `ExactlyOne`, `OneOrMore`, `Zero`, `ZeroOrMore`, `ZeroOrOne`, or `ArgumentArity (min, max)` |
| `Input.required` | Marks an option required |
| `Input.recursive` | Applies the option to the command and, recursively, its subcommands |
| `Input.hidden` | Keeps it out of help output |
| `Input.allowMultipleArgumentsPerToken` | Lets one identifier token carry several values |
| `Input.acceptOnlyFromAmong` | Restricts to a set of legal strings, ordinally |
| `Input.acceptLegalFileNamesOnly` / `Input.acceptLegalFilePathsOnly` | Restricts to legal file names / paths |
| `Input.validate` | A `'T -> Result<unit, string>` check; `Error` becomes a CLI validation message |
| `Input.validateFileExists` / `Input.validateDirectoryExists` | The two common cases, over `FileInfo` / `DirectoryInfo` |
| `Input.addValidator` | A raw `SymbolResult -> unit` validator |
| `Input.customParser` | An `ArgumentResult -> 'T` parser |
| `Input.tryParse` | An `ArgumentResult -> Result<'T, string>` parser; `Error` becomes a parse diagnostic instead of an exception |
| `Input.editOption` / `Input.editArgument` | Reaches the underlying `Option<'T>` / `Argument<'T>` for anything not covered above |

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

Ready-made declarations for the options every build CLI ends up wanting. `Baked.Input.*` are options,
`Baked.Argument.*` the positional equivalents.

| Value | What it declares |
|---|---|
| `Baked.Input.NuGet.apiKey` | `--nuget-key` (alias `--nuget`) as `string option` |
| `Baked.Input.NuGet.apiKeyOrEnv` | The same, defaulting to the `NUGET_API_KEY` environment variable |
| `Baked.Input.DotNet.config` | `--configuration` (alias `-c`) as `Configuration option`, restricted to `Debug`/`Release` |
| `Baked.Input.DotNet.configString` | The same as `string option` |
| `Baked.Input.Versioning.bump` | `--bump` as `Bump option`, over `major\|minor\|patch\|alpha\|beta\|rc\|preview\|<SEMVER>`, defaulting to `Patch` |
| `Baked.Input.Project.target targets` | `--project` (alias `-p`) as `string list`, restricted to `targets` |
| `Baked.Input.CI.isCI` | `--ci`, defaulting to true when any of the usual CI environment variables is set |

| Function | What it does |
|---|---|
| `Baked.Version.apply bump version` | Semantic version arithmetic over a `Bump` |
| `Baked.Version.assembly version` | The assembly version that goes with a package version: its major, and nothing else |
| `Baked.IO.writeVersion` / `Baked.IO.setVersion` | Rewrites `<Version>` and `<AssemblyVersion>` in a project file |
| `Baked.IO.bumpVersion projPath bump` | Applies a bump to a project file in place, answering the versions before and after |
| `Baked.Pipelines.bumpArgument allProjects projects` | A `bump` stage taking the bump kind as a positional argument |
| `Baked.Pipelines.bumpOption allProjects projects` | The same with the bump kind as `--bump` |

## Reference

- [Overview](build-overview.html) — the layers, and a first pipeline.
- [Composing reusable blocks](composition.html) — blocks, nesting, and composition across files.
- [Stage CE run overloads](computation-expression-operations.html).
- [API reference](reference/index.html) — full signatures and remarks.
