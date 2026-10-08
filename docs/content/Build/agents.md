---
title: Agents and JSON
description: Inspect a command and consume structured execution results.
category: Build
order: 8
---

Use the build's own command model to discover what it accepts and what it will run.

## Inspect before execution

```shell
dotnet run --project Build.fsproj -- build --help
dotnet run --project Build.fsproj -- build --schema --json
dotnet run --project Build.fsproj -- build --explain --json
```

`--help` describes registered inputs. `--schema --json` returns the command schema as compact JSON. `--explain --json` returns a compact, static execution plan without running steps or effectful conditions. Text `--explain` may evaluate conditions such as a Git branch check.

For people, `--schema` prints indented JSON unless agent detection enables JSON mode. Use `--schema --json false` to request indentation explicitly. Console JSON mode always emits compact output; `--report <file>` saves an indented run result.

## Consume a run result

```shell
dotnet run --project Build.fsproj -- build --json
```

A JSON run ends with one result document containing the outcome, exit code, reports, failures, and timings. Child processes may still print output before that document.

Detected AI environments default `--json` to true. Detection happens per invocation. `PARTAS_BUILD_DISABLE_AI=true` disables that automatic default; explicit `--json` still works. Use `--json false` to request text for an individual invocation.

## Handle exit codes

- `0`: success, including help and inspection.
- `1`: execution failed.
- `2`: invalid command or input; execution did not start.
- `130`: cancelled.

In an F# host, [invoke a reusable root](hosting.md) to receive a `RunResult` directly.

## Read these docs as Markdown

The site publishes [llms.txt](/Partas.Build/llms.txt), [llms-full.txt](/Partas.Build/llms-full.txt), and Markdown copies beside guide pages. Each HTML guide advertises its Markdown URL with an alternate link.

Copy the [agent instructions](https://shayanhabibi.github.io/Partas.Build/AGENTS-snippet.md) into a consumer repository to document how its build should be inspected.
