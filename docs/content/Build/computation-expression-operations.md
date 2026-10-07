---
title: Stage CE run overloads
category: Build
order: 4
---

# Computation Expression Operations

## Stage

> Unless stated otherwise, every example runs inside a `stage` computation.

`run` takes a step and has more overloads than any other operation, which is why it gets its own page.

### `run`

> A returned `string`-like value runs as a command.
>
> A returned `int`-like value is an exit code.
>
> An overload that returns or runs a command usually takes an optional `?cancellationToken: CancellationToken`.

##### `buildStep: StageContext -> BuildStep`

##### `command: string -> ?cancellationToken: CancellationToken`

```fsharp
run "exe args --options"
    run "exe args --options" CancellationToken.None
```

##### `exe: string -> args: string -> ?cancellationToken: CancellationToken`

```fsharp
run "exe" "args --options"
    run "exe" "args --options" CancellationToken.None
```

##### `command: Cmd -> ?cancellationToken: CancellationToken`

```fsharp
run (cmd $"exe args --options")
    run (cmd $"exe args --options") CancellationToken.None
```

##### `asyncExitCode: Async<int>`

```fsharp
run (async { return 0 })
```

##### `asyncAction: Async<unit>`

```fsharp
run (async { do () })
```

##### `exitCodeFn: StageContext -> int`
##### `exitCodeFn: StageContext -> Async<int>`
##### `exitCodeFn: StageContext -> Task<int>`

```fsharp
run (fun _ -> 1)
    run (fun _ -> async { return 0 })
    run (fun _ -> task { return 99 })
```

##### `actionFn: StageContext -> unit`
##### `actionFn: StageContext -> Async<unit>`
##### `actionFn: StageContext -> Task<unit>`

```fsharp
run (fun _ -> ())
    run (fun _ -> async { do () })
    run (fun _ -> task { do () })
```

#### `runLine`

A function returning a command line is `runLine`, not `run`. `run (fun ctx -> "...")` still compiles, marked
obsolete: a lambda written to return a message would otherwise start a process. The line is split on whitespace,
honouring quotes; build a `Cmd` with `cmd $"..."` to keep each interpolation hole as one argument.

##### `commandFn: StageContext -> string`
##### `commandFn: StageContext -> Async<string>`
##### `commandFn: StageContext -> Task<string>`

```fsharp
runLine (fun _ -> "dotnet build")
    runLine (fun _ -> async { return "dotnet build" })
    runLine (fun _ -> task { return "dotnet build" })
    // With CancellationToken
    runLine (fun _ -> "dotnet build") CancellationToken.None
    runLine (fun _ -> async { return "dotnet build" }) CancellationToken.None
    runLine (fun _ -> task { return "dotnet build" }) CancellationToken.None
```

##### `commandMaybeFn: StageContext -> string option`
##### `commandMaybeFn: StageContext -> Async<string option>`
##### `commandMaybeFn: StageContext -> Task<string option>`

```fsharp
runLine (fun _ -> Some "dotnet build")
    runLine (fun _ -> async { return Option<string>.None })
    runLine (fun _ -> task { return Some "dotnet build" })
    // With CancellationToken
    runLine (fun _ -> Some "dotnet build") CancellationToken.None
    runLine (fun _ -> async { return Some "dotnet build" }) CancellationToken.None
    runLine (fun _ -> task { return Some "dotnet build" }) CancellationToken.None
```

##### `resultFn: StageContext -> Result<unit, string>`
##### `resultFn: StageContext -> Async<Result<unit, string>>`
##### `resultFn: StageContext -> Task<Result<unit, string>>`

```fsharp
run (fun _ -> Error "some error")
    run (fun _ -> async { return Ok() })
    run (fun _ -> task { return Error "some error" })
```

##### `cmdResultFn: StageContext -> Result<Cmd option, string>`
##### `cmdResultFn: StageContext -> Async<Result<Cmd option, string>>`
##### `cmdResultFn: StageContext -> Task<Result<Cmd option, string>>`

```fsharp
// todo - overloads without CancellationToken should not require explicit typing
    run (fun _ -> Ok (Some (cmd $"dotnet build")) : Result<Cmd option, string>)
    run (fun _ -> async { return Ok (Some (cmd $"dotnet build")) : Result<Cmd option, string> })
    // With CancellationToken
    run (fun _ -> Ok (Some (cmd $"dotnet build"))) CancellationToken.None
    run (fun _ -> async { return Ok (Some (cmd $"dotnet build")) }) CancellationToken.None
```

##### `cmdResultFn: StageContext -> Result<Cmd, string>`
##### `cmdResultFn: StageContext -> Async<Result<Cmd, string>>`
