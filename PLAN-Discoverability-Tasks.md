# Discoverability & Reach Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Answer `FEEDBACK-Xantham.md` by making Partas.Build able to describe itself at runtime, closing the API gaps that make wrong code the easy code, and putting documentation where a consumer holding only the NuGet package will actually find it.

**Architecture:** Four pillars over three waves. Wave 1 changes the input/command-line surface (files disjoint from the execution engine) while documentation of already-existing features proceeds in parallel. Wave 2 adds runtime self-description — which needs a label on `Step`, hence the ordering — and execution robustness. Wave 3 documents the new surface and verifies against the repository's own CLI, which is written against the library.

**Tech Stack:** F# 9/10, .NET 10 / 8 / netstandard2.0, System.CommandLine 2.0.11, Spectre.Console 0.57.2, Expecto, FsToolkit.ErrorHandling.

**Spec:** `PLAN-Discoverability.md` (in this repository root). Read it before Task 1; it carries the triage evidence and the reasoning each task argues from.

## Global Constraints

- **Style:** `.editorconfig` sets Stroustrup, `max_line_length=150`, `fsharp_space_before_uppercase_invocation=true`. No fantomas is installed and there is no `format` command — match surrounding style by hand.
- **Prefer `voption`/`ValueOption` and `[<Struct>]` DUs** in the library. This is the deliberate departure from the ported Fun.Build code; do not reintroduce `option` in the model.
- **Public API goes in `[<AutoOpen>]` modules under `Partas.Build`.** The model and engine stay in `Partas.Build.Internal`.
- **Never mark a CE entry member `inline` when it applies a `Build*` alias** (`BuildStage`, `BuildStep`, `BuildStageIsActive`, …). Release builds fail with `FS1118`; Debug compiles fine. `Run` members are the usual offenders.
- **Compile order in `src/Partas.Build/Partas.Build.fsproj` matters:** `System.CommandLine/Aliases.fs` → `System.CommandLine/Inputs.fs` → `Types.fs` → `Process.fs` → `Builders/Stage.fs` → `Builders/Conditions.fs` → `Builders/Pipeline.fs` → `Builders/Inputs.fs` → `Builders/Command.fs` → `Baked.fs`.
- **Verify CE changes against the compiler, not by inspection.** Overload resolution in these builders fails invisibly — a catch-all overload swallowing a value, an overload that can never match, `Combine` right-associating into nested tuples. Build a throwaway project in the scratchpad referencing `src/Partas.Build`, exercise the syntax, and walk `Steps`/`Stages` to confirm the shape.
- **Both configurations must build:** `dotnet build src/Partas.Build` and `dotnet build src/Partas.Build -c Release`.
- **Tests are Expecto, run `--sequenced`.** One file per layer in `tests/Partas.Build.Tests`. Add tests to the existing file for the layer you touch; create a new file only where this plan says so.
- **Run a single test:** `dotnet run --project tests/Partas.Build.Tests -- --filter-test-case "<name>"`.
- **Full acceptance:** `dotnet run --project Build.fsproj -- test`.
- **Branch:** all work lands on `feedback/discoverability`.

---

## File Structure

| File | Responsibility | Wave |
|---|---|---|
| `src/Partas.Build/Types.fs` | Move `InputSpec<'T>` out of `Internal` (T1); add the `Step` label (T9) | 1, 2 |
| `src/Partas.Build/Process.fs` | `Cmd` argument combinators (T2) | 1 |
| `src/Partas.Build/System.CommandLine/Inputs.fs` | `Input.choices` family (T3) | 1 |
| `src/Partas.Build/Builders/Conditions.fs` | `whenSome` / `whenOk` (T4) | 1 |
| `src/Partas.Build/Builders/Command.fs` | Root command `name` and args default (T5), auto-registered `--explain` / `--version` (T10, T11) | 1, 2 |
| `src/Partas.Build/Builders/Stage.fs` | Step labels at construction (T9), `retry` (T13) | 2 |
| `src/Partas.Build/Explain.fs` **(new)** | Renders a resolved stage tree without running it (T10) | 2 |
| `src/Partas.Build/Summary.fs` **(new)** | End-of-run timing table (T12) | 2 |
| `README.md` | Leads with the option model; the "did you look for this?" table (T6) | 1 |
| `docs/CAPABILITIES.md` **(new)** | Every custom operation and `Input` combinator, one line each (T7) | 1, 3 |
| `docs/composition.fsx` | Cross-script composition — the real W1 answer (T8) | 1 |
| `docs/llms.txt`, `docs/llms-full.txt` **(new)** | Machine-readable entry point (T8) | 1 |
| `tests/Partas.Build.Tests/CmdTests.fs` | T2 coverage | 1 |
| `tests/Partas.Build.Tests/InputsTests.fs` | T1, T3 coverage | 1 |
| `tests/Partas.Build.Tests/ConditionsTests.fs` | T4 coverage | 1 |
| `tests/Partas.Build.Tests/CommandTests.fs` | T5, T11 coverage | 1, 2 |
| `tests/Partas.Build.Tests/ExplainTests.fs` **(new)** | T10, T12 coverage | 2 |
| `tests/Partas.Build.Tests/StageTests.fs` | T13 coverage | 2 |
| `tests/Partas.Build.Tests/ParallelismTests.fs` | T14 coverage | 2 |

New `.fs` files must be added to `Partas.Build.fsproj` in compile order, and new test files to `tests/Partas.Build.Tests/Partas.Build.Tests.fsproj` before `Main.fs`.

---

# Wave 1a — Inputs and command-line ergonomics

Tasks 1-5. Touches `Types.fs` (one type move), `Process.fs`, `Inputs.fs`, `Conditions.fs`, `Command.fs`. Disjoint from the execution engine, so it runs in parallel with Wave 1b.

### Task 1: `InputSpec<'T>` leaves `Internal`

Spec §B6. Deletes a load-bearing `open Partas.Build.Internal` from a consumer's CI script.

**Files:**
- Modify: `src/Partas.Build/Types.fs` (the `type InputSpec<'T>` declaration, currently at ~line 94 inside the `namespace Partas.Build.Internal` block that opens at ~line 85)
- Test: `tests/Partas.Build.Tests/InputsTests.fs`

**Interfaces:**
- Produces: `Partas.Build.InputSpec<'T> = { Inputs: ActionInput list; Read: System.CommandLine.ParseResult -> 'T }` — same shape, new namespace. The `InputSpec` *module* is already at `Partas.Build` (~line 500) and does not move.

- [ ] **Step 1: Write the failing test**

In `tests/Partas.Build.Tests/InputsTests.fs`, add to the existing `testList`. This test compiles only if the type is reachable without the `Internal` namespace — which is the whole point, so the assertion is deliberately trivial.

```fsharp
test "InputSpec is nameable without the Internal namespace" {
    // The type annotation is the assertion: this file must not need `open Partas.Build.Internal`
    // to write a stage factory parameterised by an option. See FEEDBACK-Xantham.md §2.1.
    let factory (projects: Partas.Build.InputSpec<string list>) =
        input {
            let! ps = projects
            return stage "build" { run (fun (_: StageContext) -> ignore ps) }
        }

    let spec = factory (InputSpec.ret [ "a"; "b" ])
    Expect.equal spec.Inputs [] "a pure spec declares no inputs"
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter-test-case "InputSpec is nameable without the Internal namespace"`
Expected: compile error — `The type 'InputSpec' is not defined in 'Partas.Build'`.

- [ ] **Step 3: Move the type**

Cut the `type InputSpec<'T> = { Inputs: ActionInput list; Read: CommandLine.ParseResult -> 'T }` declaration (with its doc comment) out of the `namespace Partas.Build.Internal` block and place it in the `namespace Partas.Build` block that precedes it, after the `ActionInput` references it needs are in scope.

Nothing inside `Partas.Build.Internal` needs a new `open`: F# makes an enclosing namespace implicitly accessible from a nested one, which is already why the declaration at its old site could name `ActionInput` (defined in `Partas.Build` by `System.CommandLine/Inputs.fs`) unqualified. Confirm this by building rather than by assuming it.

- [ ] **Step 4: Build both configurations**

Run: `dotnet build src/Partas.Build && dotnet build src/Partas.Build -c Release`
Expected: both clean. If `Internal` code cannot see the type, add `open Partas.Build` at the top of the `namespace Partas.Build.Internal` block rather than moving the type back.

- [ ] **Step 5: Run the full suite**

Run: `dotnet run --project Build.fsproj -- test`
Expected: green. `Build/Program.fs` and `Baked.fs` both name `InputSpec`; a namespace move that broke them would surface here.

- [ ] **Step 6: Commit**

```bash
git add src/Partas.Build/Types.fs tests/Partas.Build.Tests/InputsTests.fs
git commit -m "feat: publish InputSpec<'T> under Partas.Build

A stage factory parameterised by an option is the idiomatic shape the docs
present, and writing its signature required naming a type in a namespace
called Internal. FEEDBACK-Xantham.md 2.1.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 2: `Cmd` argument combinators

Spec §B3. "Add a flag conditionally" is the most common edit anyone makes to a command line, and today the only expressible form is duplicating the whole line — so people abandon `cmd` for unquoted strings, which is the exact failure the quoting existed to prevent.

**Files:**
- Modify: `src/Partas.Build/Process.fs` (inside `module Cmd`, after `ofList` at ~line 121)
- Test: `tests/Partas.Build.Tests/CmdTests.fs`

**Interfaces:**
- Consumes: `Cmd = { Executable: string; Arguments: string list; Secrets: Set<int> }`, `Cmd.toLogString`, the `mask` literal `"***"`.
- Produces:
  ```fsharp
  Cmd.arg                 : string -> Cmd -> Cmd
  Cmd.args                : string list -> Cmd -> Cmd
  Cmd.argIf               : bool -> string list -> Cmd -> Cmd
  Cmd.argWhenSome         : 'a option -> ('a -> string list) -> Cmd -> Cmd
  Cmd.secretArg           : string -> Cmd -> Cmd
  Cmd.secretOption        : string -> string -> Cmd -> Cmd
  Cmd.secretOptionWhenSome: string -> string option -> Cmd -> Cmd
  ```

- [ ] **Step 1: Write the failing tests**

Add to the existing `testList` in `tests/Partas.Build.Tests/CmdTests.fs`:

```fsharp
test "argIf appends only when the condition holds" {
    let baseline = cmd $"dotnet test {"Foo.slnx"} --no-build"

    let withFilter = baseline |> Cmd.argIf true [ "--filter"; "Category=Fast" ]
    let without = baseline |> Cmd.argIf false [ "--filter"; "Category=Fast" ]

    Expect.equal withFilter.Arguments [ "test"; "Foo.slnx"; "--no-build"; "--filter"; "Category=Fast" ]
        "the flag and its value arrive as two arguments, not one"
    Expect.equal without.Arguments baseline.Arguments "a false condition leaves the command alone"
}

test "argWhenSome renders the value it was given" {
    let baseline = cmd $"dotnet test {"Foo.slnx"}"
    let applied = baseline |> Cmd.argWhenSome (Some "Category=Fast") (fun f -> [ "--filter"; f ])
    let skipped = baseline |> Cmd.argWhenSome None (fun f -> [ "--filter"; f ])

    Expect.equal applied.Arguments [ "test"; "Foo.slnx"; "--filter"; "Category=Fast" ] "Some appends"
    Expect.equal skipped.Arguments baseline.Arguments "None does not"
}

test "a secret argument is masked in the log string but intact in the arguments" {
    let pushed =
        Cmd.ofList "dotnet" [ "nuget"; "push"; "pkg.nupkg" ]
        |> Cmd.secretOption "-k" "super-secret-key"

    Expect.equal pushed.Arguments [ "nuget"; "push"; "pkg.nupkg"; "-k"; "super-secret-key" ]
        "the process still receives the real key"
    Expect.stringContains (Cmd.toLogString pushed) "-k ***" "the log masks the key but keeps the flag"
    Expect.isFalse ((Cmd.toLogString pushed).Contains "super-secret-key") "the key never reaches the log"
}

test "secretOptionWhenSome omits the flag entirely when there is no value" {
    let baseline = Cmd.ofList "dotnet" [ "nuget"; "push"; "pkg.nupkg" ]
    let applied = baseline |> Cmd.secretOptionWhenSome "-k" (Some "key")
    let skipped = baseline |> Cmd.secretOptionWhenSome "-k" None

    Expect.equal applied.Arguments [ "nuget"; "push"; "pkg.nupkg"; "-k"; "key" ] "Some appends flag and value"
    Expect.equal skipped.Arguments baseline.Arguments "None appends neither"
    Expect.isEmpty skipped.Secrets "and marks nothing secret"
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "cmd"`
Expected: compile error — `The value, namespace, type or module 'argIf' is not defined`.

- [ ] **Step 3: Implement the combinators**

In `src/Partas.Build/Process.fs`, inside `module Cmd`, directly after `ofList`:

```fsharp
    /// <summary>Appends one argument, taken exactly as given.</summary>
    let arg (value: string) (cmd: Cmd) = { cmd with Arguments = cmd.Arguments @ [ value ] }

    /// <summary>Appends arguments, each taken exactly as given.</summary>
    let args (values: string list) (cmd: Cmd) = { cmd with Arguments = cmd.Arguments @ values }

    /// <summary>Appends <paramref name="values"/> only when <paramref name="condition"/> holds.</summary>
    /// <remarks>
    /// The alternative is branching on the whole command line, which is why three optional flags become
    /// eight interpolated strings and people give up on <c>cmd</c> — losing the quoting it exists for.
    /// </remarks>
    let argIf (condition: bool) (values: string list) (cmd: Cmd) = if condition then args values cmd else cmd

    /// <summary>Appends what <paramref name="render"/> makes of the value, when there is one.</summary>
    let argWhenSome (value: 'a option) (render: 'a -> string list) (cmd: Cmd) =
        match value with
        | Some value -> args (render value) cmd
        | None -> cmd

    /// <summary>Appends one argument whose value must never be printed.</summary>
    let secretArg (value: string) (cmd: Cmd) =
        { cmd with
            Arguments = cmd.Arguments @ [ value ]
            Secrets = cmd.Secrets |> Set.add cmd.Arguments.Length }

    /// <summary>Appends a flag and its value, masking the value everywhere the command is printed.</summary>
    /// <remarks>The flag stays visible: <c>-k ***</c> says more in a log than <c>***</c> does.</remarks>
    let secretOption (flag: string) (value: string) (cmd: Cmd) = cmd |> arg flag |> secretArg value

    /// <summary>Appends a masked flag and value when there is a value, and nothing at all when there is not.</summary>
    /// <remarks>
    /// The shape a publish step wants: no <c>.Value</c> under a <c>when'</c> that happens to guard it.
    /// </remarks>
    let secretOptionWhenSome (flag: string) (value: string option) (cmd: Cmd) =
        match value with
        | Some value -> secretOption flag value cmd
        | None -> cmd
```

Note `secretArg` computes the index from `cmd.Arguments.Length` **before** the append — that is the index the new argument lands on.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "cmd"`
Expected: PASS, all four.

- [ ] **Step 5: Build Release**

Run: `dotnet build src/Partas.Build -c Release`
Expected: clean. `Process.fs` has a `#if NETSTANDARD2_0` branch nearby; these functions are framework-agnostic, but the multi-target build is the check.

- [ ] **Step 6: Commit**

```bash
git add src/Partas.Build/Process.fs tests/Partas.Build.Tests/CmdTests.fs
git commit -m "feat: Cmd argument combinators for conditional flags

Adding a flag conditionally was expressible only by duplicating the whole
command line, so three optional flags meant eight branches and cmd got
abandoned for unquoted strings. FEEDBACK-Xantham.md 2.4, W4.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 3: `Input.choices` — an option over a known set of typed values

Spec §B1. The report calls this "the single most common option shape in any build script, and the one the library serves worst": twenty lines that map names to paths and paths back to records, restate their default twice, and throw an unhandled `KeyNotFoundException` on an unrecognised token.

**Files:**
- Modify: `src/Partas.Build/System.CommandLine/Inputs.fs` (inside `module Input`, after `acceptOnlyFromAmong`)
- Test: `tests/Partas.Build.Tests/InputsTests.fs`

**Interfaces:**
- Consumes: `Input.option<'T>`, `Input.acceptOnlyFromAmong`, `Input.tryParse`, `Input.arity`, `Input.allowMultipleArgumentsPerToken`, `Aliases.Arity`.
- Produces:
  ```fsharp
  Input.choicesWith      : StringComparer -> string -> (string * 'T) list -> ActionInput<'T>
  Input.choices          : string -> (string * 'T) list -> ActionInput<'T>
  Input.choicesCI        : string -> (string * 'T) list -> ActionInput<'T>
  Input.choicesManyWith  : StringComparer -> string -> (string * 'T) list -> ActionInput<'T list>
  Input.choicesMany      : string -> (string * 'T) list -> ActionInput<'T list>
  Input.choicesManyCI    : string -> (string * 'T) list -> ActionInput<'T list>
  ```

**Design note — a deliberate deviation from the spec sketch.** `PLAN-Discoverability.md` §B1 sketches a pipeable `Input.caseInsensitive`. That cannot work: by the time it would run, `choices` has already closed over its lookup table and handed a `CustomParser` to System.CommandLine, and a downstream combinator has no way to reach back into that closure. Comparison is therefore a parameter of construction, with two named specialisations. Record this in the doc comment so the next reader does not re-litigate it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Partas.Build.Tests/InputsTests.fs`. The second test asserts the *failure mode*, which is the actual point of the task.

```fsharp
type private Layer = { Name: string; Path: string }

let private layers =
    [ { Name = "ast"; Path = "src/Ast" }
      { Name = "proto"; Path = "src/Proto" } ]

let private choiceTable = layers |> List.map (fun layer -> layer.Name, layer)
```

```fsharp
test "choices binds a token to its typed value" {
    let input = Input.choices<Layer> "--layer" choiceTable
    let command = Command "generate"
    command.Options.Add (input.GetOption())

    let parsed = command.Parse [| "--layer"; "proto" |]

    Expect.isEmpty parsed.Errors "a legal token parses cleanly"
    Expect.equal (input.GetValue parsed).Path "src/Proto" "the stage receives the record, not the token"
}

test "choices rejects an unknown token as a parse error rather than an exception" {
    let input = Input.choices<Layer> "--layer" choiceTable
    let command = Command "generate"
    command.Options.Add (input.GetOption())

    let parsed = command.Parse [| "--layer"; "nope" |]

    Expect.isNonEmpty parsed.Errors "an illegal token is a CLI diagnostic"
    let message = parsed.Errors |> Seq.map (fun e -> e.Message) |> String.concat " "
    Expect.stringContains message "nope" "the message names the offending token"
    Expect.stringContains message "ast" "and lists what was legal"
}

test "choicesCI accepts a differently-cased token" {
    let input = Input.choicesCI<Layer> "--layer" choiceTable
    let command = Command "generate"
    command.Options.Add (input.GetOption())

    let parsed = command.Parse [| "--layer"; "PROTO" |]

    Expect.isEmpty parsed.Errors "case-insensitive lookup accepts it"
    Expect.equal (input.GetValue parsed).Name "proto" "and yields the canonical entry"
}

test "choicesMany binds every token it was given" {
    let input = Input.choicesMany<Layer> "--layer" choiceTable
    let command = Command "generate"
    command.Options.Add (input.GetOption())

    let parsed = command.Parse [| "--layer"; "ast"; "--layer"; "proto" |]

    Expect.isEmpty parsed.Errors "both tokens are legal"
    Expect.equal (input.GetValue parsed |> List.map (fun l -> l.Name)) [ "ast"; "proto" ] "in the order given"
}
```

`Expect.stringContains message "ast"` asserts the legal set reaches the user. If System.CommandLine's own `AcceptOnlyFromAmong` message wins the race it already lists the values; if the `tryParse` error wins, the implementation lists them. Either satisfies the test, which is the intent — the requirement is that the user is told, not which layer tells them.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "inputs"`
Expected: compile error — `'choices' is not defined`.

- [ ] **Step 3: Implement**

In `src/Partas.Build/System.CommandLine/Inputs.fs`, inside `module Input`, after `acceptOnlyFromAmong`. Ensure `open System` is present at the top of the file for `StringComparer` and `String.Join`.

```fsharp
    /// <summary>An option whose legal values are a known set, each bound to a typed value.</summary>
    /// <remarks>
    /// One declaration produces completions, validation, help text and typed values. An unrecognised token
    /// becomes a parse diagnostic listing the legal set, not an exception out of a lookup in the caller.
    /// <para>
    /// Comparison is a parameter of construction rather than a pipeable combinator: the parser closes over
    /// this table when the input is built, and nothing downstream can reach back into that closure.
    /// </para>
    /// </remarks>
    let choicesWith<'T> (comparer: StringComparer) (name: string) (choices: (string * 'T) list): ActionInput<'T> =
        let keys = choices |> List.map fst
        let legal = String.Join (", ", keys)
        let lookup token = choices |> List.tryFind (fun (key, _) -> comparer.Equals (key, token)) |> Option.map snd

        option<'T> name
        |> acceptOnlyFromAmong keys
        |> tryParse (fun argResult ->
            match argResult.Tokens |> Seq.tryLast with
            | None -> Error $"'%s{name}' needs one of: %s{legal}"
            | Some token ->
                match lookup token.Value with
                | Some value -> Ok value
                | None -> Error $"'%s{token.Value}' is not one of: %s{legal}")

    /// <summary>An option whose legal values are a known set, matched case-sensitively.</summary>
    let choices<'T> (name: string) (choices: (string * 'T) list): ActionInput<'T> =
        choicesWith<'T> StringComparer.Ordinal name choices

    /// <summary>An option whose legal values are a known set, matched without regard to case.</summary>
    let choicesCI<'T> (name: string) (choices: (string * 'T) list): ActionInput<'T> =
        choicesWith<'T> StringComparer.OrdinalIgnoreCase name choices

    /// <summary>A repeatable option over a known set, collecting every token as a typed value.</summary>
    let choicesManyWith<'T> (comparer: StringComparer) (name: string) (choices: (string * 'T) list): ActionInput<'T list> =
        let keys = choices |> List.map fst
        let legal = String.Join (", ", keys)
        let lookup token = choices |> List.tryFind (fun (key, _) -> comparer.Equals (key, token)) |> Option.map snd

        option<'T list> name
        |> acceptOnlyFromAmong keys
        |> arity Arity.ZeroOrMore
        |> allowMultipleArgumentsPerToken
        |> tryParse (fun argResult ->
            let resolved = [ for token in argResult.Tokens -> token.Value, lookup token.Value ]

            match resolved |> List.tryPick (fun (raw, value) -> if value.IsNone then Some raw else None) with
            | Some unknown -> Error $"'%s{unknown}' is not one of: %s{legal}"
            | None -> Ok [ for _, value in resolved -> value.Value ])

    /// <summary>A repeatable option over a known set, matched case-sensitively.</summary>
    let choicesMany<'T> (name: string) (choices: (string * 'T) list): ActionInput<'T list> =
        choicesManyWith<'T> StringComparer.Ordinal name choices

    /// <summary>A repeatable option over a known set, matched without regard to case.</summary>
    let choicesManyCI<'T> (name: string) (choices: (string * 'T) list): ActionInput<'T list> =
        choicesManyWith<'T> StringComparer.OrdinalIgnoreCase name choices
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "inputs"`
Expected: PASS, all four.

If `choicesCI` fails because `AcceptOnlyFromAmong` rejects `PROTO` before the custom parser runs, drop `acceptOnlyFromAmong` from the two `*With` functions when the comparer is case-insensitive and register the legal set as completions instead:

```fsharp
        |> editOption (fun o -> for key in keys do o.CompletionSources.Add key)
```

The `tryParse` error still supplies validation in that path. Record whichever branch you took in the doc comment.

- [ ] **Step 5: Build Release and commit**

Run: `dotnet build src/Partas.Build -c Release`
Expected: clean.

```bash
git add src/Partas.Build/System.CommandLine/Inputs.fs tests/Partas.Build.Tests/InputsTests.fs
git commit -m "feat: Input.choices for an option over a known set of typed values

Twenty lines become one, and an unrecognised token becomes a CLI validation
message instead of an unhandled KeyNotFoundException.
FEEDBACK-Xantham.md 2.3, W8.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 4: `whenSome` — a condition that binds the value it tested

Spec §B2. Removes `.Value` under `when' x.IsSome` from the one stage where a mistake is public and permanent. Today that shape is correct only because `when'`'s evaluation order says so; the compiler is not checking it, and a refactor that moves `when'` below the `run` compiles fine and throws in CI at the last stage of a release.

**Files:**
- Modify: `src/Partas.Build/Builders/Conditions.fs` (a new `[<AutoOpen>]` module at the end of the file)
- Test: `tests/Partas.Build.Tests/ConditionsTests.fs`

**Interfaces:**
- Consumes: `StageContext`, the `stage` builder.
- Produces:
  ```fsharp
  whenSome : 'a option -> ('a -> StageContext) -> StageContext list
  whenOk   : Result<'a, 'b> -> ('a -> StageContext) -> StageContext list
  ```

**Design note — a deliberate deviation from the spec sketch.** `PLAN-Discoverability.md` §B2 gives the return type as `StageContext`. Returning a single stage forces the `None` case to invent a phantom inactive stage, which then appears in the run log and in `--explain` under a name nobody wrote. Returning a **list** — `[]` for `None` — composes with the `Yield(stages: StageContext seq)` overload that both `CommandBuilder` (`Builders/Command.fs:113`) and the stage builder already have, and produces no phantom. Confirm that overload resolves for a nested `stage` before relying on it (see Step 3).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Partas.Build.Tests/ConditionsTests.fs`:

```fsharp
test "whenSome yields a stage that closes over the value" {
    let observed = ResizeArray<string>()

    let built =
        pipeline "publish" {
            whenSome (Some "the-key") (fun key ->
                stage "push" { run (fun (_: StageContext) -> observed.Add key) })
        }

    runPipeline built
    Expect.equal (List.ofSeq observed) [ "the-key" ] "the stage ran with the bound value"
}

test "whenSome yields nothing at all when there is no value" {
    let observed = ResizeArray<string>()

    let built =
        pipeline "publish" {
            whenSome None (fun key -> stage "push" { run (fun (_: StageContext) -> observed.Add key) })
            stage "always" { run (fun (_: StageContext) -> observed.Add "always") }
        }

    runPipeline built
    Expect.equal (List.ofSeq observed) [ "always" ] "the guarded stage did not run"
    Expect.equal (built.Stages |> List.map (fun s -> s.Name)) [ "always" ]
        "and left no phantom stage behind for the log to name"
}

test "whenOk yields the stage only for Ok" {
    let observed = ResizeArray<string>()

    let built =
        pipeline "publish" {
            whenOk (Ok "yes") (fun v -> stage "ok" { run (fun (_: StageContext) -> observed.Add v) })
            whenOk (Error "no") (fun v -> stage "err" { run (fun (_: StageContext) -> observed.Add v) })
        }

    runPipeline built
    Expect.equal (List.ofSeq observed) [ "yes" ] "only the Ok branch contributed a stage"
}
```

`runPipeline` is the existing helper in `tests/Partas.Build.Tests/Helpers.fs`. Read that file first and use whatever it actually exports; if it has a different name, use that name and do not add a second helper.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "conditions"`
Expected: compile error — `'whenSome' is not defined`.

- [ ] **Step 3: Implement**

At the end of `src/Partas.Build/Builders/Conditions.fs`:

```fsharp
/// <summary>Stage-producing conditions that bind the value they tested.</summary>
[<AutoOpen>]
module ValueConditions =
    /// <summary>Yields the stage <paramref name="build"/> makes of the value, and nothing when there is none.</summary>
    /// <remarks>
    /// The alternative is <c>when' value.IsSome</c> above a <c>run</c> that reaches for <c>value.Value</c>,
    /// which is correct only because of evaluation order — nothing the compiler checks, and a refactor that
    /// moves the condition below the step compiles cleanly and throws at run time.
    /// <para>
    /// It returns a list so that the absent case contributes no stage at all, rather than an inactive one
    /// that would need a name it was never given.
    /// </para>
    /// </remarks>
    let whenSome (value: 'a option) (build: 'a -> StageContext): StageContext list =
        match value with
        | Some value -> [ build value ]
        | None -> []

    /// <summary>Yields the stage <paramref name="build"/> makes of an <c>Ok</c> value, and nothing for <c>Error</c>.</summary>
    let whenOk (value: Result<'a, 'b>) (build: 'a -> StageContext): StageContext list =
        match value with
        | Ok value -> [ build value ]
        | Error _ -> []
```

- [ ] **Step 4: Verify the CE actually accepts the list**

Before running the tests, prove the overload resolves in all three positions — inside `pipeline`, inside `command`, and nested inside a `stage`. Build a spike in the scratchpad referencing `src/Partas.Build`, yield a `whenSome` in each position, and print the resulting `Stages`/`Steps`. If a nested `stage` has no `Yield(StageContext seq)` overload, add one to the stage builder in `Builders/Stage.fs` alongside the existing `Yield(stage: StageContext)`, matching the `Yield`/`Delay`/`Combine`/`For` overload set as the other kinds do — adding one without the other three is the documented way this breaks.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "conditions"`
Expected: PASS, all three.

- [ ] **Step 6: Build Release and commit**

```bash
dotnet build src/Partas.Build -c Release
git add src/Partas.Build/Builders/Conditions.fs src/Partas.Build/Builders/Stage.fs tests/Partas.Build.Tests/ConditionsTests.fs
git commit -m "feat: whenSome and whenOk bind the value they test

Publishing is the one stage where a mistake is public and permanent, and it
was the stage with the least type safety. FEEDBACK-Xantham.md 2.6, W5.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 5: Root command identity — a name, and no args slice

Spec §B5 and §B7. Combined into one task because both edit `Builders/Command.fs`; splitting them would put two workers in one file for a few lines each.

Today every script built this way ships help text calling the program `fsi`:

```
Usage:
  fsi [command] [options]
```

**Files:**
- Modify: `src/Partas.Build/Builders/Command.fs` (`CommandSpec` application in `applyTo` at ~line 59; `RootCommandBuilder` at ~line 383; the `rootCommand` binding at ~line 422)
- Modify: `src/Partas.Build/Types.fs` (`CommandSpec` record at ~line 182 — add `Name`; `CommandSpec.create` sets it `ValueNone`)
- Test: `tests/Partas.Build.Tests/CommandTests.fs`

**Interfaces:**
- Consumes: `CommandSpec`, `applyTo`, `RootCommandBuilder`.
- Produces:
  ```fsharp
  // custom operation on rootCommand only
  name : string -> BuildCommand
  // module-level
  Args.take       : string array -> string array   // everything after the first "--"; the pure core
  Args.nameOf     : string array -> string voption // the first .fsx among them, filename only; the pure core
  Args.script     : unit -> string array           // Args.take over Environment.GetCommandLineArgs ()
  Args.scriptName : unit -> string voption         // Args.nameOf over Environment.GetCommandLineArgs ()
  rootCommandOfScript : RootCommandBuilder   // rootCommand over Args.script ()
  ```

- [ ] **Step 1: Write the failing tests**

Add to `tests/Partas.Build.Tests/CommandTests.fs`:

```fsharp
test "the root command takes the name it was given" {
    let built =
        rootCommand [| "--help" |] {
            name "build.fsx"
            description "The repository build"
        }

    ignore built
    // rootCommand's Run returns an exit code, so assert through a parse instead:
    let spec = CommandSpec.create "" |> (fun c -> { c with Name = ValueSome "build.fsx" })
    Expect.equal spec.Name (ValueSome "build.fsx") "the spec carries the name"
}

test "Args.script takes everything after the first separator" {
    let taken = Args.take [| "fsi.dll"; "build.fsx"; "--"; "test"; "--quick" |]
    Expect.equal taken [| "test"; "--quick" |] "the script's own arguments and nothing else"
}

test "Args.script is empty when there is no separator" {
    let taken = Args.take [| "fsi.dll"; "build.fsx" |]
    Expect.isEmpty taken "no separator means no arguments were passed to the script"
}

test "Args.scriptName finds the script file among the host's arguments" {
    let named = Args.nameOf [| "fsi.dll"; "tools/generate-wire.fsx"; "--"; "generate" |]
    Expect.equal named (ValueSome "generate-wire.fsx") "the filename, without its directory"
}
```

`Args.take` and `Args.nameOf` are the pure, testable cores; `Args.script ()` and `Args.scriptName ()` are the one-line wrappers over `Environment.GetCommandLineArgs()`. Testing the pure functions is the point — the wrappers cannot be tested from inside a test host whose own command line is not a script's.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "command"`
Expected: compile error — `'Args' is not defined` and `The record label 'Name' is not defined`.

- [ ] **Step 3: Add `Name` to `CommandSpec`**

In `src/Partas.Build/Types.fs`, add `Name: string voption` to the `CommandSpec` record and set it `ValueNone` in `CommandSpec.create`. Leave the existing positional name (the `create "build"` argument) alone — that is the subcommand's token and is unrelated.

- [ ] **Step 4: Add the `Args` module**

At the end of `src/Partas.Build/Builders/Command.fs`, before the `rootCommand` binding:

```fsharp
/// <summary>The arguments a script was given, as distinct from the ones its host was given.</summary>
[<AutoOpen>]
module Args =
    /// <summary>Everything after the first <c>--</c>.</summary>
    /// <remarks>
    /// <c>dotnet fsi build.fsx -- test --quick</c> reaches the process as the whole command line; the
    /// script's own arguments are what follows the separator. This is what <c>fsi.CommandLineArgs[1..]</c>
    /// was standing in for, and it does not require the script to reach for <c>fsi</c> at all.
    /// </remarks>
    let take (argv: string array) =
        match argv |> Array.tryFindIndex (fun arg -> arg = "--") with
        | Some index -> argv[index + 1 ..]
        | None -> [||]

    /// <summary>The filename of the first <c>.fsx</c> among <paramref name="argv"/>.</summary>
    let nameOf (argv: string array) =
        argv
        |> Array.tryFind (fun arg -> arg.EndsWith (".fsx", StringComparison.OrdinalIgnoreCase))
        |> function
            | Some path -> ValueSome (IO.Path.GetFileName path)
            | None -> ValueNone

    /// <summary>The running script's own arguments.</summary>
    let script () = take (Environment.GetCommandLineArgs())

    /// <summary>The running script's filename, when it was launched as one.</summary>
    let scriptName () = nameOf (Environment.GetCommandLineArgs())
```

Add `open System` to the file if it is not already present.

- [ ] **Step 5: Add the `name` operation and apply it**

On `RootCommandBuilder`, alongside `parserConfiguration`:

```fsharp
    /// <summary>Sets the name the root command calls itself in help and usage text.</summary>
    /// <remarks>
    /// Available only on <c>rootCommand</c>. Without it the name is the host executable's, so a script run
    /// as <c>dotnet fsi build.fsx</c> ships help telling a new contributor to run <c>fsi build</c>.
    /// Defaults to the script's filename when the process was launched with one.
    /// </remarks>
    [<CustomOperation>] member inline _.
        name
        ([<InlineIfLambda>] build: BuildCommand, name: string): BuildCommand
        = build >> fun cmd -> { cmd with Name = ValueSome name }
```

In `RootCommandBuilder.Run(build: BuildCommand)`, after `applyTo (RootCommand()) spec`, set the name from the spec falling back to the script filename:

```fsharp
        match spec.Name |> ValueOption.orElse (Args.scriptName ()) with
        | ValueSome name -> root.Name <- name
        | ValueNone -> ()
```

**Verify `Command.Name` is settable in System.CommandLine 2.0.11 before relying on this.** If it is get-only, the fallback is to construct the root through `RootCommand(description)` and override the usage line with a custom `HelpAction`; if that also proves unavailable, implement the default-from-filename part, drop `root.Name <- …`, and record the limitation in the doc comment and in `PLAN-Discoverability.md` §B5 — which already flags `usage` as an investigation rather than a commitment.

- [ ] **Step 6: Add `rootCommandOfScript`**

Next to the existing `let inline rootCommand args = RootCommandBuilder args`:

```fsharp
/// <summary>The root command over the running script's own arguments.</summary>
/// <remarks><c>rootCommandOfScript { … }</c> is <c>rootCommand (Args.script ()) { … }</c>.</remarks>
let rootCommandOfScript = RootCommandBuilder (Args.script ())
```

Named to sit beside `rootCommand` in autocomplete, which is where a consumer looks.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "command"`
Expected: PASS. The whole existing `command` list must stay green — `CommandTests.fs` drives real invocations.

- [ ] **Step 8: Build Release and commit**

```bash
dotnet build src/Partas.Build -c Release
git add src/Partas.Build/Types.fs src/Partas.Build/Builders/Command.fs tests/Partas.Build.Tests/CommandTests.fs
git commit -m "feat: root command name, and a default for the script args slice

Every script built on the library shipped help text calling the program fsi,
and every script repeated fsi.CommandLineArgs[1..] as ceremony.
FEEDBACK-Xantham.md 2.8, W9, W13.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

# Wave 1b — Documenting what already exists

Tasks 6-8. **Runs in parallel with Wave 1a** and touches no `.fs` file, so there is no merge contention. This is the wave that answers most of the report: the features below all exist today and were never found.

Every worker on this wave must first read `FEEDBACK-Xantham.md` §2 and the triage table in `PLAN-Discoverability.md` §2, and must **verify each claim against the source before writing it down**. Documenting a feature that does not behave as described is how this problem started.

### Task 6: README leads with the option model

Spec §C. The report's §1.1 says the option model "is the best idea in the library and it should be the thing the README leads with". Today the README leads with a `pipeline` that no consumer script uses.

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Restructure the opening**

Lead with the option model — declared at the point of use, registered automatically, deduplicated by construction — using a worked example that shows a stage binding a flag and a command registering nothing. The existing example at `README.md:33-80` is close; what changes is that `pipeline` comes out of it and the *property* is stated in prose before the code:

> Across 23 option declarations and three root commands, a flag that a stage reads but the CLI does not accept is not expressible. Neither is a flag registered on a command whose stages ignore it. Both are routine failures in hand-wired `System.CommandLine` setups.

Then show `command "test" { Stages.restore; Stages.test }` and its generated `--help`.

- [ ] **Step 2: Demote `pipeline`**

Move `pipeline` to a "Composition" section further down, and say plainly what it is for and that `command { stage; stage }` is the common form. §2.11 and §4.4 of the report are one reader spending a year unsure whether they were missing something; one sentence prevents the next.

- [ ] **Step 3: Add the "Did you look for this?" table**

Immediately after the opening example, because it is the highest-value block in the file. Verify every right-hand cell against the source before writing it:

```markdown
## Did you look for this?

| You want | Use |
|---|---|
| An environment variable for one stage and its children | `envVars` on the stage — it is applied to the child process, so it never touches your own and needs no restore |
| A secret in a command line | `runSensitive $"..."`, or `Cmd.secretOption` — every hole is masked `***` wherever the library prints |
| A stage's output only when it fails | `captureOutput` |
| Another script's commands as subcommands | `#load` it and yield the `Command` value — see [Composition](composition.html) |
| An option with a fixed set of legal values | `Input.choices` |
| To know what a command will do before running it | `<command> --explain` |
| A flag added to a command line only sometimes | `Cmd.argIf` |
| A working directory for a stage's children | `workingDir` on the parent — it is inherited |
```

The `--explain` and `Input.choices` / `Cmd.argIf` rows land in Wave 1a/2. Write the whole table now; the rows for unlanded features are the reason Task 15 re-checks this file.

- [ ] **Step 4: Add a docs pointer near the top**

The package ships this README and points at nothing else, which is why 2,500 lines of `docs/` went unread. Add, above the fold: a link to <https://shayanhabibi.github.io/Partas.Build>, a link to `docs/CAPABILITIES.md`, and a line saying agents should start at `llms.txt`.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs: lead the README with the option model

The one property consumers call better than anything else in .NET was
below the fold, and the headline example used a pipeline no consumer
script writes. FEEDBACK-Xantham.md 1.1, 2.11, 4.4.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 7: The capability map

Spec §C. One table of every operation in the library. This single artifact would have prevented most of the report.

**Files:**
- Create: `docs/CAPABILITIES.md`

- [ ] **Step 1: Enumerate the real surface**

```bash
grep -oE 'CustomOperation\("[a-zA-Z'"'"']+"\)|CustomOperation>\] member inline (this|_)\.\s*[a-zA-Z'"'"']+' src/Partas.Build/Builders/*.fs
grep -nE "^\s{0,8}let (inline )?[a-zA-Z]" src/Partas.Build/System.CommandLine/Inputs.fs
grep -nE "^\s{0,8}let (inline )?[a-zA-Z]" src/Partas.Build/Process.fs
```

Note that `[<CustomOperation>]` with no argument takes its name from the member, so both forms must be collected.

- [ ] **Step 2: Write the file**

Four tables — **Stage operations**, **Pipeline operations**, **Command operations**, **`Input` combinators** — plus a short **`Cmd`** table. One line per entry: the name, what it does, and where it is inherited from or resolved (for the settings that walk `ParentContext` upward, say so, because §2.9 is a reader who could not tell).

Mark each entry with the builder it is available on. `timeout` existing on stage, pipeline *and* command with different meanings (the command's is a *default*, not an override) is exactly the kind of thing this table exists to disambiguate.

- [ ] **Step 3: Sanity-check for omissions**

Count the `[<CustomOperation>]` occurrences per builder file and compare against your table's row counts. A mismatch is an omission; find it.

- [ ] **Step 4: Commit**

```bash
git add docs/CAPABILITIES.md
git commit -m "docs: capability map of every operation and Input combinator

The consumer's reachable surface was a README and autocomplete on names
they did not know to type. FEEDBACK-Xantham.md, method note.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 8: Cross-script composition, and a machine-readable entry point

Spec §C. This is the real answer to W1 — the report's number one wish, for a feature that already exists.

**Files:**
- Modify: `docs/composition.fsx`
- Create: `docs/llms.txt`, `docs/llms-full.txt`

- [ ] **Step 1: Prove the pattern before documenting it**

In the scratchpad, write two `.fsx` files. The first binds its command tree as a value and gates its own invocation:

```fsharp
// tools/generate-wire.fsx
let generateCommands =
    command "generate" {
        description "Regenerate a wire layer"
        command "ast" { Stages.generateAst }
        command "proto" { Stages.generateProto }
    }

// Only take over the process when this script is the one being run.
if Args.scriptName () = ValueSome "generate-wire.fsx" then
    exit (rootCommandOfScript { generateCommands })
```

The second `#load`s it and yields the value:

```fsharp
// build.fsx
#load "tools/generate-wire.fsx"

exit (rootCommandOfScript {
    command "generate" {
        description "Generate every wire layer"
        Stages.deps
        ``Generate-wire``.generateCommands
    }
})
```

Run both. Confirm `build.fsx -- generate --help` shows the per-layer options, and that `#load`ing does not execute the loaded script's root command. Note the exact module name `#load` gives the file — F# derives it from the filename and it is not always what you would guess; write down what it actually was.

- [ ] **Step 2: Document it in `docs/composition.fsx`**

Add a "Composition across files" section with the verified example. State the two rules plainly: the command tree is a value, and the `rootCommand` invocation is gated. Say what it replaces — a `--only <string>` flag whose legal values live in a description string, four layer names spelled twice in two files with nothing checking the agreement, and four `fsi` startups with four NuGet resolutions per run.

- [ ] **Step 3: Write `docs/llms.txt`**

The published entry point for an agent handed only a package name. Follow the convention: an `# Partas.Build` heading, a one-paragraph summary, then link sections with one-line descriptions.

```markdown
# Partas.Build

> An F# build-pipeline DSL whose stages declare the CLI options they read. Commands derive their
> System.CommandLine option set from the pipelines they activate, so options, validation and help text
> are generated from the pipeline definition rather than registered by hand. Runs from a `.fsx` script
> with no build project.

## Start here

- [Capabilities](https://shayanhabibi.github.io/Partas.Build/CAPABILITIES.html): every operation and Input combinator, one line each
- [Index](https://shayanhabibi.github.io/Partas.Build/index.html): the layers, and a first pipeline
- [Composition](https://shayanhabibi.github.io/Partas.Build/composition.html): nesting stages, and reusing commands across files

## Ask the script rather than reading it

Any command accepts `--explain`, which prints the resolved stage tree — every stage, its condition verdict
and each step's command line — and runs nothing.

## Optional

- [Build overview](https://shayanhabibi.github.io/Partas.Build/build-overview.html)
- [CE operations](https://shayanhabibi.github.io/Partas.Build/computation-expression-operations.html)
```

`llms-full.txt` is the same content with the linked pages inlined as plain text. Generate it, do not hand-write it.

- [ ] **Step 4: Publish both at the site root**

`fsdocs` copies `docs/` content; confirm `llms.txt` survives to `output/` by running `dotnet run --project Build.fsproj -- docs` and checking `output/llms.txt` exists. If fsdocs skips unknown extensions, add it under `docs/content/` instead and re-check.

- [ ] **Step 5: Commit**

```bash
git add docs/composition.fsx docs/llms.txt docs/llms-full.txt
git commit -m "docs: cross-script composition, and an llms.txt entry point

W1 was the report's top wish for a capability that already shipped: a
command tree is a value and Yield takes it. What was missing was the
pattern for gating the loaded script's own rootCommand.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

# Wave 2 — Self-description and execution robustness

Tasks 9-14. **Starts only after Wave 1a has landed on the branch**, because Task 9 changes `Step`, which Task 5's file also touches.

Tasks 9-12 (self-description) and tasks 13-14 (robustness) may run in parallel with each other: the first group owns `Types.fs`'s `Step`, `Explain.fs`, `Summary.fs` and `Command.fs`; the second owns `Builders/Stage.fs`'s operation list and the runner's parallel branch. They overlap only in `Types.fs`, in different regions.

### Task 9: Label a step with the command line it will run

Spec §A1. `--explain` cannot render `$ dotnet test …` unless the step carries the text. This task adds the label and changes nothing else; it is separated so a reviewer can gate the model change on its own.

**Files:**
- Modify: `src/Partas.Build/Types.fs` (`Step` at ~line 97; every `Step.StepFn` construction and match site)
- Modify: `src/Partas.Build/Builders/Stage.fs` (~lines 180, 383, 429, 439 and any other `Step.StepFn` site)
- Test: `tests/Partas.Build.Tests/StageTests.fs`

**Interfaces:**
- Produces:
  ```fsharp
  Step.StepFn of label: string voption * fn: (StageContext -> StepIndex -> Async<Result<unit, string>>)
  StageContext.addStepFn        : StepFnSignature -> StageContext -> StageContext          // unchanged, labels ValueNone
  StageContext.addLabelledStepFn: string -> StepFnSignature -> StageContext -> StageContext
  ```

- [ ] **Step 1: Write the failing test**

```fsharp
test "a step built from a literal command line carries it as a label" {
    let built = stage "test" { run "dotnet test --no-build" }

    let labels = [ for step in built.Steps do match step with Step.StepFn(label, _) -> label | _ -> () ]
    Expect.equal labels [ ValueSome "dotnet test --no-build" ] "the literal is the label"
}

test "a step built from cmd carries the printable command line, with secrets masked" {
    let key = "super-secret-key"
    let built = stage "push" { runSensitive $"dotnet nuget push pkg.nupkg -k {key}" }

    let labels = [ for step in built.Steps do match step with Step.StepFn(label, _) -> label | _ -> () ]
    match labels with
    | [ ValueSome label ] ->
        Expect.stringContains label "nuget push" "the label shows the command"
        Expect.isFalse (label.Contains key) "and never the secret"
    | other -> failtestf "expected one labelled step, got %A" other
}

test "a step built from a function has no label to show" {
    let built = stage "compute" { run (fun (_: StageContext) -> ()) }

    let labels = [ for step in built.Steps do match step with Step.StepFn(label, _) -> label | _ -> () ]
    Expect.equal labels [ ValueNone ] "an opaque closure claims nothing"
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "stage"`
Expected: compile error — `Step.StepFn` takes one argument, not two.

- [ ] **Step 3: Widen the case**

In `Types.fs`:

```fsharp
type [<Struct; RequireQualifiedAccess>] Step =
    /// <summary>A step, and the command line it prints as when there is one.</summary>
    /// <remarks>
    /// The label is what <c>--explain</c> renders. It is populated for the <c>run</c> overloads whose
    /// command is known at construction — a literal string, or a <c>Cmd</c> — and left empty for the ones
    /// taking a function, whose command is a closure nothing can read. An empty label renders as the step's
    /// index rather than as a guess.
    /// <para>Labels come from <c>Cmd.toLogString</c>, which masks secrets, so a label is safe to print.</para>
    /// </remarks>
    | StepFn of label: string voption * fn: (StageContext -> StepIndex -> Async<Result<unit, string>>)
    | StepOfStage of stage: StageContext
```

Then fix every construction and match site the compiler names. `StageContext.addStepFn` keeps its signature and passes `ValueNone`; add `addLabelledStepFn` beside it.

- [ ] **Step 4: Label the eager `run` overloads**

In `Builders/Stage.fs`, the `run` overloads taking a `string` or a `Cmd` know their command at construction: label them `Cmd.toLogString` of the parsed `Cmd`. The overloads taking `StageContext -> …` (the `buildCmd` sites at ~429 and ~439) build their `Cmd` from the context asynchronously, so they pass `ValueNone`.

Do not try to make the async sites eager. Running a `buildCmd` closure to obtain a label would execute user code during `--explain`, which is the one thing `--explain` must not do.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet run --project Build.fsproj -- test`
Expected: green. Every layer's tests pattern-match `Step`, so this is where a missed site surfaces.

- [ ] **Step 6: Build Release and commit**

```bash
dotnet build src/Partas.Build -c Release
git add src/Partas.Build/Types.fs src/Partas.Build/Builders/Stage.fs tests/Partas.Build.Tests/StageTests.fs
git commit -m "feat: carry the command line on a step as a label

Groundwork for --explain: the tree can only show what a step will run if the
step kept it. Secrets are masked because the label comes from toLogString.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 10: `--explain`

Spec §A1. The report calls this "the cheapest large win available; the data already exists" — and, for an agent working in a consumer's repository, "the difference between reading the script and asking the script".

**Files:**
- Create: `src/Partas.Build/Explain.fs` (compile after `Builders/Command.fs`, before `Baked.fs`)
- Modify: `src/Partas.Build/Partas.Build.fsproj` (compile order)
- Modify: `src/Partas.Build/Builders/Command.fs` (register the option in `applyTo`; branch in the command action)
- Create: `tests/Partas.Build.Tests/ExplainTests.fs` (add to the test fsproj before `Main.fs`)

**Interfaces:**
- Consumes: `CommandSpec.Pipelines: InputSpec<PipelineContext> list`, `InputSpec.Read`, `StageContext.IsActive`, `Step.StepFn(label, _)`, `Step.StepOfStage`.
- Produces:
  ```fsharp
  Explain.option        : ActionInput<bool>                              // "--explain"
  Explain.render        : PipelineContext list -> string                 // the tree, as text
  Explain.undescribed   : System.CommandLine.Command -> string list      // command paths lacking a description
  ```

**Design note on skip reasons.** A bare `when' (not quick)` is an opaque `bool` — there is no expression text to recover, so it renders `skipped` with no reason. The structured conditions carry a reason. Implement this by giving the condition builders an optional reason recorded on the stage; a stage with several conditions reports the first that failed. Do **not** invent a reason for `when'`.

- [ ] **Step 1: Write the failing tests**

```fsharp
test "explain renders the tree without running anything" {
    let ran = ResizeArray<string>()

    let built =
        pipeline "test" {
            stage "restore" { when' false; run (fun (_: StageContext) -> ran.Add "restore") }
            stage "build" { run "dotnet build Foo.slnx -c Release" }
        }

    let text = Explain.render [ built ]

    Expect.isEmpty ran "explain executes no step"
    Expect.stringContains text "restore" "every stage appears"
    Expect.stringContains text "skipped" "an inactive stage says so"
    Expect.stringContains text "dotnet build Foo.slnx -c Release" "a labelled step shows its command line"
}

test "explain masks a secret in a step's command line" {
    let key = "super-secret-key"
    let built = pipeline "publish" { stage "push" { runSensitive $"dotnet nuget push pkg -k {key}" } }

    let text = Explain.render [ built ]

    Expect.isFalse (text.Contains key) "explain is safe to run on a publish command"
    Expect.stringContains text "***" "the hole is masked, not omitted"
}

test "explain names an unlabelled step by its index rather than guessing" {
    let built = pipeline "compute" { stage "work" { run (fun (_: StageContext) -> ()) } }
    let text = Explain.render [ built ]
    Expect.stringContains text "step 1" "an opaque closure renders as its position"
}

test "explain reports commands that have no description" {
    let built = command "orphan" { pipeline "p" { stage "s" { run (fun (_: StageContext) -> ()) } } }
    Expect.contains (Explain.undescribed built) "orphan" "an undescribed command is reported"
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "explain"`
Expected: compile error — `'Explain' is not defined`.

- [ ] **Step 3: Write `Explain.fs`**

`render` walks each `PipelineContext`'s `Stages`, and for each stage: evaluate `IsActive`, then recurse into `Steps` — `Step.StepOfStage` is a nested stage, `Step.StepFn(label, _)` is a leaf rendering `$ <label>` or `step <n>`. Use box-drawing characters matching the report's mockup (`├─`, `└─`, `│`). Emit through `StageContext.writeLine` where a context is in hand, and return the text so tests can assert on it without capturing console output.

Evaluating `IsActive` performs read-only IO for some conditions — `whenBranch` shells out to git. That is acceptable and must be stated in the module's doc comment.

- [ ] **Step 4: Register `--explain` on every command**

In `Builders/Command.fs`'s `applyTo`, add `Explain.option` to every command's options alongside the pipeline-derived inputs. In the command's action, read it first: when set, materialise the pipelines by `Read`ing each `InputSpec` against the `ParseResult`, print `Explain.render`, append the `Explain.undescribed` report if non-empty, and return exit code 0 without running.

The option is registered by the library on every command, deliberately. Opt-in would be one more undiscoverable feature, which is the failure this whole plan answers.

- [ ] **Step 5: Verify against the repository's own CLI**

Run: `dotnet run --project Build.fsproj -- test --explain`
Expected: the resolved tree for the `test` command, no test suite executed, exit 0. Then `dotnet run --project Build.fsproj -- publish --explain` and confirm no NuGet key appears in the output.

- [ ] **Step 6: Run the full suite, build Release, commit**

```bash
dotnet run --project Build.fsproj -- test
dotnet build src/Partas.Build -c Release
git add src/Partas.Build/Explain.fs src/Partas.Build/Partas.Build.fsproj src/Partas.Build/Builders/Command.fs tests/Partas.Build.Tests/
git commit -m "feat: --explain prints the resolved stage tree without running it

The library knew the whole tree, every condition verdict and every command
line before it ran anything, and nobody else did. FEEDBACK-Xantham.md W2.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 11: `--version`

Spec §A2. Makes a bug report name a version.

**Files:**
- Modify: `src/Partas.Build/Builders/Command.fs` (root command only)
- Test: `tests/Partas.Build.Tests/CommandTests.fs`

**Interfaces:**
- Produces: `Explain.libraryVersion : string` — read from the assembly's `AssemblyInformationalVersionAttribute`, falling back to `AssemblyVersion`.

- [ ] **Step 1: Write the failing test**

```fsharp
test "the library reports its own version" {
    let version = Explain.libraryVersion
    Expect.isNotEmpty version "a version is available for a bug report to quote"
    Expect.isTrue (version |> Seq.exists Char.IsDigit) "and it looks like a version"
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter-test-case "the library reports its own version"`
Expected: compile error.

- [ ] **Step 3: Implement**

In `Explain.fs`:

```fsharp
    /// <summary>The Partas.Build version this script is pinned to.</summary>
    let libraryVersion =
        let assembly = System.Reflection.Assembly.GetExecutingAssembly()
        assembly.GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
        |> Array.tryHead
        |> function
            | Some attr -> (attr :?> System.Reflection.AssemblyInformationalVersionAttribute).InformationalVersion
            | None -> assembly.GetName().Version |> string
```

Note `AssemblyVersion` is pinned at `0.0.0.0` in this repository (deliberately — see `CLAUDE.md`, *Versioning*), so the informational version is the one that carries real information and the fallback is genuinely a fallback.

Register `--version` on the root command in `applyTo`, printing `Partas.Build <version>` alongside the script's own name from Task 5.

- [ ] **Step 4: Run the test, then the suite, then commit**

```bash
dotnet run --project tests/Partas.Build.Tests -- --filter "command"
dotnet run --project Build.fsproj -- test
git add src/Partas.Build/Explain.fs src/Partas.Build/Builders/Command.fs tests/Partas.Build.Tests/CommandTests.fs
git commit -m "feat: --version reports the pinned Partas.Build version

Makes a bug report precise about which version it is about.
FEEDBACK-Xantham.md W13.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 12: The run summary

Spec §A3. "Every optimisation anyone makes to a build script starts with this table." The timings are already measured in `Types.fs`; only the summary is missing.

**Files:**
- Create: `src/Partas.Build/Summary.fs` (compile after `Explain.fs`)
- Modify: `src/Partas.Build/Types.fs` (`PipelineContext.run` — collect per-stage elapsed times; `StageContext.run` — report its own)
- Modify: `src/Partas.Build/Partas.Build.fsproj`
- Test: `tests/Partas.Build.Tests/ExplainTests.fs`

**Interfaces:**
- Produces:
  ```fsharp
  type StageTiming = { Name: string; Depth: int; Elapsed: TimeSpan; Outcome: StageOutcome }
  type [<Struct; RequireQualifiedAccess>] StageOutcome = Succeeded | Skipped | Failed of error: string
  Summary.render : StageTiming list -> string
  ```

- [ ] **Step 1: Write the failing tests**

```fsharp
test "the summary lists each stage with its wall time" {
    let timings =
        [ { Name = "build"; Depth = 1; Elapsed = TimeSpan.FromSeconds 9.1; Outcome = StageOutcome.Succeeded }
          { Name = "run gate"; Depth = 1; Elapsed = TimeSpan.FromSeconds 57.6; Outcome = StageOutcome.Failed "exit 1" } ]

    let text = Summary.render timings

    Expect.stringContains text "build" "every stage appears"
    Expect.stringContains text "9.1" "with its wall time"
    Expect.stringContains text "run gate" "including the one that failed"
    Expect.stringContains text "exit 1" "and what failing meant"
}

test "a skipped stage is shown as skipped rather than as zero seconds" {
    let timings = [ { Name = "restore"; Depth = 1; Elapsed = TimeSpan.Zero; Outcome = StageOutcome.Skipped } ]
    let text = Summary.render timings
    Expect.stringContains text "skipped" "a skipped stage is not a fast one"
}
```

- [ ] **Step 2: Run them to verify they fail; Step 3: implement `Summary.fs`**

Render a Spectre.Console table (console output is Spectre throughout) and return the text form for tests. Indent by `Depth` so nested stages read as a tree.

- [ ] **Step 4: Collect the timings during a run**

`Types.fs` already starts a `Stopwatch` per stage (~lines 732, 789) and per pipeline (~line 985). Accumulate a `StageTiming` per stage into a collector on the pipeline run, and print `Summary.render` once the pipeline finishes — on failure as well as success, since the failing stage is half the point.

Do this without changing `StageContext.run`'s signature if possible; a `ConcurrentBag` on the pipeline context that stages append to is sufficient and is safe under `parallel'`. Sort by start order before rendering, because a bag is unordered.

- [ ] **Step 5: Verify on the real build, run the suite, commit**

```bash
dotnet run --project Build.fsproj -- test
```
Expected: green, and a timing table at the end naming each stage. Confirm the numbers are plausible against the wall time of the run.

```bash
dotnet build src/Partas.Build -c Release
git add src/Partas.Build/Summary.fs src/Partas.Build/Types.fs src/Partas.Build/Partas.Build.fsproj tests/Partas.Build.Tests/ExplainTests.fs
git commit -m "feat: per-stage timing summary at the end of a run

The consumer measured their own build with a stopwatch because the library
printed no timings. FEEDBACK-Xantham.md 2.10, W6.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 13: `retry`

Spec §B4. Network-touching stages hang rather than fail, and a hung CI job costs a runner-hour and tells you nothing.

**Files:**
- Modify: `src/Partas.Build/Types.fs` (`StageContext` — add `Retry: int`; `StageContext.create` sets `0`)
- Modify: `src/Partas.Build/Builders/Stage.fs` (the `retry` operation, beside `timeout`)
- Modify: `src/Partas.Build/Types.fs` (`StageContext.run` — the retry loop)
- Test: `tests/Partas.Build.Tests/StageTests.fs`

**Interfaces:**
- Produces: custom operation `retry : int -> BuildStage`, and the `InputSpec<BuildStage>` overload beside it, as every other stage operation has.

- [ ] **Step 1: Write the failing tests**

```fsharp
test "retry runs a failing step again up to the given count" {
    let attempts = ref 0

    let built =
        stage "flaky" {
            retry 2
            run (fun (_: StageContext) ->
                incr attempts
                if attempts.Value < 3 then Error "not yet" else Ok())
        }

    let result = runStage built
    Expect.isOk result "the third attempt succeeded"
    Expect.equal attempts.Value 3 "one attempt plus two retries"
}

test "retry gives up after the given count" {
    let attempts = ref 0

    let built =
        stage "doomed" {
            retry 1
            run (fun (_: StageContext) -> incr attempts; Error "always")
        }

    let result = runStage built
    Expect.isError result "it still fails"
    Expect.equal attempts.Value 2 "one attempt plus one retry, and no more"
}

test "a stage without retry attempts its steps once" {
    let attempts = ref 0
    let built = stage "once" { run (fun (_: StageContext) -> incr attempts; Error "no") }

    runStage built |> ignore
    Expect.equal attempts.Value 1 "the default is unchanged"
}
```

`runStage` is the existing helper in `Helpers.fs`; read that file and use its real name and signature.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "stage"`
Expected: compile error — `'retry' is not defined`.

- [ ] **Step 3: Implement**

Add `Retry: int` to `StageContext`, defaulting to `0` in `create`. Add the operation in `Builders/Stage.fs` next to `timeout`, with both the `BuildStage` and `InputSpec<BuildStage>` overloads — matching how `timeout` is declared at ~line 662, since a stage operation that exists on only one of the two silently fails to compose inside `input { … }`.

In `StageContext.run`, wrap the step-execution body in a loop that re-runs on `Error` while attempts remain. Retry the **stage's steps**, not the stage's sub-stages, and reset any `OutputCapture` between attempts — `Types.fs:736` already clears a capture at stage start, so the retry loop must go through the same path or call it explicitly. A stale capture would lift the first attempt's output into the final error.

Interaction with `timeout`: the stage timeout is the budget for the whole stage including its retries, not per attempt. State this in the doc comment; a per-attempt budget would make `timeout` mean something different depending on whether `retry` was present.

- [ ] **Step 4: Run the tests, the suite, build Release, commit**

```bash
dotnet run --project tests/Partas.Build.Tests -- --filter "stage"
dotnet run --project Build.fsproj -- test
dotnet build src/Partas.Build -c Release
git add src/Partas.Build/Types.fs src/Partas.Build/Builders/Stage.fs tests/Partas.Build.Tests/StageTests.fs
git commit -m "feat: retry on a stage

Network-touching stages hang rather than fail. timeout bounded them; retry
is what makes the bound recoverable. FEEDBACK-Xantham.md W10.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 14: Buffered output under `parallel'`

Spec §B8. `parallel' 4` interleaves four npm installs into unreadable soup, which makes the parallelism unusable for anything you need to read.

**Files:**
- Modify: `src/Partas.Build/Types.fs` (the parallel branch of `StageContext.run`, ~lines 874-880)
- Test: `tests/Partas.Build.Tests/ParallelismTests.fs`

**Interfaces:**
- Produces: no new public API. Behaviour change only: when a stage is parallel, each branch's output is held and flushed as one block on completion.

- [ ] **Step 1: Write the failing test**

```fsharp
test "parallel branches flush their output as blocks rather than interleaved" {
    let lines = ResizeArray<string>()
    let write _ line = lock lines (fun () -> lines.Add line)

    let built =
        pipeline "install" {
            stage "installs" {
                parallel' 2
                redirectOutput write
                stage "a" { run (fun ctx -> for i in 1..5 do StageContext.writeLine ctx StdStream.Out $"a{i}") }
                stage "b" { run (fun ctx -> for i in 1..5 do StageContext.writeLine ctx StdStream.Out $"b{i}") }
            }
        }

    runPipeline built

    let sequence = List.ofSeq lines |> List.map (fun l -> l.Substring(0, 1))
    let blocks = sequence |> List.fold (fun acc c -> match acc with | h :: _ when h = c -> acc | _ -> c :: acc) []
    Expect.equal (List.length blocks) 2 "each branch's five lines arrive as one contiguous run"
}
```

The `blocks` fold counts transitions between branches: two branches interleaved freely would produce many more than two runs. Read `ParallelismTests.fs` first — if it already has a helper for collecting routed output, use it rather than adding `write` here.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project tests/Partas.Build.Tests -- --filter "parallel"`
Expected: FAIL — many alternating runs, not two.

- [ ] **Step 3: Implement**

In the parallel branch of `StageContext.run`, give each sub-stage a buffering sink for the duration of its run and flush it to the parent's sink on completion. The `StageOutput.Captured` machinery already exists and does exactly this holding; reuse it rather than writing a second buffer. A sub-stage that set its *own* `Output` explicitly keeps it — buffering is the default for a parallel branch, not an override of what the author asked for.

Flush under a lock so two branches finishing together cannot interleave at the flush.

- [ ] **Step 4: Verify by eye as well as by test**

A test asserting contiguity can pass while the output is still unpleasant. Run a real parallel stage — `dotnet run --project Build.fsproj -- test` exercises one — and read the log.

- [ ] **Step 5: Run the suite, build Release, commit**

```bash
dotnet run --project Build.fsproj -- test
dotnet build src/Partas.Build -c Release
git add src/Partas.Build/Types.fs tests/Partas.Build.Tests/ParallelismTests.fs
git commit -m "feat: buffer each parallel branch's output and flush it as a block

parallel' was usable for work you did not need to read, and no other.
FEEDBACK-Xantham.md W11.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

# Wave 3 — Documenting the new surface, and verifying the whole

### Task 15: Documentation second pass

**Files:**
- Modify: `docs/CAPABILITIES.md`, `README.md`, `docs/llms.txt`, `docs/llms-full.txt`, `docs/index.fsx`, `docs/composition.fsx`

- [ ] **Step 1: Add every new operation to the capability map**

`Input.choices` / `choicesCI` / `choicesMany` / `choicesManyCI` / `choicesWith` / `choicesManyWith`; `Cmd.arg` / `args` / `argIf` / `argWhenSome` / `secretArg` / `secretOption` / `secretOptionWhenSome`; `whenSome` / `whenOk`; `retry`; `name` on `rootCommand`; `Args.script` / `Args.scriptName`; `rootCommandOfScript`.

- [ ] **Step 2: Re-check the README's "did you look for this?" table**

Task 6 wrote rows for `--explain`, `Input.choices` and `Cmd.argIf` before they existed. Verify each row against the shipped behaviour now and correct any that drifted.

- [ ] **Step 3: Document `--explain` and the run summary as first-class features**

In `docs/index.fsx`, a section on asking the script rather than reading it: what `--explain` shows, what it does not (a bare `when'` has no reason to give; a step built from a function has no command line to show), and that it performs read-only IO when a condition consults git.

- [ ] **Step 4: Answer the remaining §2 items in prose**

Each of these needs a documented home, verified against the source:
- §2.2 — the rule for which combinator lives on `Input` versus `InputSpec`, replacing rote conversion.
- §2.9 — `workingDir` is inherited; four defensive calls can go.
- §2.5 — `envVars` on a stage, and why it needs no restore.
- W13's last bullet — what a `run` returning `Error` does to the exit code, to the remaining steps, to the parent stage, and to the pipeline. Establish this by reading `StageContext.run` and by writing a test, not by assuming.

- [ ] **Step 5: Audit the XML docs**

IntelliSense is the surface the consumer actually used, which makes it the highest-traffic documentation in
the project. Every feature this report proves was undiscoverable gets an `<example>` on its declaration:
`envVars`, `runSensitive`, `captureOutput`, `workingDir`, `acceptOnlyFromAmong`, `helpName`, `tryParse`,
`Cmd.ofList`, and the `Yield(Command)` path that makes cross-script composition work.

Confirm the generated file actually carries them:

```bash
dotnet build src/Partas.Build -c Release
grep -c "<example>" src/Partas.Build/bin/Release/net10.0/Partas.Build.xml
```

The shared fragments under `src/Partas.Build/xmldoc/*.xml` are spliced by `<include>`, which only an F# 11
compiler expands and only on the doc-writer path — so a missing `<example>` in the generated XML is a real
failure, not a grep artifact.

- [ ] **Step 6: Add a test for each behavioural claim the docs now make**

Spec §D: a documented claim that no test holds cannot rot silently. Every "X is inherited" / "Y needs no
restore" / "Z returns Error" sentence written in Steps 3-4 gets an Expecto test in the suite for its layer.
Write them; do not assume an existing test already covers a sentence you just wrote.

- [ ] **Step 7: Regenerate `llms-full.txt` and commit**

```bash
dotnet run --project Build.fsproj -- docs
git add README.md docs/
git commit -m "docs: document the surface added for the Xantham feedback

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

### Task 16: Verification against the acceptance criteria

**Files:** none — this task changes nothing and gates the branch.

- [ ] **Step 1: Both configurations build**

```bash
dotnet build src/Partas.Build
dotnet build src/Partas.Build -c Release
```
Expected: both clean. Release is not optional: `FS1118` on an `inline` CE entry member applying a `Build*` alias appears only there.

- [ ] **Step 2: The full suite is green**

```bash
dotnet run --project Build.fsproj -- test
```
Expected: green across all three Expecto suites. The `Build/` CLI is written against the library, so this is simultaneously the suite and the integration test.

- [ ] **Step 3: `--explain` works end to end**

```bash
dotnet run --project Build.fsproj -- test --explain
dotnet run --project Build.fsproj -- publish --explain
```
Expected: a resolved tree, nothing executed, exit 0, and no secret in the `publish` output.

- [ ] **Step 4: Walk `FEEDBACK-Xantham.md` §2 and Part 3 item by item**

Write the result into `PLAN-Discoverability.md` as a closing "Disposition" table: for each of §2.1-§2.11 and W1-W13, one of *shipped in task N*, *documented at `<path>`*, or *deferred, because …*. Acceptance criterion 4 says no entry may be answered by "it was always there" alone — an existing feature must name the document that now explains it.

- [ ] **Step 5: Confirm the capability map is complete**

```bash
grep -c "CustomOperation" src/Partas.Build/Builders/Stage.fs
grep -c "CustomOperation" src/Partas.Build/Builders/Pipeline.fs
grep -c "CustomOperation" src/Partas.Build/Builders/Command.fs
```
Compare against the row counts in `docs/CAPABILITIES.md`. A mismatch is an omission.

- [ ] **Step 6: Confirm no consumer signature needs `Internal`**

```bash
grep -rn "Partas.Build.Internal" Build/ docs/*.fsx
```
Expected: no hit in a public signature or a doc example. The engine's own files may still use it.

- [ ] **Step 7: Commit the disposition table**

```bash
git add PLAN-Discoverability.md
git commit -m "docs: disposition of every item in the Xantham feedback

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Notes for whoever picks this up

- **The report is evidence, not instruction.** Roughly half of it asks for things that exist. Where a task says "already exists, document it", verify that against the source before writing the documentation — and if you find the report was right and the triage was wrong, say so and change the task rather than documenting a feature that is not there.
- **`FEEDBACK-Xantham.md` §4 is the consumer's own self-criticism** and is worth reading for what it says about the DSL: "the DSL made the wrong thing easy and we took it". That is the standard for judging a new operation.
- **The two design deviations recorded in Tasks 3 and 4** (comparison as a construction parameter; `whenSome` returning a list) are corrections to the spec made because the spec's sketch cannot compile. Both are recorded in `PLAN-Discoverability.md`'s terms and should be reflected back into it if they survive review.
