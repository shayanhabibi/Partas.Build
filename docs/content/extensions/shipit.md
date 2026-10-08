---
title: EasyBuild.ShipIt
description: Conventional-commit releases with changelogs and synchronized package versions.
category: Extensions
order: 1
---

`Partas.Build.EasyBuild.ShipIt` wraps [EasyBuild.ShipIt](https://github.com/easybuild-org/EasyBuild.ShipIt) as reusable operations, inputs, stages and commands. ShipIt calculates release versions from conventional commits, updates changelogs and configured project files, and can open release pull requests.

The extension supports `net10.0`, `net8.0` and `netstandard2.0`. The default ShipIt tool version, 3.1.0, requires .NET 10. Add the extension as a project/package reference using the [installation guide](../Build/installation.md).

## Add the commands to your build

```fsharp
open Partas.Build
open Partas.Build.EasyBuild.ShipIt

let root = Command.root {
    workingDir __SOURCE_DIRECTORY__
    addCommand Commands.shipit
    addCommand (command "bump" {
        Command.pipeline { Stages.bump }
    })
}

let build args = Command.invoke args root
```

`Commands.shipit` exposes `generate`, `github`, `bump`, `version`, `conventions`, `setup`, and `init changelog|workflows|github|project`. Each command registers the inputs its stages read. Help, schema and explain inspect the workflow without running setup or release operations.

For a build project, invoke it as `dotnet run --project Build.fsproj -- shipit ...`. For a script, use `dotnet fsi build.fsx -- shipit ...` after your usual root invocation. The following examples use the build-project form.

## Set up the local tool

```shell
dotnet run --project Build.fsproj -- shipit setup
```

Setup creates the Git repository's `.config/dotnet-tools.json` if absent, installs EasyBuild.ShipIt 3.1.0 only if unregistered, and restores the tools. Existing registrations retain their pinned version. `--tool-version` selects the version for a new registration. There is no global installation.

From a subdirectory, setup still uses the closest Git repository's manifest. Competing nested manifests or an alternate root `dotnet-tools.json` are rejected; invoke from the repository root or remove the competing manifest. Normal ShipIt prefab stages apply the same boundary check.

Setup is explicit: bumping does not install the tool or change GitHub settings.

## Keep package versions and changelogs together

```shell
dotnet run --project Build.fsproj -- shipit init project \
  --changelog src/MyLibrary/CHANGELOG.md \
  --project src/MyLibrary/MyLibrary.fsproj
```

Use a single line in PowerShell. This creates the changelog if missing and registers a ShipIt XML updater. Project/changelog input paths are relative to the stage's working directory; updater paths are relative to the changelog:

```yaml
updaters:
  - xml:
      file: MyLibrary.fsproj
      selector: /Project/PropertyGroup/Version
```

Configuration preserves existing front matter, comments, changelog body, line endings and UTF-8 BOM. Repeated setup does not duplicate equivalent XML updaters. Projects must contain exactly one unconditional literal `<Version>` under `<Project><PropertyGroup>`. Conditional, computed or externally defined versions require manual updater configuration. Existing `updaters` must use a nonempty block sequence; remove an empty key before initialization.

For independently versioned packages, use a changelog for each package. For a shared version, register multiple projects against one changelog using `--project`. When adopting an existing package, seed its changelog with its last released version and set `last_commit_released` to the correct commit. ShipIt calculates from the changelog's release history, rather than reading the `.fsproj` version as its baseline.

`AssemblyVersion` remains under your compatibility policy. This setup only registers updates for the package `Version`.

## Use the ShipIt-backed bump

```shell
dotnet run --project Build.fsproj -- shipit bump --allow-branch master --dry-run
dotnet run --project Build.fsproj -- shipit bump --allow-branch master
```

`Stages.bump`, `Stages.bumpWith` and `shipit bump` always use **local mode**: ShipIt calculates the version and updates the changelog and configured project files without pushing or creating a PR. Even consumer-supplied options with another mode are forced to local. The bump command has no `--mode` input.

Do not follow this with the agnostic `Partas.Build.Baked.SemVer` bump. ShipIt owns the release version; a second independent increment could make the changelog and package disagree. Review the local changes, commit them, then compose your normal build, pack and publish stages.

Consumer-defined inputs can supply the configuration directly:

```fsharp
let bump = Stages.bumpWith (InputSpec.ret {
    ReleaseOptions.defaults with
        AllowedBranches = [ "master" ]
        PreRelease = Some "beta"
})

let bumpCommand = command "bump" {
    Command.pipeline { bump }
}
```

Omit `PreRelease` for a stable CLI request, or supply a prefix such as `beta`. A prerelease configured in changelog front matter still applies when the CLI prefix is omitted.

## Release and initialize GitHub explicitly

`shipit generate` supports `--mode local|pull-request|push`, defaulting to `pull-request`. `shipit github` selects the GitHub provider explicitly and accepts a sensitive `--token`; it falls back to `GITHUB_TOKEN` or upstream `gh` authentication. Token values are masked in command labels, schema and explain; upstream tool output remains upstream's responsibility.

Release inputs also include `--allow-branch` (default `main`, multiple values), `--pre-release PREFIX`, remote hostname/owner/repository overrides, `--skip-invalid-commit`, `--skip-merge-commit`, and `--dry-run`. Use `--allow-branch master` for repositories using master.

`shipit init changelog` and `shipit init workflows` delegate upstream scaffolding, which refuses to overwrite existing files. `shipit init github` applies GitHub merge and workflow-permission settings using authenticated `gh`; preview them with `--dry-run`. `--org` additionally changes organization settings and requires the appropriate administrative access.

CI needs full Git history, conventional commits and suitable GitHub permissions/authentication. ShipIt generates release changes; it does not publish NuGet packages. See [upstream's configuration and CI recipes](https://github.com/easybuild-org/EasyBuild.ShipIt#configuration) for changelog include/exclude rules and release automation.

## Choose the composition level

- `Operations`: pure `Cmd` builders for upstream operations. Low-level callers supply their own working directory/tool resolution.
- `Inputs`: reusable typed CLI inputs, including token defaults evaluated per invocation.
- `Stages`: ready-made stages and `*With` variants for consumer-supplied `InputSpec` values. They inherit normal execution settings.
- `Commands.shipit`: the complete command tree for a build root.

The extension is also included in the [API reference](https://shayanhabibi.github.io/Partas.Build/reference/).
