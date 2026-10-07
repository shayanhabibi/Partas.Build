# EasyBuild.ShipIt extension

## Intent

Provide a separately packaged Partas.Build extension wrapping the EasyBuild.ShipIt workflow as reusable operations, inputs, stages, and a command tree. The user approved the CLI-wrapper approach. ShipIt owns conventional commit parsing, changelog configuration, version calculation, release branches, and release pull requests.

## Package and dependencies

Create `src/extensions/Partas.Build.EasyBuild.ShipIt/Partas.Build.EasyBuild.ShipIt.fsproj`, package version 0.1.0, assembly identity 0.0.0.0, targeting net10.0, net8.0, and netstandard2.0 consistently with Baked. Reference Partas.Build directly; do not reference ShipIt's executable as a library. Namespace is `Partas.Build.EasyBuild.ShipIt`. Register the project with the solution and repository build, test, pack, and bump workflows. The extension's target frameworks do not remove the installed tool's runtime requirements: upstream currently targets .NET 10.

## Public surface

- `ReleaseMode`: Local, PullRequest, Push. Defaults match upstream (PullRequest).
- `ReleaseOptions`: allowed branches (main by default), mode, optional prerelease prefix, remote hostname/owner/repository, skip-invalid and skip-merge flags, and dry-run. Absence of a prerelease prefix disables prerelease; explicitly passing beta requests the upstream default prefix.
- `Operations`: pure `Cmd` builders for generation, explicit GitHub generation with optional secret token, version, conventions, changelog initialization with optional path, workflow initialization, and GitHub initialization with organization and dry-run settings. Arguments use Cmd's argument APIs, not shell-concatenated user values. Tokens use secret argument support.
- `Inputs`: typed inputs for each workflow option, shared by the prefabs and command tree. GitHub token supports GITHUB_TOKEN and CLI overrides. Boolean inputs default false. Typed modes reject invalid values during parsing.
- `Stages`: prefab `InputSpec<StageContext>` values/functions and corresponding consumer-input variants, following Baked conventions. Working directories are supplied through normal stage composition. Each stage propagates child failures and cancellation through the existing runner.
- `Stages.bump` and `Stages.bumpWith`: the extension's baked bump workflow delegates calculation and updates to ShipIt in local mode. It updates changelogs and configured project versions without creating a PR or pushing. It does not call Baked.SemVer or apply a second independent increment. Consumer inputs control allowed branches, prerelease, commit filters, remote details, and dry-run. A `shipit bump` command exposes it; generation/github commands separately expose remote release modes.
- `Commands`: a ready-made `shipit` command containing generation (a named `generate` subcommand), `github`, `version`, `conventions`, `setup`, and `init changelog|workflows|github`. A named generation command keeps initialization isolated from release-only inputs. Consumers can compose these with their own root command.

## Setup

Setup is an explicit stage/command, never an effect of inspecting help/schema/explain or constructing operations. It manages a repository-local dotnet tool manifest. Create a manifest only if absent, install EasyBuild.ShipIt only if missing, and restore tools. Existing registrations retain their configured version. New installs default to the verified stable 3.1.0 tool version, with a caller/input override. Do not silently update registrations or install globally. Manifest resolution and all commands use the same working-directory boundary; ambiguous or invalid manifests fail clearly. Repeating setup succeeds without overwriting existing files.

Changelog and workflow scaffolding delegate to upstream `init` commands, retaining upstream refusal to overwrite files. GitHub settings are a separate explicit init operation, including dry-run and optional organization changes. The extension does not run these effects against this repository during implementation/testing.

## Configuration boundary

ShipIt's CHANGELOG.md front matter remains the authoritative release configuration, including monorepo include/exclude rules and file updaters. Explain how to configure an XML updater for project versions in the extension README; do not invent a duplicate configuration model or automatically rewrite consumers' changelogs. Document Git/gh authentication, full Git history in CI, allowed release branches, .NET runtime requirements, and upstream setup side effects.

## Verification

## .NET project configuration

Provide an explicit setup operation accepting a changelog path and project path(s). Register ShipIt XML updaters targeting `/Project/PropertyGroup/Version`, with paths relative to the changelog. Create a missing changelog through upstream initialization; preserve existing changelog content, front matter, and unrelated updaters. Repeated configuration does not duplicate equivalent entries. Validate projects have an unambiguous Version element before editing configuration; reject conditional/multiple or externally defined versions with actionable guidance instead of guessing. AssemblyVersion remains under the consumer's compatibility policy and is not rewritten by this operation. ShipIt is the release-version authority; document not chaining the agnostic Baked bump after it. Independent package versions use separate package changelogs; shared versioning explicitly registers multiple project updaters on one changelog.

Test configuration preservation, relative paths, idempotency, ambiguous versions, and a local fixture release showing the changelog and project Version agree. Keep remote operations out of integration tests.

Add extension tests covering mode validation, defaults, every supported argument, omitted optional arguments, paths/prefixes containing spaces, token masking, consumer-supplied inputs, and command help/schema/explain discovery. Test setup against temporary manifests and a fake process boundary: absent manifest, existing registered tool, missing registration, custom version, repeated setup, and malformed manifest. Tests must not create remote PRs, publish packages, change GitHub settings, or depend on network tool installation. Run the repository's final test command and builds across declared frameworks. Validate documentation examples compile.

## Sources and exclusions

Contract checked against https://github.com/easybuild-org/EasyBuild.ShipIt README, src/Types/Settings.fs, src/EasyBuild.ShipIt.fsproj, and CHANGELOG.md on 2026-10-07. Current upstream changelog reports 3.1.0. No upstream algorithm port, automatic repository adoption, CI authentication provisioning, or live release execution is included.
