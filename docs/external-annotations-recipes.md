---
title: External Annotations — Recipes
category: Documentation
index: 4
---

# External Annotations — Recipes

Concrete setups. Background is on the [overview](external-annotations.html); the F# API is
[here](external-annotations-api.html).

## Ship annotations from a repo you own

The default. One-time setup, then every pack is correct.

```shell
dotnet new tool-manifest              # if you have none
dotnet tool install Partas.ExternalAnnotations.Tool
dotnet partas-annotations init --annotations-tool "dotnet partas-annotations"
git add .config/dotnet-tools.json Directory.Build.targets
```

CI needs `dotnet tool restore` before `dotnet pack`; nothing else changes.

## Scope annotations to one project

A repo with a packable library, a test project and three samples wants the targets file next to the library,
not at the root — MSBuild takes the first `Directory.Build.targets` it finds walking up, so this leaves
everything else untouched:

```shell
dotnet partas-annotations init --directory src/My.Lib --annotations-tool "dotnet partas-annotations"
```

The emitted file chain-imports any parent `Directory.Build.targets`, so an existing root one keeps working.

## No tool available (or no tool dependency wanted)

Commit the sidecar instead. The targets file packs it when generation is not configured, and the path it looks
for is fixed:

```shell
# generate once, by hand, into the fallback location
dotnet partas-annotations generate \
  --assembly src/My.Lib/bin/Release/net8.0/My.Lib.dll \
  --output   src/My.Lib/ExternalAnnotations/My.Lib.ExternalAnnotations.xml

dotnet partas-annotations init --directory src/My.Lib   # no --annotations-tool
git add src/My.Lib/ExternalAnnotations src/My.Lib/Directory.Build.targets
```

Packs are correct today, with no tool and no warnings. Output is BOM-free and stably sorted, so regenerating it
produces a reviewable diff rather than a whole-file churn. Switch to generation later by re-running `init` with
`--annotations-tool ... --force`.

Caveat: a committed file is only as fresh as the last time you ran that command. Regenerate it in the same
change that adds or moves annotated members.

## Multi-targeted packages

Nothing to do. Generation runs once per inner build, from that TFM's own assembly, into a per-TFM path under
`obj\`. Confirm it, because the failure mode is silent and plausible-looking:

```shell
unzip -l bin/Release/My.Lib.1.0.0.nupkg | grep ExternalAnnotations
# lib/net8.0/My.Lib.ExternalAnnotations.xml
# lib/net10.0/My.Lib.ExternalAnnotations.xml
```

If both files are byte-identical *and* your TFMs expose different surfaces, something is pointing every inner
build at one path — check `PartasExternalAnnotationsFile`, which overrides the per-TFM default.

## Pack a project you cannot commit into

Inject the targets file for one invocation:

```shell
dotnet pack -c Release \
  -p:CustomAfterMicrosoftCommonTargets=$(pwd)/build/Partas.ExternalAnnotations.targets \
  -p:PartasExternalAnnotationsTool="dotnet partas-annotations"
```

The path must be absolute — MSBuild otherwise resolves it per project. From a pipeline, use `packArgs`, which
absolutises it for you. Write the file out first with `writeTargets` if it is not already in your repo.

## Gate CI on the annotation count

The check worth having is not "the file exists" but "the file annotates what it used to". `verifyPackageOf`
takes the floor:

```fsharp
let publish =
    pipeline "publish" {
        stage "pack" { run "dotnet pack -c Release" }
        verifyPackageOf 596 "bin/Release/My.Lib.1.0.0.nupkg"
        stage "push" { run "dotnet nuget push bin/Release/My.Lib.1.0.0.nupkg" }
    }
```

Or from the shell, once the package is built:

```shell
dotnet partas-annotations verify --package bin/Release/My.Lib.1.0.0.nupkg --min-members 596
```

Exit code `1` and a message naming each sidecar that is `(missing)` or short. Raise the floor when the number
goes up; a bare `--min-members 1` still catches the wrong-assembly case, which is the one that otherwise ships.

## Fail on skipped members

A skipped member is an annotation that is silently absent from the output. In your own library's pipeline,
where the count is known, promote it:

```shell
dotnet partas-annotations generate --assembly ... --output ... --strict
```

### MSBuild Limitation

Under MSBuild:

```xml
<PartasExternalAnnotationsTool>dotnet partas-annotations</PartasExternalAnnotationsTool>
```

`--strict` cannot be appended there — the targets build the whole `generate` command line — so for a strict
pack, generate in the pipeline with `generateTo` (which binds `--strict`) and point
`PartasExternalAnnotationsFile` at its output.

## Narrowing the attribute set

Everything in the `JetBrains.Annotations` namespace is collected by default. To ship only some of it, name the
attributes — either on the command line:

```shell
dotnet partas-annotations generate --assembly My.Lib.dll --output My.Lib.ExternalAnnotations.xml     --attribute NotNullAttribute --attribute LanguageInjectionAttribute
```

or in code, where `AttributeFilter.Where` also takes an arbitrary `namespace -> name -> bool` predicate:

```fsharp
open Partas.ExternalAnnotations

let r =
    generateWith
        (AttributeFilter.Named [ "NotNullAttribute" ])
        []
        "bin/Release/net8.0/My.Lib.dll"
        "obj/My.Lib.ExternalAnnotations.xml"
```

A pipeline stage can fix the set instead of leaving it to `--attribute`, with `generateOnlyTo`.

Both constructor arguments and named arguments (`Prefix`, `Suffix`, …) are carried through, including for
attribute types that reflection cannot materialise — those are decoded from the raw metadata blob.

## Adopt the commands without the tool

Your build CLI already exists; give it the three commands rather than a tool dependency:

```fsharp
open Partas.Build
open Partas.Build.ExternalAnnotations

[<EntryPoint>]
let main argv =
    rootCommand argv {
        description "My build"
        addCommands [ Commands.build; Commands.test; generateCommand; verifyCommand; initCommand ]
    }
```

> If not using `Partas.Build`, use function calls to `generateOnlyTo` and `verifyOnlyTo`.

Then pin *that* as the generator, and pack-time generation goes through your CLI:

```shell
dotnet run --project Build.fsproj -- init --annotations-tool "dotnet run --project Build.fsproj --"
```

Slower per pack than a tool — it builds the CLI first — but there is nothing extra to restore or publish.

## Inspect what would be emitted

Before committing to a number, dump every site the generator found:

```fsharp
open Partas.ExternalAnnotations

use gen = new ExternalAnnotationGenerator ("bin/Release/net8.0/My.Lib.dll")
printfn $"%d{gen.MemberCount} members / %d{gen.SiteCount} sites / %d{gen.TypeScanCount} types"
gen.PrintfMembers ()
```

`PrintfMembers` prints each member's XML doc id with its sites and decoded attribute arguments — the fastest way
to tell "the attribute is not where I thought it was" from "the sidecar is not reaching Rider".

## Troubleshooting

**Nothing injects in Rider.** In order: is the sidecar in the package (`unzip -l`)? Is it beside the assembly in
`lib/<tfm>/`, not in a folder of its own? Does it contain the member (`PrintfMembers` vs. the XML)? Is the
annotation at *member* level — parameter-level on a mangled F# extension member does not inject. Rider caches
external annotations per assembly version; bump the package version rather than repacking the same one.

**`MSB4011 ... will be ignored`.** Both injection routes are active and one of them is re-importing. Harmless,
but it means either the targets file is imported twice by path, or a `Directory.Build.targets` copy was placed
somewhere that makes its parent chain-import loop back.

**The package ships annotations that do not match the source.** Generation failed and the fallback shipped. The
targets only use a generated file on exit code `0`, so check the `Exec` output in the pack log — it runs at
normal importance, so `-v:n` shows it.

**"External annotations were not packed for &lt;tfm&gt;".** Neither generation nor a committed file produced
anything. The warning names the path it looked for; put a file there or configure
`PartasExternalAnnotationsTool`.

**`NETSDK1054` when packing the tool.** `PackAsTool` cannot target `netstandard2.0`. The tool is `net8.0` with
`RollForward=LatestMajor`; only the generator library multi-targets down.
