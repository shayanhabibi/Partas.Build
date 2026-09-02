# Partas.Build — a consumer report from Xantham

A wish list and a candid review, written from one repository's real usage.

## Who is writing this

[Xantham](https://github.com/shayanhabibi/Xantham) is an F#/Fable toolchain that generates
Fable bindings from TypeScript declarations. Its build surface is four `.fsx` files, all of
them driven by `Partas.Build 0.3.0` and `Partas.TypeProvider.BuildHelper 0.2.5`:

| Script | Root commands | Options | Stages |
|---|---|---|---|
| `build.fsx` | `format` `bump` `build` `generate` `docs` `publish` `test` `findings` `pack` | 12 | 12 |
| `tools/generate-wire.fsx` | `sync tsc-ast`, `generate {ast,proto,session,browser}` | 9 | 6 |
| `tools/xantham-fixtures.fsx` | `init` | 2 | 3 |
| `tools/workspace.fsx` | *(`#load`-only helper)* | — | — |

CI is one line — `dotnet fsi build.fsx -- publish --nuget-key $KEY` — and the repository
previously carried a `Build.fsproj` plus a FAKE target-operator arrangement that now lives in
`.archive/`. That deletion is the headline compliment in this document: a library that
replaces a whole build *project* with a script, and loses nothing, is doing its job.

**Method.** Everything below was written without reading Partas.Build's source. That is
deliberate — the point is to report what the library feels like from the outside, using its
README, its IntelliSense surface, and four scripts' worth of scar tissue. Where a wish may
already be satisfied by something we failed to find, we say so; in that case treat the entry
as a discoverability report rather than a feature request, because from where we sit the two
are the same bug.

---

## Part 1 — What is genuinely good

### 1.1 Options declared at the point of use, registered automatically

This is the best idea in the library and it should be the thing the README leads with. A stage
declares what it reads:

```fsharp
let test =
    input {
        let! skipTests = Options.skipTests
        and! config = Options.config
        and! update = Options.updateGoldens
        and! filter = Options.testFilter
        and! skipRunGate = Options.skipRunGate
        return stage "test" { ... }
    }
```

and a command composes stages:

```fsharp
command "test" {
    Stages.restore
    Stages.clean
    Stages.format
    Stages.deps
    Stages.fixtures
    Stages.test
}
```

Nothing registers `--filter` with the `test` command. Yet:

```
$ dotnet fsi build.fsx -- test --help
Options:
  -q, --quick                          Skip setup steps, such as installing dependencies
  --skip-tests                         Skip running tests
  -c, --configuration <Debug|Release>  Build configuration
  -u, --update                         Regenerate the golden corpus before asserting against it...
  --filter <filter>                    Run only tests whose name matches...
  --no-run-gate                        Skip the Fable run gate, much the slowest step...
```

`--quick` appears once, though five of the six stages bind it. Across 23 option declarations
and three root commands we have never had a flag that a stage reads but the CLI does not
accept, and never had a flag registered on a command whose stages ignore it. Both of those are
routine failures in hand-wired `System.CommandLine` setups, and deduplication by construction
removes the whole class. It is worth protecting in any future redesign.

### 1.2 `when'` at run time versus `if` at construction time

Having both, and having them be visibly different, turned out to matter more than expected.
`when'` skips a stage that exists; `if` decides which stages exist. Our `docs` stage uses `if`
because watch mode and build mode are different shapes:

```fsharp
stage "docs" {
    if watch then stage "watch" { run "dotnet fsdocs watch --eval" }
    else stage "build" { run "dotnet fsdocs build --eval --clean" }
}
```

while `--quick` uses `when'` everywhere, so the skipped stages still appear in the run. That
distinction is now written into our repository rules as house style. It is a good design that
most task runners get wrong by offering only one of the two.

### 1.3 Stages nest, and nested stages can be generated

The per-project and per-fixture stages in our scripts are computed, not written:

```fsharp
stage "pack" {
    quiet
    for project in projects do
        stage $"pack-{project.Name}" {
            run (cmd $"dotnet pack {project.Path} -c {config} --no-build --no-restore -v q -o bin")
        }
}
```

Twenty-one fixtures become twenty-one named stages from a `for` over a list. Being able to put
ordinary F# — `let`, `for`, `if`, a locally computed `wanted` predicate — inside a stage body
is the thing YAML pipelines cannot do, and the reason this library exists.

### 1.4 `parallel' 4`

One word, and the parallelism itself has never been the problem. Our fixture installer runs 21
npm installs four at a time; the one failure we hit came from our own duplicate list entry
putting two workers in one directory. That is the correct ratio of ceremony to power.

### 1.5 The `run` overload set

`run "literal string"`, `run (cmd $"interpolated {value}")`, `run (fun _ -> Result<unit,string>)`
and `run (fun _ -> async { ... Result<Cmd option,string> })` covered every case in four
scripts. We have never dropped down to `Process.Start`, and our `findings` command — 60 lines
of `System.Text.Json` inside a `run` — sits in the same file as the shell-outs without
friction.

### 1.6 The `Input` pipeline

```fsharp
Input.option<bool> "--no-run-gate"
|> Input.description "Skip the Fable run gate, much the slowest step..."
|> Input.def false
```

Discoverable by autocomplete, readable top to bottom, and vastly better than
`System.CommandLine`'s constructor-and-property dance. The README's "we don't need CEs
EVERYWHERE" instinct is correct, and the result proves it.

### 1.7 It is a library in a script

No global tool to install, no bootstrap project, no `dotnet build` before `dotnet run`. FAKE's
`Shell.cleanDirs` and its globbing operators drop in alongside without a wrapper. CI is one
`run:` line. This is the property that made us delete `Build.fsproj`, and it should be
defended against any future feature that would require a project file.

---

## Part 2 — Sore points

Each of these is something we actually wrote, in the shape we actually wrote it, with what it
cost.

### 2.1 `Internal.InputSpec` in a public signature

`build.fsx:145`:

```fsharp
let build (projects: Internal.InputSpec<string list>) =
    input { let! projects = projects
            and! config = Options.config
            ... }
```

To write a stage factory parameterised by an option — which the README itself presents as the
idiomatic shape — the annotation has to name a type in a namespace called `Internal`. Every
reader of our build script now has a reasonable question about whether we are using an
unsupported API, and the honest answer is that we do not know. Either `InputSpec<'T>` is part
of the contract, in which case it should not be reachable only through `Internal`, or it is
not, in which case parameterised stage factories need a supported alternative.

`tools/xantham-fixtures.fsx` goes further and does `open Partas.Build.Internal` at module scope
to reach `InputSpec.ret`. That is a load-bearing `open Internal` in a script that runs in CI.

### 2.2 Two option worlds, and a conversion we perform by rote

`Input<'T>` and `InputSpec<'T>` are both real, both necessary, and the seam between them shows
up at nearly every declaration:

```fsharp
let config =
    Baked.Input.DotNet.configString
    |> InputSpec.ofInput
    |> InputSpec.map (Option.defaultValue "Release")
```

Three lines to say "configuration, defaulting to Release". A baked, well-known option arrives
as `string option` with no default, so every consumer re-supplies one; and the shaping has to
happen after a conversion, because `Input.def` and `InputSpec.map` live on opposite sides of
the seam. We could not form a rule for which functions live where, so in practice we write
`|> InputSpec.ofInput |> InputSpec.map ...` whenever anything needs shaping and stop thinking
about it.

### 2.3 The worst thing in our repository

```fsharp
let projects =
    Spec.srcProjects
    |> List.map _.Name
    |> Baked.Input.Project.target
    |> Input.def (Spec.srcProjects |> List.filter _.Name.EndsWith("Wire") |> List.map _.Name)
    |> Input.customParser (fun res ->
        res.Tokens
        |> Seq.map (fun token ->
            Spec.srcProjects
            |> List.find _.Name.Equals(token.Value, System.StringComparison.OrdinalIgnoreCase)
            |> _.RelativePath)
        |> Seq.toList)
    |> InputSpec.ofInput
    |> InputSpec.map (fun projects ->
        Spec.srcProjects
        |> List.filter (_.RelativePath >> List.contains >> fun fn -> fn projects)
        |> function
            | [] -> Spec.srcProjects |> List.filter _.Name.EndsWith("Wire")
            | projects -> projects)
```

All of this says: *take project names on the command line, offer the real ones as completions,
reject anything else, and hand my stage the project records.* It is twenty lines; it maps names
to paths and then paths back to records; it re-states the default twice because we could not
tell whether `Input.def` survives a `customParser`; and `List.find` throws an unhandled
exception on a token that does not match, rather than producing a CLI validation error. Three
of `build.fsx`'s nine commands then re-map it at the use site:

```fsharp
Stages.build (Options.projects |> InputSpec.map (List.map _.RelativePath))
```

The underlying need — *an option whose legal values are a known set of typed things* — is the
single most common option shape in any build script, and it is the one the library serves
worst.

### 2.4 `cmd` quoting forces us to branch on whole command lines

From `build.fsx`, comment and all, because we learned this the hard way:

```fsharp
// `cmd` quotes each interpolation hole as one argument, so the flag and its value
// have to be part of the format string rather than a pre-baked `" --filter ..."`
// hole - that arrives as a single argument and MSBuild rejects it as one switch.
let suite =
    if System.String.IsNullOrWhiteSpace filter then
        cmd $"dotnet test {Repo.Project.SolutionFile} -c {config} --no-build"
    else
        cmd $"dotnet test {Repo.Project.SolutionFile} -c {config} --no-build --filter {filter}"
```

The quoting rule itself is right — it is what makes paths with spaces work. The problem is
that "add a flag conditionally" is the most common thing anyone does to a command line, and
the only expressible form is duplicating the entire line. With three optional flags that is
eight branches, so in practice people abandon `cmd` for unquoted strings, which is exactly the
failure the quoting existed to prevent.

### 2.5 Environment variables: we wrote a bug, and the DSL let us

```fsharp
stage "regenerate goldens" {
    when' update
    run (fun _ -> System.Environment.SetEnvironmentVariable("XANTHAM_UPDATE_GOLDEN", "1"); Ok())
    run suite
    run (fun _ -> System.Environment.SetEnvironmentVariable("XANTHAM_UPDATE_GOLDEN", null); Ok())
}
```

Three `run`s to give one command an environment variable, mutating the *fsi process's* globals
to do it. If `run suite` fails, the unset never executes, and every later stage in the same
process — including the checking run of the same suite immediately below — inherits
`XANTHAM_UPDATE_GOLDEN=1`. That turns a failed regeneration into a suite that silently
overwrites the goldens it was supposed to be checking. We have not been bitten yet; that is
luck, not design. There is no way to express this correctly with the operations we could find.

### 2.6 Secrets go through string interpolation

```fsharp
stage "publish" {
    when' apiKey.IsSome
    failIfIgnored
    run $"dotnet nuget push {path} -k {apiKey.Value} -s https://api.nuget.org/v3/index.json --skip-duplicate"
}
```

Two problems in four lines. The NuGet key is interpolated into a string the library may echo,
and we are relying on an undocumented default rather than a guarantee. And `apiKey.Value`
directly under `when' apiKey.IsSome` is the classic shape that is correct only because `when'`'s
evaluation order says so — the compiler is not checking it, and a refactor that moves `when'`
below the `run` compiles fine and throws in CI at the last stage of a release.

### 2.7 Cross-script composition happens over a process boundary

`build.fsx` invokes its own sibling by string:

```fsharp
stage "generate ast"     { when' (wanted "ast");     run "dotnet fsi tools/generate-wire.fsx -- generate ast" }
stage "generate proto"   { when' (wanted "proto");   run "dotnet fsi tools/generate-wire.fsx -- generate proto" }
stage "generate session" { when' (wanted "session"); run "dotnet fsi tools/generate-wire.fsx -- generate session" }
stage "generate browser" { when' (wanted "browser"); run "dotnet fsi tools/generate-wire.fsx -- generate browser" }
```

`generate-wire.fsx` already declares `generate ast|proto|session|browser` as real subcommands
with real options. Because a `command` cannot cross a file, `build.fsx` re-implements the
choice as a `--only <string>` flag whose legal values live in a description string:

```fsharp
let generateOnly =
    Input.option<string> "--only"
    |> Input.description "Limit generation to one layer: ast | proto | session | browser. All four by default."
```

Four layer names, spelled twice, in two files, with no compiler checking the agreement — plus
four fsi startups and four NuGet resolutions per `generate` run. The composition story is
excellent inside a file and absent between files, and repositories of any size are made of
more than one file.

### 2.8 The root command has no identity

```
$ dotnet fsi build.fsx -- --help
Description:

Usage:
  fsi [command] [options]
```

The tool is called `fsi`, the description is blank, and the usage line tells a new contributor
to run `fsi build`. We found no way to set the root name or description from the `rootCommand`
CE, so every script built this way ships help text naming the wrong program.

### 2.9 Repeated `workingDir`, unclear inheritance

`workingDir` appears six times in `build.fsx`, including once on the root command and again on
stages nested inside commands that already set it. We could not determine from the README
whether a child stage inherits its parent's working directory, so we set it defensively
everywhere. That is cheap insurance, but it is noise, and noise in a build script is how
mistakes hide.

### 2.10 No visible cost model

Our `test` command runs about 90 seconds. We know from instrumenting it by hand that the Fable
run gate is most of it, which is why `--no-run-gate` exists. The library prints no per-stage
timing, so that measurement was stopwatch-and-eyeball, and it is not reproducible in CI.

### 2.11 `pipeline` is in the README and in none of our scripts

Across four scripts and 21 stages we write `command { stage; stage }` and never once wrote
`pipeline`. The README's headline example uses
`command "build" { description ...; pipeline "build" { ... } }`. Either `pipeline` is optional
sugar, in which case the README should not lead with it, or it provides something we have been
missing for a year, in which case nothing told us.

---

## Part 3 — The wish list

Ordered by what we would trade for what.

### W1. Mount another script's commands as subcommands

*The big one.* Let a `#load`ed script expose its command tree as a value, and let another
script graft it in:

```fsharp
// tools/generate-wire.fsx
let generateCommands =
    command "generate" {
        description "Regenerate a wire layer"
        command "ast"     { requireUpstream; generateAst }
        command "proto"   { generateProto }
        command "session" { generateSession }
        command "browser" { generateBrowser }
    }

rootCommand fsi.CommandLineArgs[1..] { generateCommands }
```

```fsharp
// build.fsx
#load "tools/generate-wire.fsx"

rootCommand fsi.CommandLineArgs[1..] {
    command "generate" {
        Stages.deps
        mount GenerateWire.generateCommands     // options, help and all
    }
}
```

This deletes our `--only` flag, deletes the duplicated layer names, deletes four process
launches, and makes `build.fsx -- generate --help` show the real per-layer options instead of a
prose list. The unit of reuse today is the stage; the unit of reuse we need is the command.

If a single process is too strong a promise, a weaker version still helps enormously: a way to
declare "this stage delegates to command *X* of script *Y*", so the library builds the
`dotnet fsi ...` line, forwards the options the two share, and surfaces the callee's help.

### W2. `--explain` — print the resolved stage tree without running it

The library knows the whole tree, every `when'` verdict, and every command line before it runs
anything. Nobody else does:

```
$ dotnet fsi build.fsx -- test --quick --no-run-gate --explain
test
├─ restore              skipped (--quick)
├─ clean                skipped (--quick)
├─ format               skipped (--quick)
├─ npm install          skipped (--quick, borrowed XANTHAM_TSGO_EXE)
├─ initialise fixtures  skipped (--quick)
└─ test
   ├─ $ dotnet build /…/Xantham.slnx -c Release -v q
   ├─ regenerate goldens  skipped (--update not set)
   ├─ $ dotnet test /…/Xantham.slnx -c Release --no-build
   └─ run gate          skipped (--no-run-gate)
```

For a human this answers "what does `publish` actually do to my repository" without reading 479
lines of F#. For an agent working in this repository — which is most of the traffic our build
script sees — it is the difference between reading the script and asking the script. It is also
the cheapest possible feature relative to its value, because the information already exists in
the object graph once argument parsing is done.

A `--dry-run` that additionally validates working directories and executable presence would be
the CI-preflight version of the same idea.

### W3. Environment variables scoped to a stage

```fsharp
stage "regenerate goldens" {
    when' update
    env "XANTHAM_UPDATE_GOLDEN" "1"      // set for this stage and its children, restored after
    run suite
}
```

with the guarantee that it is restored on failure as well as on success, and that it reaches
child processes. Useful companions: `envIf cond key value` and `envFrom (Map ...)`. This
replaces our three-`run` sandwich and removes a real latent bug (§2.5). If per-stage scoping is
hard, per-`run` scoping — `run (cmd "..." |> Cmd.withEnv "K" "V")` — solves 90% of it.

### W4. Command lines as arguments, and conditional flags

Keep `cmd`'s quoting; add a way to build the argument list directly:

```fsharp
run (exe "dotnet" [ "test"; string Repo.Project.SolutionFile; "-c"; config; "--no-build" ])
```

and combinators for the conditional case, which is the one that hurts:

```fsharp
run (
    cmd $"dotnet test {Repo.Project.SolutionFile} -c {config} --no-build"
    |> Cmd.argIf (filter <> "") [ "--filter"; filter ]
    |> Cmd.argIf update [ "--blame-hang" ]
)
```

Our `test` stage collapses from an `if/else` over two full command lines to one line plus a
condition, and it stays correct when the third optional flag arrives.

### W5. Secret inputs, and conditions that carry their value

Two related asks. First, let an input be marked secret, so the library redacts it everywhere it
echoes — command lines, error messages, `--explain` output:

```fsharp
let apiKey = Baked.Input.NuGet.apiKeyOrEnv |> Input.secret
```

Second, a form of `when'` that binds the value it tested, so `.Value` never appears in a build
script:

```fsharp
whenSome apiKey (fun key ->
    stage "publish" {
        failIfIgnored
        run (cmd $"dotnet nuget push {path} -k {key} -s https://api.nuget.org/v3/index.json --skip-duplicate")
    })
```

Publishing is the one stage where a mistake is public and permanent, and it is currently the
stage with the least type safety in our script.

### W6. A run summary: timings, and the failing stage

At the end of a run, per-stage wall time and a clear statement of what failed:

```
test                    88.4s
├─ build                 9.1s
├─ dotnet test          21.7s
└─ run gate             57.6s   ← failed (exit 1)
      $ dotnet fable . -o fable-out --noCache --run node ...
```

`--no-run-gate` exists because we measured this by hand. Every optimisation anyone makes to a
build script starts with this table, and every task runner that has it gets used to tune builds
while the ones without it do not.

Adjacent, and nearly as valuable: **capture a stage's output and print it only on failure**.
Our run gate produces hundreds of lines of Fable output that nobody reads unless it breaks.
`quiet` hides it including on failure; the useful mode is the third one.

### W7. One option type, and no `Internal` in our signatures

Retire the `Input`/`InputSpec` split at the surface, or publish `InputSpec<'T>` under
`Partas.Build` with `map`, `ret`, `apply` and friends alongside it, so that:

```fsharp
let build (projects: InputSpec<string list>) = ...
```

compiles without a namespace that tells the reader they are somewhere they should not be. If
the two types must stay, a single documented rule for which combinators live on which — and
`Input.map` / `Input.def` mirrored on both — would remove the rote conversion in §2.2.

### W8. An option over a known set of typed values

The fix for §2.3. One declaration that produces completions, validation, help text and typed
values:

```fsharp
let projects =
    Input.choices<ProjectInfo> "--project" (Spec.srcProjects |> List.map (fun p -> p.Name, p))
    |> Input.alias "-p"
    |> Input.desc "Projects to act on"
    |> Input.defaults (Spec.srcProjects |> List.filter _.Name.EndsWith "Wire")
    |> Input.caseInsensitive
```

`--project Xantham.Nope` should then produce `'Xantham.Nope' is not one of: …` from
`System.CommandLine`'s own validation path, not an unhandled `KeyNotFoundException` from our
`List.find`. Twenty lines become six, `List.map _.RelativePath` disappears from three command
bodies, and the failure mode becomes a help message.

A narrower version — `Input.parseWith : (string -> Result<'T, string>)`, where `Error` becomes a
parse diagnostic instead of an exception — would fix the crash even without the completion
story.

### W9. Give the root command a name and a description

```fsharp
rootCommand fsi.CommandLineArgs[1..] {
    name "build.fsx"
    usage "dotnet fsi build.fsx -- <command> [options]"
    description "The Xantham repository build"
    ...
}
```

so `--help` stops telling people the program is called `fsi`. A default derived from
`fsi.CommandLineArgs[0]` would fix most of it with no new API at all.

### W10. `timeout` and `retry`

```fsharp
stage "npm install" {
    timeout (TimeSpan.FromMinutes 5)
    retry 2
    run "npm install"
}
```

Our network-touching stages are `npm install`, the fixture installer's 21 installs, and
`sync tsc-ast`'s GitHub fetches. All three hang rather than fail on a bad network, and a hung
CI job costs a runner-hour and tells you nothing.

### W11. Buffered per-stage output under `parallel'`

`parallel' 4` interleaves four npm installs' output into unreadable soup. Buffering each
stage's output and flushing it as a block on completion — the standard approach — would make
the parallelism usable for anything you actually need to read.

### W12. Baked prefabs: expose the parts, name the arguments

```fsharp
Baked.Pipelines.bumpArgument
    (Spec.srcProjects |> List.map _.RelativePath)
    (Options.projects |> InputSpec.map (List.map _.RelativePath))
```

At the use site this says nothing about what it will do — does it rewrite `<Version>`, does it
touch `<AssemblyVersion>`, does it commit? — and the two positional list arguments are easy to
transpose and impossible to tell apart by type. We would rather write:

```fsharp
Baked.Pipelines.bump {
    Projects = Spec.srcProjects
    Selected = Options.projects
    AssemblyVersion = MajorOnly
}
```

and, more importantly, be able to take *one stage* out of a prefab. Prefabs are a good idea
that is currently all-or-nothing; if `Baked.Pipelines.bump` were a record of composable stages
rather than an opaque pipeline, we would use several more of them.

### W13. Smaller things

- **`Input.desc` and `Input.description` are both real.** Our repository uses `description` in
  `build.fsx` and `desc` in `generate-wire.fsx`, written weeks apart by the same author. Pick
  one and `[<Obsolete>]` the other.
- **Warn on an undescribed command.** See §4.1 — our top-level help is blank because the CE
  never asked us for a description. A build-time warning, or making `description` the first
  required operation of `command`, would have caught it.
- **`rootCommand` without the args slice.** `fsi.CommandLineArgs[1..]` appears in all three of
  our scripts and is pure ceremony; a script-mode default would remove it.
- **Document `workingDir` inheritance**, so we can delete four defensive calls.
- **Document what a `run` returning `Error` does** to the exit code, to the remaining stages,
  and to the parent stage. We have never deliberately tested it, which means our error paths
  are untested by inspection.
- **`--version` for the script**, reporting the pinned Partas.Build version, would make bug
  reports like this one more precise.

---

## Part 4 — Feedback on the quality of *our* usage

The library deserves a fair split of the blame, so here is where Xantham is holding it wrong.
Several of these are worth reading as evidence of what the DSL makes easy versus what it makes
correct.

### 4.1 Not one of our seventeen commands has a description

```
Commands:
  format
  bump <BUMP_TYPE>  [default: Patch]
  build
  generate
  ...
```

`description` is available on `command` — the README's own example uses it — and we have not
written it once, across three scripts and seventeen commands. Our per-*option* help is
genuinely good, because
`Input.description` sits in a pipeline where you cannot miss it; our per-*command* help is
nonexistent, because `command "test" { ... }` is a complete, compiling, working command without
it. **The DSL made the wrong thing easy and we took it.** This is ours to fix, and also the
clearest single argument for W13's second bullet.

### 4.2 We describe enumerations in prose instead of declaring them

```fsharp
Input.option<string> "--only"
|> Input.description "Limit generation to one layer: ast | proto | session | browser. All four by default."
```

`Input.acceptOnlyFromAmong` and `Input.helpName` both exist — we use them nowhere. The result is
`--only <only>` in help text, no validation, and a typo that silently generates nothing.
Rendering that as `--only <ast|proto|session|browser>` with real validation is a one-line change
we should make regardless of anything in Part 3.

### 4.3 `Options.projects` should have been three functions

Even inside today's API, §2.3 is worse than it needs to be. The name → path → record round trip
exists because we never decided up front what a stage wants, and the duplicated default exists
because we did not test whether `Input.def` survives `customParser` — we guessed, then wore belt
and braces. That is our engineering, not the library's.

### 4.4 We never used `pipeline`, and never found out why

Zero occurrences across four scripts. If it offers ordering, failure semantics or reporting we
do not have, we have been going without and never noticed. We should read the docs properly;
the docs should also make it obvious whether skipping it is fine.

### 4.5 Our `--quick` conflates five things

`--quick` skips restore, clean, format, `npm install` *and* fixture initialisation. It is one
flag for "I know my working tree is set up", which is convenient and imprecise — a contributor
who wants to skip the format pass but keep the restore has no way to say so. Per-stage `when'`
would support finer flags easily; we simply have not split it.

### 4.6 We shell out to ourselves, which is partly our own choice

W1 would make this better, but `build.fsx`'s generate stages could already share option
declarations with `generate-wire.fsx` through a `#load`ed module holding the `Input` values, the
way `tools/workspace.fsx` is shared. We duplicated the layer names because it was three lines
faster on the day.

### 4.7 What we do well

For balance: our stages are small, single-purpose and reused across five commands; `when'` and
`if` are used deliberately, with the distinction written into the repository's rules; every
option has a default that makes the bare command correct for this repository; and we route
fine-grained work to `tools/*.fsx` rather than bloating the root command with `build-wire`,
`build-tests`, `build-docs`. That discipline is possible because the library composes well, and
we would not have arrived at it on top of a YAML runner.

---

## Summary

If exactly three things were built, we would take, in order:

1. **W1 — mount commands across scripts.** It is the difference between a script and a build
   system.
2. **W2 — `--explain`.** The cheapest large win available; the data already exists.
3. **W3 — scoped `env`.** It closes a real latent bug in our release path.

And if only one thing were *removed*: `Internal`, from the type we have to write in our own
signatures.

The verdict, plainly: Partas.Build let us delete a build project, and its option model — declare
at the point of use, register automatically, dedupe by construction — is better than anything
else we have used in .NET. What it is missing is not power but *visibility and reach*: it cannot
tell you what it is about to do, and it cannot see past the edge of one file.
