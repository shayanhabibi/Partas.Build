# Partas.Build.EasyBuild.ShipIt

Composable operations, inputs, stages and commands for [EasyBuild.ShipIt](https://github.com/easybuild-org/EasyBuild.ShipIt).
ShipIt calculates releases from conventional commits and updates changelogs and configured files. This extension delegates those algorithms to the local tool.

```fsharp
open Partas.Build
open Partas.Build.EasyBuild.ShipIt

let cli = Command.root {
    addCommand Commands.shipit
    addCommand (command "bump" {
        Command.pipeline { Stages.bump }
    })
}
cli |> Command.invoke [ "shipit"; "bump"; "--allow-branch"; "master"; "--dry-run" ]
```

## Explicit setup

Run your build CLI's `shipit setup` to create a repository-local tool manifest if missing, install EasyBuild.ShipIt 3.1.0 if unregistered, and restore the tools. `--tool-version` changes the version for a new registration; existing registrations retain their version. Setup resolves the closest Git repository and uses its `.config/dotnet-tools.json`, including when called from a subdirectory. It never installs globally. The extension supports net10.0, net8.0 and netstandard2.0; ShipIt 3.1.0 itself needs .NET 10.

Run `shipit init project --changelog src/MyLibrary/CHANGELOG.md --project src/MyLibrary/MyLibrary.fsproj` to initialize a missing changelog and register the project version updater. Initialization preserves existing YAML keys, comments, changelog body, newline style and UTF-8 BOM. Repeating it does not duplicate XML updaters. Project paths are relative to the stage working directory. The updater path is relative to the changelog:

```yaml
updaters:
  - xml:
      file: MyLibrary.fsproj
      selector: /Project/PropertyGroup/Version
```

Each project must have exactly one unconditional literal `<Version>` under `<Project><PropertyGroup>`. Configure conditional, computed or externally defined versions yourself. Use a block-style `updaters` sequence; remove an empty key before setup. Configuration does not alter `<AssemblyVersion>`: manage assembly compatibility separately. Register multiple projects on one changelog for shared versions; use separate changelogs for independent package versions. When adopting an existing package, seed the changelog with the last released version and set `last_commit_released` appropriately before running a bump.

## Bumping and releasing

`Stages.bump`, `Stages.bumpWith` and `shipit bump` always use **local mode**, updating the changelog and its configured project files without pushing or opening a PR. They do not use `Baked.SemVer`. Do not chain a separate numeric bump afterward; ShipIt owns the release version. `bumpWith` also forces local mode for consumer-supplied options.

`shipit generate` supports upstream modes `local`, `pull-request` (default) and `push`. `shipit github` explicitly selects GitHub and accepts `--token`, falling back to `GITHUB_TOKEN` or upstream gh authentication. Token values are marked sensitive in schema/explain and command labels. Upstream output itself remains upstream's responsibility.

Release inputs: `--allow-branch` (default `main`, multiple values), `--pre-release PREFIX` (omit for stable; pass `beta` for the standard prefix), `--remote-hostname`, `--remote-owner`, `--remote-repo`, `--skip-invalid-commit`, `--skip-merge-commit`, `--dry-run`. Use `--allow-branch master` for this repository's branch naming. A prerelease configured in changelog front matter still applies when the CLI prefix is omitted.

`shipit init changelog`, `shipit init workflows`, and `shipit init github` delegate upstream setup. Changelog/workflow creation refuses to overwrite existing files. GitHub initialization can change merge and workflow-permission settings; it requires authenticated `gh`. Use `--dry-run` to preview and `--org` only when organization settings should also change. No setup operation runs automatically during help/schema/explain or ordinary bumping.

For custom workflows, use `Operations` to build pure `Cmd` values, `Inputs` to reuse options, or the `Stages.*With` functions to bind consumer `InputSpec` values. Stages inherit normal working directory, output, environment and cancellation settings. CI requires full Git history and appropriate GitHub permissions/authentication; ShipIt is not a package publisher, so compose pack/push stages separately after release changes land.

## Verification

Offline coverage runs with `dotnet run --project tests/Partas.Build.Tests -- --filter-test-list shipit --sequenced`.
The opt-in tool contract probe uses a temporary Git repository and ShipIt 3.1.0 to verify a local release updates project and changelog consistently; it never creates remote releases or changes repository settings.

In PowerShell, set `$env:SHIPIT_TEST_TOOL` to the absolute path of an already installed `shipit.exe`, then run `dotnet run --project tests/Partas.Build.Tests -- --filter-test-list "shipit contract" --sequenced`. On Unix, set the same environment variable to the installed `shipit` executable path.
