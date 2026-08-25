(**
---
title: External Annotations — F# surface
category: Documentation
index: 3
---
*)
(*** hide ***)
// Sources are #load-ed rather than #r-ing built DLLs so the guide type-checks against the code as written,
// and so nothing holds a file lock while `fsdocs watch --eval` is running. Keep this list in the same order
// as the <Compile> items of each project.
#r "nuget: FSharp.Control.AsyncSeq, 4.15.0"
#r "nuget: FsToolkit.ErrorHandling, 5.2.0"
#r "nuget: System.CommandLine, 2.0.11"
#r "nuget: Spectre.Console, 0.57.2"
#r "nuget: System.Reflection.MetadataLoadContext, 10.0.11"

#load "../src/Partas.Build/System.CommandLine/Aliases.fs"
#load "../src/Partas.Build/System.CommandLine/Inputs.fs"
#load "../src/Partas.Build/Types.fs"
#load "../src/Partas.Build/Process.fs"
#load "../src/Partas.Build/Builders/Stage.fs"
#load "../src/Partas.Build/Builders/Conditions.fs"
#load "../src/Partas.Build/Builders/Pipeline.fs"
#load "../src/Partas.Build/Builders/Inputs.fs"
#load "../src/Partas.Build/Builders/Command.fs"
#load "../Partas.ExternalAnnotations/Library.fs"
#load "../Partas.Build.ExternalAnnotations/Library.fs"

open Partas.Build
open Partas.Build.ExternalAnnotations

(**
# External Annotations — F# surface

[The overview](external-annotations.html) covers what external annotations are and why a sidecar is the only
reliable way to ship them. This page is the F# API, for a build CLI that would rather own the behaviour than
shell out to `partas-annotations`.

Everything here comes from `Partas.Build.ExternalAnnotations`, which is what the tool itself is built from.
Adopting one operation does not oblige you to take the others.

## Two entry points per operation

Each operation exists twice:

| Operation | Pipeline knows the paths | Paths come from the command line |
|---|---|---|
| generate | `generateTo assembly output` | `generateStage` / `generateCommand` |
| verify | `verifyPackage nupkg`, `verifyPackageOf min nupkg` | `verifyStage` / `verifyCommand` |
| init | `initIn directory` | `initStage` / `initCommand` |

Use the left column when your pipeline already knows the paths, the right when the user supplies them. Both
forms sit over one implementation.

## In a pipeline

Generation belongs after the build and before the pack. `generateTo` still binds `--strict` itself, so the
pipeline is an `InputSpec<PipelineContext>` and the flag appears under the command's `--help` without being
registered anywhere:
*)

let packPipeline =
    pipeline "pack" {
        stage "build" { run "dotnet build -c Release" }

        generateTo "src/My.Lib/bin/Release/net8.0/My.Lib.dll" "obj/My.Lib.ExternalAnnotations.xml"

        stage "pack" { run "dotnet pack -c Release --no-build" }

        verifyPackageOf 596 "bin/My.Lib.1.0.0.nupkg"
    }

(**
`verifyPackage` is the same check with a floor of `0`: every assembly under `lib/` must have a sidecar beside
it, but an empty sidecar passes. `verifyPackageOf` adds the floor, and is what catches the characteristic
failure — a green build, a sidecar generated from the wrong assembly, and a package that annotates nothing.

Verification opens the `.nupkg` a consumer would download rather than any proxy for it, and fails the stage
(non-zero exit) naming each sidecar that is `(missing)` or short.

## As commands

The three commands are values. Hand them to your own root command and you have the tool, without installing
it:
*)

let main argv =
    rootCommand argv {
        description "My build"
        addCommands [ generateCommand; verifyCommand; initCommand ]
    }

(**
That is, modulo the description, the entire `partas-annotations` executable.

To expose one operation under your own name and defaults, wrap the option-driven stage instead:
*)

let annotationsCommand =
    command "annotations" {
        description "Generates the external annotations sidecar"

        Command.pipeline { generateStage }
    }

(**
> `Partas.Build.ExternalAnnotations` is `[<AutoOpen>]` and exposes its own `Options` module (`strict`,
> `assembly`, `output`, `package`, `minMembers`, `directory`, `annotationsTool`, `force`). If your build CLI has
> a module by that name, open the namespace before defining yours, or qualify.

## Writing the targets file

`initIn` is the pipeline form of `init`. It writes `Directory.Build.targets` into the given directory and
refuses to overwrite an existing one without `--force`:
*)

let initPipeline = pipeline "init" { initIn "src/My.Lib" }

(**
Note the directory: MSBuild takes the first `Directory.Build.targets` it finds walking up, so writing it beside
the packable project scopes annotations to that project and leaves test and sample projects untouched.

`writeTargets` is the same thing without a stage around it — a path and an optional command to pin inside the
file:
*)

let writeItSomewhereElse () =
    writeTargets "build/Partas.ExternalAnnotations.targets" (Some "dotnet partas-annotations")

(**
Pinning the command means the committed artifact says what runs, rather than depending on an ambient MSBuild
property being set correctly on every machine.

## Packing a project you cannot commit into

`packArgs` builds the `-p:CustomAfterMicrosoftCommonTargets=` argument that injects the same MSBuild logic into
a single `dotnet pack`, with no file added to the project. The path must be absolute; `packArgs` makes it so:
*)

let packWithInjection =
    stage "pack" {
        run (Cmd.ofList "dotnet" ([ "pack"; "-c"; "Release" ] @ packArgs "build/Partas.ExternalAnnotations.targets"))
    }

(**
This is the exception, not the rule. A committed `Directory.Build.targets` is what makes *every* pack correct,
including the ones that never go through your pipeline.

## The generator directly

`Partas.ExternalAnnotations` has no dependency on Partas.Build, so it can be used from anywhere — an MSBuild
task, a script, a test:

```fsharp
open Partas.ExternalAnnotations

let result = generate "bin/Release/net8.0/My.Lib.dll" "obj/My.Lib.ExternalAnnotations.xml"
printfn $"%d{result.Members} members, %d{result.Sites} sites, %d{result.Types} types"
```

`GenerateResult` carries `Types`, `Sites`, `Members` and `Skipped`. `Skipped` pairs a type or member with the
reason nothing could be emitted for it; a non-empty list means those annotations are **absent from the
output**, which is exactly what `--strict` turns into a failure.

`generate` collects **every** attribute in the `JetBrains.Annotations` namespace — `NotNull`, `Pure`,
`ContractAnnotation`, `StringFormatMethod`, `LanguageInjection`, `PublicAPI` and the rest — on types, members,
parameters, returns and generic parameters. Nothing has to be declared on the calling side.

`generateWith` narrows that, and takes extra probe directories for an assembly whose references do not resolve
from its own folder plus its `deps.json` closure:

```fsharp
generateWith
    (AttributeFilter.Named [ "NotNullAttribute" ])
    [ "packages/JetBrains.Annotations/lib/netstandard2.0" ]
    assembly
    output
```

`AttributeFilter` is `JetBrains` (the default, the whole namespace), `Named` (simple names, whatever namespace
declares them) or `Where` (a `namespace -> name -> bool` predicate).

Both are ordinary functions and `ExternalAnnotationGenerator` sits under them if you want the counts, the
`XDocument`, or `PrintfMembers()` for a human-readable dump of every site found. It is `IDisposable` — it holds
a `MetadataLoadContext` and an open `PEReader` over the assembly.

## Recipes

Concrete end-to-end setups are on the [recipes page](external-annotations-recipes.html).
*)
