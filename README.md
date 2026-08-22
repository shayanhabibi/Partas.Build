# Partas.Build

## Build CLI

Every repository task runs through the `Build` project rather than a script, so
the tasks are typed, debuggable, and discoverable:

```shell
dotnet run --project Build.fsproj -- --help
```

| Command | What it does |
|---------|--------------|
| `build` | Restores and builds the solution |
| `test` | Builds and runs the Expecto suite |
| `publish` | Packs and pushes to NuGet (`--nuget-key`; falls back to the `local` feed) |
| `docs` | Builds the fsdocs site (`--watch` to serve it) |

Flags belong to the commands whose stages read them: `--quick` skips restores
and the clean, `--skip-tests` skips the suite, `--configuration` picks the
configuration. None of them is registered by hand — see *Adding a step*.

## Layout

```
Build.fsproj              the build CLI
Build/
  TargetOperators.fs      list-taking FAKE target operators
  Spec.fs                 typed repository paths and the CLI options
  Program.fs              the stages and the commands
src/Partas.Build/      the library
tests/Partas.Build.Tests/  the Expecto suite
```

### Adding a project

`Spec.fs` addresses the repository through `EasyBuild.FileSystemProvider`, so
paths are checked when the build project compiles. After adding a project,
register it in `Spec.fs`:

```fsharp
module Projects =
    module Directory =
        type Solution = Root.src.``Partas.Build``
        type NewThing = Root.src.``Partas.NewThing``
```

A typo, or a project renamed without updating the build, then fails at compile
time rather than halfway through a release.

### Adding a step

A step is a stage of a pipeline. A stage that needs a flag binds it in an
`inputs { }` block, which is also what makes the flag appear in `--help`:

```fsharp
let myStep = inputs {
    let! quick = Options.quick

    return stage "my step" {
        when' (not quick)
        run (cmd $"dotnet ... {Projects.FsProj.Solution}")
    }
}
```

Add it to any command's `pipeline { }`. The condition stays in the stage, so the
command carries no flags of its own, and adding the stage to a second command
registers `--quick` there too.
