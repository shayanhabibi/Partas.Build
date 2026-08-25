---
title: External Annotations
category: Documentation
index: 2
---

# External Annotations

ReSharper and Rider read code annotations — `[<LanguageInjection>]`, `[<NotNull>]`, `[<StringFormatMethod>]`
and the rest — from two places: an assembly's own metadata, and an **external annotations** sidecar named
`<AssemblyName>.ExternalAnnotations.xml` sitting next to the assembly. The sidecar is honoured regardless of
what the assembly's metadata says.

`Partas.ExternalAnnotations` generates that sidecar from the attributes already in your assembly and ships it
inside `lib/<tfm>/` of your NuGet package, so the annotations survive a binary reference.

## Why it exists

`Partas.Solid` carries ~596 `[<LanguageInjection>]` attributes on F# optional type extensions, and **none of
them reached consumers**:

- ReSharper's code-annotation support is C#-first, and its handling of F# extension members — which compile to
  mangled static methods — is idiosyncratic.

A sidecar sidesteps both. A Rider 2026.2 harness established what actually injects across a binary reference:

| Site | Injects |
|---|---|
| ordinary static method, parameter-level | yes |
| mangled F# extension setter, **member**-level | **yes** |
| mangled F# extension setter, parameter-level | no |

The generator therefore emits at member level wherever the attribute sits on a member, which is what makes all
596 sites resolve.

## The pieces

| Project | TFMs | What it is |
|---|---|---|
| `Partas.ExternalAnnotations` | `netstandard2.0;net8.0` | The generator. Reflection-only scan of an assembly, XML doc-id emission. No dependency on Partas.Build. |
| `Partas.Build.ExternalAnnotations` | `net10.0;net8.0;netstandard2.0` | Partas.Build stages and commands over the generator, plus the MSBuild `.targets` as an embedded resource and a packed `build/` asset. |
| `Partas.ExternalAnnotations.Tool` | `net8.0` | The `partas-annotations` dotnet tool: a `rootCommand` over the library's three commands, presentation only. |

The tool exists because MSBuild has to shell out to *something* during pack, and a dotnet tool is the one form
of "something" a consumer can restore from a manifest. It cannot drift from the library, because it contains no
logic of its own — see [the F# surface](external-annotations-api.html).

The scan uses `System.Reflection.MetadataLoadContext`, so your assembly is never loaded for execution and its
target framework is irrelevant: a `net8.0` tool host produces byte-identical output from a `net6.0` assembly.

## Quick start

```shell
dotnet tool install --local Partas.ExternalAnnotations.Tool

# writes Directory.Build.targets, pinning the generator command inside it
dotnet partas-annotations init --annotations-tool "dotnet partas-annotations"

git add Directory.Build.targets   # commit it; see below

dotnet pack -c Release
dotnet partas-annotations verify --package bin/Release/My.Lib.1.0.0.nupkg --min-members 1
```

That is the whole loop. After `init`, every pack is correct — `dotnet pack`, CI, and Rider's Pack button
alike — because the logic lives in the repository rather than in a build script.

### The three commands

```shell
partas-annotations generate --assembly PATH --output PATH [--strict]
partas-annotations verify   --package PATH [--min-members N]
partas-annotations init     [--directory PATH] [--annotations-tool COMMAND] [--force]
```

- `--strict` promotes skipped members from a warning to a failure. A skip means those annotations are silently
  absent from the output, so use it for a library whose count you know.
- `--min-members N` fails a sidecar that exists but annotates fewer members than that. Absence is always a
  failure; an *empty* sidecar is only one when you say what you expect.
- `init` refuses to clobber an existing `Directory.Build.targets` unless given `--force`.

## What the targets file does

`Directory.Build.targets` hooks `TargetsForTfmSpecificContentInPackage`, which runs **once per inner build**, so
`$(TargetFramework)` is the real TFM even when multi-targeting and each TFM's `lib/` folder gets annotations
generated from *its own* assembly. (A plain `<None Pack="true" PackagePath="lib\$(TargetFramework)\">` does not
work: `None` items are evaluated in the outer build, where `$(TargetFramework)` is empty.)

Per inner build, in order:

1. `PartasGenerateExternalAnnotations` — if a generator command is configured, delete the previous output and
   `Exec` the tool against `$(TargetPath)`, capturing its exit code.
2. `PartasPackExternalAnnotations` — pack the generated file if generation exited `0`, else the committed
   fallback file if one exists, else emit a warning naming the path it looked for.

Properties:

| Property | Meaning |
|---|---|
| `PartasExternalAnnotationsTool` | Command that generates the file, e.g. `dotnet partas-annotations`. Empty means generate nothing and pack whatever file already exists. |
| `PartasExternalAnnotationsFile` | Path of the annotations file. Defaults to a per-TFM path under `obj\` when generating, and to `ExternalAnnotations\$(AssemblyName).ExternalAnnotations.xml` when packing a pre-existing file. |

Two injection routes, and both may be active at once without duplicate-import warnings:

1. A committed `Directory.Build.targets` — the normal one.
2. `dotnet pack -p:CustomAfterMicrosoftCommonTargets=<absolute path to the targets file>` — for projects you
   cannot commit into. `ExternalAnnotations.packArgs` builds that argument.

`Exec` runs with `ContinueOnError="WarnAndContinue"`: a missing or unrestored tool degrades to packing the
committed file rather than failing your build.

## Design decisions

**`init` is a developer command, not a CI stage.** The file it writes is ordinary build configuration, meant to
be reviewed and committed. Running it in CI would make package contents differ between local and CI machines
with nothing visible to show for it.

**Generation happens at pack time, not in a build stage.** Staleness is then structurally impossible: the
annotations come from the assembly being packed, in the same invocation. A build stage that generated them
beforehand would be correct only for packs that went through that stage — not for Rider's Pack button.

**A committed file is a valid fallback, not a second-class one.** Before the tool is published — or in a repo
that will not take a tool dependency — commit `ExternalAnnotations/<AssemblyName>.ExternalAnnotations.xml` and
packs are correct today, with no tool and no warnings.

**Failed generation must not ship the last good run's file.** The targets delete the previous output first
(only ever their own `obj/` path, never a caller-supplied one) and use the generated file only on exit code
`0`. Without this a broken generator silently ships stale annotations — found by testing, not by inspection.

**Skips warn and continue by default.** `--strict` is for pipelines with a known-good number; defaulting to
failure would let one type's reflection quirk break every consumer's build.

**The `netstandard2.0` leg is kept** at the cost of two conditioned polyfill packages, to keep the in-process
MSBuild-task route open for other library authors. `PackAsTool` cannot target it (`NETSDK1054`), so the tool is
`net8.0` with `RollForward=LatestMajor`.

## Known limits

- Parameter-level annotations on mangled F# extension members did not inject in the Rider 2026.2 harness, so
  put them on the member if you need them to take effect today. They are still **generated** — the sidecar is
  correct, the limitation is on the consuming side, and an annotation already in the file starts working the
  day that is fixed.
- Generic parameters are annotated too, on both types and methods (`<typeparameter>`). A nested type
  redeclares its enclosing type's generic parameters as its own, attributes included, so those are skipped —
  `Outer<T>.Inner<U>` gets `U` only, which is what the source declares.
- Several attributes on one parameter share a single `<parameter>` element rather than getting one each, which
  is what ~99.5% of ReSharper's own shipped annotation files do. Both forms occur in those files, so both are
  accepted.
- Annotations on property getters are inert in ReSharper; they are still emitted.
- The generator collects the whole `JetBrains.Annotations` namespace. Narrow it with `--attribute` or
  `AttributeFilter`.
- The attributes from the `JetBrains.Annotations` NuGet package are `[<Conditional("JETBRAINS_ANNOTATIONS")>]`,
  so unless the annotated project defines that constant they are stripped from its metadata and there is
  nothing for the generator to find. Assemblies declaring their own copies of the attributes are unaffected.
- Creating a `Directory.Build.targets` shadows any parent one. The emitted file chain-imports the parent to
  compensate, but if you already have one, merge by hand rather than passing `--force`.
