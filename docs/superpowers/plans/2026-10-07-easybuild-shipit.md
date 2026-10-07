# EasyBuild.ShipIt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Ship a separately packaged extension exposing ShipIt operations, inputs, setup, commands, and a ShipIt-backed local bump.

**Architecture:** Pure Cmd builders describe upstream CLI operations. InputSpec-based stages compose them with Partas.Build execution; explicit setup owns tool registration and project-updater configuration. ShipIt alone calculates versions and applies release changes.

**Tech Stack:** F#, Partas.Build, System.Text.Json, YamlDotNet for front-matter preservation, System.Xml.Linq, Expecto, repository build CLI.

**Spec:** `docs/superpowers/specs/2026-10-07-easybuild-shipit-design.md`

## Global Constraints

- Project: `src/extensions/Partas.Build.EasyBuild.ShipIt/Partas.Build.EasyBuild.ShipIt.fsproj`; namespace/package name `Partas.Build.EasyBuild.ShipIt`.
- Package version 0.1.0; assembly identity 0.0.0.0; targets net10.0, net8.0, netstandard2.0.
- Reference Partas.Build directly. Never reference ShipIt's executable as a library or duplicate its algorithms.
- Default new tool installation pins EasyBuild.ShipIt 3.1.0; preserve existing registrations. Tool runtime currently requires .NET 10.
- No global installation, remote PR creation, publication, or GitHub settings changes during development/tests.
- ShipIt owns release versions. Bump is local-only; no Baked.SemVer increment. Project configuration leaves AssemblyVersion unchanged.
- Every shell command starts with rtk. Check callable SageFs tools before F# edits; if available follow the user's SageFs workflow, otherwise follow CLAUDE.md.

## Review Focus

- Hostile argument values and token values must retain argument boundaries and never leak into labels (Task 1).
- Running from a nested directory must use the intended repository manifest, without touching an ancestor repository (Task 2).
- Front matter with unknown keys/comments and Windows line endings must survive configuration (Task 3).
- Conditional or externally supplied MSBuild versions must produce a useful error before any file mutation (Task 3).
- Help/schema/explain must never install tools, edit files, or initiate releases (Task 4).

## File map

Extension files compile in this order: `Types.fs`, `Operations.fs`, `Inputs.fs`, `ToolSetup.fs`, `ProjectSetup.fs`, `Stages.fs`, `Commands.fs`. Each owns its named responsibility. Package README documents setup and examples. Tests live in `tests/Partas.Build.Tests/ShipItTests.fs`, registered in the existing suite with an extension project reference. Modify `Partas.Build.slnx`, `Build/Program.fs`, and root README for discovery, packing, and version bump registration.

## Task 1: Pure operation builders and package skeleton

**Files:** Create extension fsproj, Types.fs, Operations.fs; create ShipItTests.fs; modify main test fsproj and solution.

**Interfaces:**

```fsharp
type ReleaseMode = Local | PullRequest | Push
type ReleaseOptions = {
    AllowedBranches: string list
    Mode: ReleaseMode
    PreRelease: string option
    RemoteHostname: string option
    RemoteOwner: string option
    RemoteRepository: string option
    SkipInvalidCommit: bool
    SkipMergeCommit: bool
    DryRun: bool
}
// ReleaseOptions.defaults has main, PullRequest, None optional fields, false flags.
// Operations signatures, all returning Cmd:
// generate: ReleaseOptions -> Cmd
// github: ReleaseOptions -> string option -> Cmd
// version: unit -> Cmd; conventions: unit -> Cmd
// initChangelog: string option -> Cmd; initWorkflows: unit -> Cmd
// initGithub: bool -> bool -> Cmd  (organization, dryRun)
```

- [ ] Write failing tests against this contract; register them in the existing Expecto suite. Initial assertions:

```fsharp
test "local generation retains literal prefix boundaries" {
    let options = { ReleaseOptions.defaults with Mode = Local; PreRelease = Some "beta candidate" }
    let operation = Operations.generate options
    Expect.equal operation.Executable "dotnet" "dotnet launcher"
    Expect.sequenceEqual operation.Arguments
        [ "shipit"; "--allow-branch"; "main"; "--mode"; "local"; "--pre-release"; "beta candidate" ]
        "no shell splitting"
}
test "github token is marked secret" {
    let operation = Operations.github ReleaseOptions.defaults (Some "secret value")
    let index = operation.Arguments |> List.findIndex ((=) "secret value")
    Expect.isTrue (operation.Secrets.Contains index) "token must be masked"
}
```

- [ ] Build/run `rtk proxy dotnet run --project tests/Partas.Build.Tests -- --filter-test-list shipit --sequenced`; observe missing API failures before implementation.
- [ ] Create the library project using Baked's framework/package conventions. Use `Cmd.ofList "dotnet"` and `Cmd.args`, `Cmd.argWhenSome`, `Cmd.secretOption`; enumerate mode strings as local/pull-request/push. Emit repeated `--allow-branch` values according to verified upstream Spectre parsing. Validate an empty AllowedBranches list and blank optional values with ArgumentException rather than accidentally restoring main upstream.
- [ ] Extend operation tests across every optional argument/flag, repeated branches, all modes, blank values, executable tokens, omitted prefix, custom changelog paths with spaces, and GitHub init flags. Inspect actual Arguments, not just rendered strings.
- [ ] Run focused tests and commit `feat: add ShipIt operation builders`.

## Task 2: Explicit repository-local tool setup

**Files:** ToolSetup.fs; ShipItTests.fs.

**Interfaces:** `ToolSetup.runWith : (Cmd -> Async<int>) -> string -> string -> Async<unit>` takes executor, repository directory, version. `ToolSetup.run : string -> string -> Async<unit>` supplies real process execution via Partas.Build. Keep executor seam internal with InternalsVisibleTo the main tests.

- [ ] Write failing temporary-directory tests: absent manifest yields new-manifest, install, restore in order; registered package restores only; missing registration installs pinned/custom version; repeated setup does not reinstall; malformed manifest fails before execution. Fake executor simulates manifest creation/registration and records commands:

```fsharp
let recorded = ResizeArray<Cmd>()
let execute operation = async {
    recorded.Add operation
    return 0
}
// Use this executor for existing manifests; for missing manifests have the fake
// write the corresponding fixture after the new/install operation.
```

- [ ] Run focused tests and observe the expected missing implementation failure.
- [ ] Resolve the git repository boundary explicitly for the public setup stage; use `<root>/.config/dotnet-tools.json` and a single consistent working directory. Inspect manifest JSON using System.Text.Json (netstandard-only package reference consistent with main library). Fail on malformed tools/commands data. Preserve existing version; validate the supplied new version is nonblank. Create root manifest only when absent, install using explicit manifest path/version, then restore using explicit manifest path. Nonzero command exit stops setup with stage failure; never ignore it.
- [ ] Test nested directory resolution, ancestor-manifest isolation, missing git repository, executor failures, cancellation, and custom version argument boundaries. No PATH/global installation changes.
- [ ] Run focused tests and commit `feat: add explicit local ShipIt tool setup`.

## Task 3: Configure project version updaters without losing changelog data

**Files:** ProjectSetup.fs; extension fsproj dependency entries; ShipItTests.fs.

**Interface:** `ProjectSetup.configure : string -> string list -> unit` takes an existing changelog path and project paths. Upstream changelog creation is a preceding stage, used only if the file is absent. Configuration function itself does not execute processes.

- [ ] Write failing fixtures with project Version and AssemblyVersion, changelog front matter containing unknown nested keys, existing updaters/comments, body text, LF/CRLF and UTF-8 BOM. Assert preserved text and duplicate-free XML updater insertion:

```fsharp
let before = File.ReadAllText changelog
ProjectSetup.configure changelog [ project ]
let once = File.ReadAllText changelog
ProjectSetup.configure changelog [ project ]
Expect.equal (File.ReadAllText changelog) once "configuration is idempotent"
Expect.stringContains once "selector: /Project/PropertyGroup/Version" "ShipIt XML selector"
Expect.stringContains once "# retained comment" "existing configuration remains"
Expect.stringContains (File.ReadAllText project) "<AssemblyVersion>0.0.0.0</AssemblyVersion>" "identity unchanged"
```

- [ ] Run focused tests red. Verify/pin a YamlDotNet version supporting netstandard2.0 using official package/source data at implementation time; use its parser/representation nodes for structure validation and source marks for narrowly inserting entries. Avoid serializing all existing YAML, which loses comments and formatting.
- [ ] Validate all projects before writes: exactly one unconditional `/Project/PropertyGroup/Version` element, no Condition on node/group, nonempty literal version, no property expressions. Reject missing/duplicate/conditional/external versions with the project path and explanation. Resolve updater file paths relative to changelog with forward slashes. Validate front matter delimiters, mapping root, updaters sequence, and existing xml updater structure; reject ambiguous configurations. Insert only missing entries while preserving original bytes/text outside the insertion region, newline style, BOM, comments, and body. Commit replacement atomically after every validation succeeds.
- [ ] Add tests for duplicate project inputs, existing equivalent relative path updater, special YAML path characters, escaped filename, malformed YAML, conditional versions, no Version, property expressions, and multi-project all-or-nothing validation.
- [ ] Run focused tests and commit `feat: configure ShipIt project version updaters`.

## Task 4: Inputs, composable stages, and the command tree

**Files:** Inputs.fs, Stages.fs, Commands.fs; ShipItTests.fs.

**Interfaces:** `Inputs.release : InputSpec<ReleaseOptions>`; `Inputs.bump : InputSpec<ReleaseOptions>` does not register --mode and supplies Local; `Inputs.token : InputSpec<string option>`; `Inputs.toolVersion : InputSpec<string>`; setup/init inputs mirror operation parameters. `Stages.generateWith : InputSpec<ReleaseOptions> -> InputSpec<StageContext>` and `Stages.githubWith : InputSpec<ReleaseOptions> -> InputSpec<string option> -> InputSpec<StageContext>`; default counterparts use Inputs. `Stages.bumpWith` takes release options but forces Local, including for caller-supplied Push/PullRequest values. `Stages.bump` uses Inputs.bump. `Stages.setupWith` takes tool-version input; `Stages.setup` supplies default. `Stages.configureProjects` accepts changelog and project paths and explicitly initializes only a missing changelog. `Commands.shipit` supplies the composed command tree.

- [ ] Write failing tests using existing Helpers.parse to resolve specs and inspect stage labels. Assert release modes, branch defaults, token env/CLI override, invalid enum parsing, every init command, and consumer InputSpec.ret overrides.

```fsharp
let localOnly = Stages.bumpWith (InputSpec.ret { ReleaseOptions.defaults with Mode = Push })
let resolved = localOnly.Read (parse localOnly.Inputs "")
let labels = resolved.Steps |> List.choose (function
    | Step.Operation(ValueSome name, _) | Step.StepFn(ValueSome name, _) -> Some name
    | _ -> None)
Expect.isTrue (labels |> List.exists (fun s -> s.Contains "--mode local")) "bump cannot push"
```

- [ ] Run tests red. Build inputs using existing Input.option/def/description/validation and applicative input CE. Use optional string prefix (explicit beta supported); do not conflate absent prefix with beta. Bind token lazily per invocation from GITHUB_TOKEN with CLI precedence and mark input secret following existing NuGet input pattern. Stage construction is pure; runtime filesystem operations happen inside stage steps. Use existing process execution/cancellation/output paths for setup.
- [ ] Compose `shipit generate|github|bump|version|conventions|setup|init changelog|workflows|github|project`. Project initialization accepts a changelog path and repeatable project paths, and delegates to Stages.configureProjects. Do not expose an inherited --mode on bump.
- [ ] Run help/schema/explain through Command.root/invoke in an empty temporary repository. Assert zero created files/process calls, token masking, stable labels, separate release/setup inputs, and invocation exit codes. Test failure/cancellation through a fake/local child process fixture.
- [ ] Run focused tests and commit `feat: expose ShipIt-backed bump and commands`.

## Task 5: Repository integration, docs, and final verification

**Files:** Build/Program.fs, root README.md, extension README.md, fsproj packed README; ShipItTests.fs; docs compiler fixtures as needed.

- [ ] Register the extension in Project.allProjects so build/pack/bump include it; main suite project reference already makes tests part of existing execution. Validate generated project-path provider names after solution registration.
- [ ] Write docs showing local tool setup, project configuration, independently versioned packages versus shared versions, and composing Stages.bump in a command:

```fsharp
open Partas.Build
module ShipIt = Partas.Build.EasyBuild.ShipIt.Stages
let bumpCommand = command "bump" {
    Command.pipeline { ShipIt.bump }
}
```

- [ ] Add a local fixture end-to-end test using real ShipIt only as an opt-in tool-contract check: temporary Git repo, initialized changelog with released baseline, project XML updater, feat commit, then local generation. Assert project Version equals new changelog version and AssemblyVersion stays fixed. Default offline suite uses a fake executor and tests XML configuration/command contract separately. Document exact opt-in command and require .NET 10/tool 3.1.0 without installing them as a test side effect.
- [ ] Run opt-in contract verification in the implementation workspace if tool prerequisites are available; if unavailable explicitly report that coverage limitation. Confirm branch/default options against installed 3.1.0 help, not just upstream main.
- [ ] Run `rtk proxy dotnet run --project Build.fsproj -- test` as final gate, all declared framework builds, compiled documentation examples, and `rtk git diff --check`. Check package via local pack and inspect nuspec dependencies/README. Do not publish or open a release PR.
- [ ] Request whole-branch review, fix important findings, rerun only relevant tests then final gate for semantic fixes. Commit `docs: integrate ShipIt extension and usage` and report results. Preserve feature branch/worktree for user review; no merge or PR requested in this task.
