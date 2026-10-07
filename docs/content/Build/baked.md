---
title: Baked stages
description: Reuse common .NET build operations and inputs.
category: Build
order: 7
---

`Partas.Build.Baked` provides ready-made stages for restore, build, clean, pack, publishing, and tests.

```fsharp
open Partas.Build
open Partas.Build.Baked

let projects = [ "src/MyLibrary/MyLibrary.fsproj" ]

let root = Command.root {
    workingDir __SOURCE_DIRECTORY__
    command "build" {
        Command.pipeline {
            Stages.restore "MyLibrary.slnx"
            Stages.build projects
        }
    }
}
```

The stages register the inputs they need. Check `build --help` for the resulting flags.

## Reuse inputs

- `Baked.Dotnet.config`: build configuration.
- `Baked.NuGet.apiKey`: NuGet key, with an environment default.
- `Baked.Common.isCI`: CI detection and override.
- `Baked.SemVer.bump`: semantic version increment or explicit version.

Bind them with the same `input` block used for your own options.

## Choose a versioning workflow

The agnostic SemVer bump rewrites project `Version` and sets `AssemblyVersion` to the major version. It does not infer releases from commits.

For conventional commits and changelogs, use the [ShipIt bump](../extensions/shipit.md#use-the-shipit-backed-bump). It computes the release version and updates configured package versions together. Use one bump workflow per release.

Full signatures are in [Capabilities](CAPABILITIES.md#baked) and the [workflow reference](workflow-reference.md#baked-the-batteries).
