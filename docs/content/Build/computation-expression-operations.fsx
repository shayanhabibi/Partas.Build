(**
---
title: Stage CE run overloads
category: Build
order: 4
---
*)

(*** hide ***)
// The sources are #load-ed rather than #r-ing a built DLL, for two reasons: the guide then type-checks against
// the code as written instead of against the last build, and nothing holds a file lock — `fsdocs watch --eval`
// keeps a loaded assembly open for its whole lifetime, which on Windows makes rebuilding the library fail.
// Keep this list in the same order as the <Compile> items in Partas.Build.fsproj.
#r "nuget: FSharp.Control.AsyncSeq, 4.15.0"
#r "nuget: FsToolkit.ErrorHandling, 5.2.0"
#r "nuget: System.CommandLine, 2.0.11"
#r "nuget: Spectre.Console, 0.57.2"

#load "../../../src/Partas.Build/System.CommandLine/Aliases.fs"
#load "../../../src/Partas.Build/System.CommandLine/Inputs.fs"
#load "../../../src/Partas.Build/Types.fs"
#load "../../../src/Partas.Build/Process.fs"
#load "../../../src/Partas.Build/Builders/Stage.fs"
#load "../../../src/Partas.Build/Builders/Conditions.fs"
#load "../../../src/Partas.Build/Builders/Pipeline.fs"
#load "../../../src/Partas.Build/Builders/Inputs.fs"
#load "../../../src/Partas.Build/Explain.fs"
#load "../../../src/Partas.Build/Summary.fs"
#load "../../../src/Partas.Build/Builders/Command.fs"
#load "../../../src/Partas.Build/Baked.fs"

open System.Threading
open Partas.Build
open Partas.Build.Internal
(**
# Computation Expression Operations

## Stage

> Unless stated otherwise all examples are within a `stage` computation.

The computation expression operation for a step has a variety of overloads.
As the most overloaded operation, it is the only one that really requires separate
documentation.

*)
(*** hide ***)

let _ = stage "stage" {

(**
### `run`

> When returning a `string`-like value, the value is run as a command.
>
> When returning an `int`-like value, the value is treated as an exit code.
>
> When returning, or running a command, the overload would usually have a `?cancellationToken: CancellationToken` parameter.

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

