---
title: Installation
description: Add Partas.Build to a project or an F# script.
category: Build
order: 1
---

Packages are published to the [Partas.Build Cloudsmith feed](https://cloudsmith.io/~shayanhabibi/repos/shayanhabibi-partas-build/packages/).

## Build project

Add the source, then the packages you need:

```shell
dotnet nuget add source https://nuget.cloudsmith.io/shayanhabibi/shayanhabibi-partas-build/v3/index.json --name partas-build
dotnet add Build.fsproj package Partas.Build
dotnet add Build.fsproj package Partas.Build.Baked
```

`Partas.Build` is the DSL and execution engine. `Partas.Build.Baked` supplies common build inputs and stages. The [ShipIt extension](../extensions/shipit.md) is optional.

## F# script

Reference the feed explicitly:

```fsharp
#i "nuget: https://nuget.cloudsmith.io/shayanhabibi/shayanhabibi-partas-build/v3/index.json"
#r "nuget: Partas.Build, 0.8.0"
#r "nuget: Partas.Build.Baked, 0.2.0"

open Partas.Build
```

Pin versions for repeatable builds. Add `Partas.Build.EasyBuild.ShipIt, 0.1.0` if you need release workflows.

## Frameworks

The build packages target `net10.0`, `net8.0`, and `netstandard2.0`. Use an SDK compatible with your build project's target framework. External tools have their own runtime requirements; the default ShipIt tool requires .NET 10.

Continue with [Getting started](getting-started.md).
