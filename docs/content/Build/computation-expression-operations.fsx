(**
---
title: Stage CE run overloads
category: Build
order: 4
---
*)

(*** hide ***)
// The sources are #load-ed rather than #r-ing a built DLL, for two reasons: the guide then type-checks against
// the code as written instead of against the last build, and nothing holds a file lock — a #r-ed assembly stays
// loaded for the lifetime of the site watcher (`dotnet run --project Build.fsproj -- docs --watch`), which on
// Windows makes rebuilding the library fail.
// Keep this list in the same order as the <Compile> items in Partas.Build.fsproj.
#r "nuget: FSharp.Control.AsyncSeq, 4.15.0"
#r "nuget: FsToolkit.ErrorHandling, 5.2.0"
#r "nuget: System.CommandLine, 2.0.11"
#r "nuget: Spectre.Console, 0.57.2"
#load "../../../src/Partas.Build.Cmd/Execution.fs"
#load "../../../src/Partas.Build.Cmd/Program.fs"
#load "../../../src/Partas.Build/System.CommandLine/Aliases.fs"
#load "../../../src/Partas.Build/System.CommandLine/Inputs.fs"
#load "../../../src/Partas.Build/Exceptions.fs"
#load "../../../src/Partas.Build/Output.fs"
#load "../../../src/Partas.Build/Environment.fs"
#load "../../../src/Partas.Build/Timing.fs"
#load "../../../src/Partas.Build/Producer.fs"
#load "../../../src/Partas.Build/Failures.fs"
#load "../../../src/Partas.Build/Conductors.fs"
#load "../../../src/Partas.Build/Conductors.Runners.fs"
#load "../../../src/Partas.Build/Process.fs"
#load "../../../src/Partas.Build/Operations.fs"
#load "../../../src/Partas.Build/Dependencies.fs"
#load "../../../src/Partas.Build/DependencyPlan.fs"
#load "../../../src/Partas.Build/ExecutionState.fs"
#load "../../../src/Partas.Build/Builders/StageSettings.fs"
#load "../../../src/Partas.Build/Builders/Stage.fs"
#load "../../../src/Partas.Build/Builders/Conditions.fs"
#load "../../../src/Partas.Build/Builders/PipelineSettings.fs"
#load "../../../src/Partas.Build/Builders/Pipeline.fs"
#load "../../../src/Partas.Build/Builders/Inputs.fs"
#load "../../../src/Partas.Build/Explain.fs"
#load "../../../src/Partas.Build/Summary.fs"
#load "../../../src/Partas.Build/Builders/Command.fs"
#load "../../../src/Partas.Build.Baked/Program.fs"
#load "../../../src/Partas.Build.Baked/Common.fs"
#load "../../../src/Partas.Build.Baked/NuGet.fs"
#load "../../../src/Partas.Build.Baked/Dotnet.fs"
#load "../../../src/Partas.Build.Baked/SemVer.fs"



open System.Threading
open Partas.Build
(**
# Computation Expression Operations

## Stage

> Unless stated otherwise, every example runs inside a `stage` computation.

`run` takes a step and has more overloads than any other operation, which is why it gets its own page.

*)
(*** hide ***)

let _ = stage "stage" {

(**
### `run`

> A returned `string`-like value runs as a command.
>
> A returned `int`-like value is an exit code.
>
> An overload that returns or runs a command usually takes an optional `?cancellationToken: CancellationToken`.

##### `buildStep: StageContext -> BuildStep`

##### `command: string -> ?cancellationToken: CancellationToken`
*)
    run "exe args --options"
    run "exe args --options" CancellationToken.None

(**
##### `exe: string -> args: string -> ?cancellationToken: CancellationToken`
*)

    run "exe" "args --options"
    run "exe" "args --options" CancellationToken.None
(**
##### `command: Cmd -> ?cancellationToken: CancellationToken`
*)
    run (cmd $"exe args --options")
    run (cmd $"exe args --options") CancellationToken.None

(**
##### `asyncExitCode: Async<int>`
*)
    run (async { return 0 })

(**
##### `asyncAction: Async<unit>`
*)
    run (async { do () })
(**
##### `exitCodeFn: StageContext -> int`
##### `exitCodeFn: StageContext -> Async<int>`
##### `exitCodeFn: StageContext -> Task<int>`
*)
    run (fun _ -> 1)
    run (fun _ -> async { return 0 })
    run (fun _ -> task { return 99 })
(**
##### `actionFn: StageContext -> unit`
##### `actionFn: StageContext -> Async<unit>`
##### `actionFn: StageContext -> Task<unit>`
*)
    run (fun _ -> ())
    run (fun _ -> async { do () })
    run (fun _ -> task { do () })

(**
##### `commandFn: StageContext -> string`
##### `commandFn: StageContext -> Async<string>`
##### `commandFn: StageContext -> Task<string>`
*)
    run (fun _ -> "dotnet build")
    run (fun _ -> async { return "dotnet build" })
    run (fun _ -> task { return "dotnet build" })
    // With CancellationToken
    run (fun _ -> "dotnet build") CancellationToken.None
    run (fun _ -> async { return "dotnet build" }) CancellationToken.None
    run (fun _ -> task { return "dotnet build" }) CancellationToken.None

(**
##### `commandMaybeFn: StageContext -> string option`
##### `commandMaybeFn: StageContext -> Async<string option>`
##### `commandMaybeFn: StageContext -> Task<string option>`
*)
    // todo - overloads without CancellationToken should not require explicit typing
    run (fun _ -> Some "dotnet build")
    run (fun _ -> async { return Option<string>.None })
    run (fun _ -> task { return Some "dotnet build" })
    // With CancellationToken
    run (fun _ -> Some "dotnet build") CancellationToken.None
    run (fun _ -> async { return Some "dotnet build" }) CancellationToken.None
    run (fun _ -> task { return Some "dotnet build" }) CancellationToken.None


(**
##### `resultFn: StageContext -> Result<unit, string>`
##### `resultFn: StageContext -> Async<Result<unit, string>>`
##### `resultFn: StageContext -> Task<Result<unit, string>>`
*)
    run (fun _ -> Error "some error")
    run (fun _ -> async { return Ok() })
    run (fun _ -> task { return Error "some error" })

(**
##### `cmdResultFn: StageContext -> Result<Cmd option, string>`
##### `cmdResultFn: StageContext -> Async<Result<Cmd option, string>>`
##### `cmdResultFn: StageContext -> Task<Result<Cmd option, string>>`
*)
    // todo - overloads without CancellationToken should not require explicit typing
    run (fun _ -> Ok (Some (cmd $"dotnet build")) : Result<Cmd option, string>)
    run (fun _ -> async { return Ok (Some (cmd $"dotnet build")) : Result<Cmd option, string> })
    // With CancellationToken
    run (fun _ -> Ok (Some (cmd $"dotnet build"))) CancellationToken.None
    run (fun _ -> async { return Ok (Some (cmd $"dotnet build")) }) CancellationToken.None

(**
##### `cmdResultFn: StageContext -> Result<Cmd, string>`
##### `cmdResultFn: StageContext -> Async<Result<Cmd, string>>`
*)
    // todo - overloads without CancellationToken should not require explicit typing
    run (fun _ -> Ok (cmd $"dotnet build"): Result<Cmd , string>)
    run (fun _ -> async { return Ok (cmd $"dotnet build"): Result<Cmd , string> })
    // With CancellationToken
    run (fun _ -> Ok (cmd $"dotnet build")) CancellationToken.None
    run (fun _ -> async { return Ok (cmd $"dotnet build") }) CancellationToken.None

(*** hide ***)
}

