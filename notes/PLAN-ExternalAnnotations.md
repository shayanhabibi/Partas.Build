# PLAN — External Annotations generator

Working document for splitting the ReSharper external-annotations generator into a reusable
library, a Partas.Build integration, and a dotnet tool. Root-level per this repo's convention
(`fsdocs` renders everything under `docs/`).

## What this is for

`Partas.Solid` carries ~596 `[<LanguageInjection>]` attributes on F# optional type extensions.
They do not reach consumers, because `JetBrains.Annotations.LanguageInjectionAttribute` is
declared `internal` in `Partas.Solid/AttributesAliasesExtensions.fs` — and, more generally,
because ReSharper's code-annotation support is C#-first and its handling of F# extension
members is idiosyncratic. **ReSharper External Annotations** sidestep both: an XML sidecar named
`<AssemblyName>.ExternalAnnotations.xml`, sitting next to the assembly, is read regardless of
what the assembly's own metadata says.

This was proven in Rider 2026.2 with the harness at
`../Partas.Solid/ExternalAnnotationsTest/` (see its `README.md`):

| Site | Result |
| --- | --- |
| ordinary static method, parameter-level (`c1` `c2` `c3`) | injects |
| mangled extension setter, **member**-level (`a1` `a2`) | **injects** |
| mangled extension setter, parameter-level (`a3`) | does not inject |

Member-level is what the generator emits for all 596 sites, both ctor overloads resolve, and
`Prefix`/`Suffix` named arguments are honoured.

## Known-good oracle — use this at every phase

```
assembly: C:\Users\shaya\RiderProjects\Partas.Solid\Partas.Solid\bin\Release\net6.0\Partas.Solid.dll
expected: types=1786 sites=596 members=596 skipped=0
compare : C:\Users\shaya\RiderProjects\Partas.Solid\ExternalAnnotationsTest\lib\Partas.Solid.ExternalAnnotations.xml
```

The comparison file contains one extra hand-added `set_innerText` probe at the bottom (marked by
a comment) which the generator does not and should not emit. Excluding it, output must be
**byte-identical**. Verified identical from both a net10 and a net8 host.

## Decisions (settled — do not relitigate)

1. **`init` is a developer command, not a CI stage.** It writes `Directory.Build.targets`, which
   is **committed and reviewed**. CI never runs it. Rejected: running `init` in CI, which makes
   package contents differ between local and CI with no visible signal.
2. **A1 — committed file only.** The pack stage is a plain `dotnet pack`; no
   `CustomAfterMicrosoftCommonTargets` flag in the normal path. The flag stays available as an
   opt-in (`packArgs`) for packing projects you cannot commit to. `verify` covers the
   "never ran `init`" hole.
3. **The hedge.** The targets file *generates* via the tool when it can resolve one, and falls
   back to packing an existing XML when it cannot. Generation-at-pack-time makes staleness
   structurally impossible and makes Rider's Pack button correct; the fallback means every phase
   before the tool exists still works.
4. **TFMs.** Library `netstandard2.0;net8.0`; stages project follows Partas.Build; tool `net8.0`
   (`PackAsTool` cannot target netstandard2.0 — `error NETSDK1054`).
5. **ns2.0 leg is kept**, at the cost of two conditioned polyfill packages, to keep the
   in-process MSBuild-task route open and to serve other library authors (e.g. Oxpecker.Solid).
6. **Path `ProjectReference`** from Partas.Solid's build CLI during development (it is
   developer-only and never packed). Move to the `local` feed once the shape settles.
   **All three projects version in lockstep.**
7. Skips **warn and continue** by default; `--strict` promotes them to failure. Use `--strict`
   in Partas.Solid's own pipeline, where 0 is the known-good number.
8. **Not** making `LanguageInjectionAttribute` public. Visibility is plausibly necessary but not
   sufficient for F# extension members, and it would solve only this repo's case.

## Evidence already established (do not re-derive)

- `System.Reflection.MetadataLoadContext` 10.0.11 ships `net10.0 net9.0 net8.0 net462 netstandard2.0`.
- The library source compiles unchanged for `netstandard2.0` given `System.Text.Json` (for
  `deps.json` probing) and `System.Reflection.Metadata` (for the attribute-blob decode); neither
  is in-box there. Both must be `Condition="'$(TargetFramework)' == 'netstandard2.0'"`.
- Running on a real net8.0.30 host against the net6.0 assembly gives byte-identical output. MLC
  does not care about the target's TFM.
- `TargetsForTfmSpecificContentInPackage` + `TfmSpecificPackageFile` resolves `$(TargetFramework)`
  correctly **per inner build**, so it works for multi-targeted projects. A plain `<None Pack="true"
  PackagePath="lib\$(TargetFramework)\">` does not — the outer build has an empty `$(TargetFramework)`.
- `Directory.Build.props` and `Directory.Build.targets` are independent auto-import hooks, so our
  file can go in `.targets` without touching a consumer-owned `.props`.
- Creating `Directory.Build.targets` **shadows any parent one** (MSBuild stops at the first found).
  The chain-import must use an intermediate property; inlining `GetPathOfFileAbove` in a
  `Condition` fails with `MSB4092: An unexpected token "Directory" was found`.
- Both injection routes active at once still produces a correct package, but emits 5x
  `MSB4011 ... will be ignored` — hence the sentinel property.
- Scratch harnesses live in this session's scratchpad: `packtest/` (single+multi-target pack),
  `dbtest/` (shadowing + chain import), `net8/`, `net8exe/`, `ns20/`, `nstool/`.

## Phases

Phases 0-2 need nothing from Partas.Build and are independently committable. Phase 4 is the only
one that cannot be validated without a feed.

### Phase 0 — rename

1. `Partas.Build.ExternalAnnotations/` -> `Partas.ExternalAnnotations/` (folder + `.fsproj`).
2. `Library.fs:1` — `module Partas.Build.ExternalAnnotations` -> `module Partas.ExternalAnnotations`.
3. `Partas.Build.slnx` — update the `<Project Path=...>` entry.
4. Delete the `ProjectReference` to `src/Partas.Build` (dead — `Library.fs` never used it).

**Check:** solution builds; regenerate and byte-compare against the oracle.

### Phase 1 — the library

1. Multi-target `netstandard2.0;net8.0`; add the two conditioned polyfills.
2. Sort emitted members by doc ID (stable, diffable output).
3. Add the facade both the stage and the tool call, so neither reimplements anything:
   ```fsharp
   type GenerateResult = { Types: int; Sites: int; Members: int; Skipped: (string * string) list }
   ExternalAnnotations.generate : assembly: string -> output: string -> GenerateResult
   ```
   Keep `ExternalAnnotationGenerator` (now `IDisposable`) as the lower-level entry point.

**Check:** both legs build clean; net8 leg reproduces the oracle byte-for-byte.

### Phase 2 — the `.targets` asset

One real file in the repo — **not** a string literal in F# — used as an `EmbeddedResource` (for
`init` to write out) and `Pack`ed to `build/` (for the future standalone-package route).

Contents:
- sentinel: `PartasExternalAnnotationsImported` guard, so double injection cannot warn;
- parent chain-import via an intermediate property (see the `MSB4092` note above);
- hedge: `Exec` the tool when `$(PartasExternalAnnotationsTool)` resolves, else pack an existing
  XML if one is present, else do nothing quietly.

**Check:** the `packtest/` harness — single- and multi-target, with and without a tool available.
Assert `lib/<tfm>/*.ExternalAnnotations.xml` lands, and that no `MSB4011` is emitted.

### Phase 3 — `Partas.Build.ExternalAnnotations`

References `Partas.Build` + `Partas.ExternalAnnotations`. Exposes:

```fsharp
ExternalAnnotations.generate   // stage: assembly -> xml (after build, before pack)
ExternalAnnotations.verify     // stage: open the .nupkg, assert lib/<tfm>/*.ExternalAnnotations.xml
ExternalAnnotations.packArgs   // string list, opt-in CustomAfterMicrosoftCommonTargets injection
ExternalAnnotations.init       // command: write Directory.Build.targets + tool manifest entry
```

`--strict` turns skips into failure.

**Check:** run the stages from a scratch pipeline; confirm `verify` **fails** on a deliberately
annotation-less package (test the negative, not just the positive).

### Phase 4 — the tool

`net8.0`, `PackAsTool`, a `rootCommand` over the Phase 3 stages — presentation only, no logic.
Expected to be comfortably under 100 LOC.

**Check:** `dotnet tool install --local` from the local feed, then drive it through the Phase 2
targets file's `Exec` path.

### Phase 5 — wire into Partas.Solid

1. Path `ProjectReference` from Partas.Solid's build CLI.
2. Run `init`; commit the emitted `Directory.Build.targets`.
3. Pack.

**Check (the one that matters):** unzip `Partas.Solid.<ver>.nupkg` and confirm
`lib/net6.0/Partas.Solid.ExternalAnnotations.xml` is present with 596 members. Then repoint
`ExternalAnnotationsTest` at the packed output instead of a hand-copied DLL, and re-run the
Rider check (`a1`/`a2` must still inject).

## Open, deliberately deferred

- Standalone `build/*.targets` NuGet route for third parties — the asset is packed to `build/`
  from Phase 2 so this is additive whenever it is wanted.
- In-process MSBuild task replacing the `Exec` — the reason the ns2.0 leg exists.
- A `d1` harness probe (public attribute, in-metadata, binary reference) to settle whether
  visibility alone would have worked. Does not affect this plan.
- Filtering the 298 `get_` entries; injection on a getter is inert.

## Status

- **Phase 0 — done.** Renamed; dead `Partas.Build` reference dropped; `slnx` updated. Regenerated
  output matches the oracle (`types=1786 sites=596 members=596 skipped=0`). The only diff against
  the committed reference is a trailing newline, which `XDocument.Save` does not emit.
- **Phase 1 — done.** `netstandard2.0;net8.0`, 0 warnings. Sorting was already present
  (`Array.sortBy fst`). Facade added: `GenerateResult` + `generateWith` + `generate`, the latter
  creating the output directory. Only `System.Text.Json` is needed on the ns2.0 leg —
  `System.Reflection.Metadata` arrives transitively via MetadataLoadContext, and pinning it
  explicitly causes `NU1605` downgrade warnings. Net8 leg reproduces the oracle.
- **Phase 2 — done.** `Partas.Build.ExternalAnnotations/assets/Partas.ExternalAnnotations.targets`.
  Verified in `scratchpad/p2/` on a `net8.0;net10.0` project:
  - existing XML, multi-target: both `lib/<tfm>/` get it, parent `Directory.Build.targets` still
    applies, 0 warnings;
  - nothing available: one diagnostic warning per TFM, package still builds;
  - committed file **and** `CustomAfterMicrosoftCommonTargets` both active: **0** `MSB4011`;
  - tool set: `Exec` runs per inner build with that TFM's `$(TargetPath)`;
  - tool missing/broken: `ContinueOnError` degrades to the fallback, 0 errors, package produced.

  Two design points found by testing, not by inspection:
  - The chain-import must be conditioned on `'$(MSBuildThisFileName)' == 'Directory.Build'`.
    Without it, the injected-by-path copy re-imports the parent and reintroduces `MSB4011`.
  - Generated output must default into `$(IntermediateOutputPath)` (per-TFM), not a project-level
    path. With a single path each inner build overwrites the last, and every `lib/<tfm>/` ships
    the same TFM's annotations. Defaults are assigned **inside** the targets because
    `IntermediateOutputPath` is not final at evaluation time.
- **Phase 3 — done.** `Partas.Build.ExternalAnnotations/Library.fs`: `generateTo`, `verifyPackage`,
  `packArgs`, `writeTargets`, `initIn`, `initCommand`. Builds clean on `net10.0;net8.0;netstandard2.0`.
  Verified from a consumer-shaped harness (`scratchpad/p3/Harness`, a `rootCommand` over the stages —
  which is also a rehearsal of Phase 4's surface):
  - `generateTo` against the oracle: `596 members, 596 sites, 1786 types`, 0 skipped;
  - `verifyPackage` **passes** on an annotation-bearing package and **fails with exit code 1** on an
    annotation-less one, naming both missing sidecars. The non-zero exit is the point: this is what
    breaks CI;
  - `init-annotations` writes the targets, pins `--annotations-tool` into it, refuses to clobber an
    existing file (exit 1), and overwrites under `--force`;
  - end-to-end: `init` with a tool pinned, then a plain `dotnet pack` of a `net8.0;net10.0` project
    produces `lib/<tfm>/Lib.ExternalAnnotations.xml` **each generated from that TFM's own assembly**,
    and `verifyPackage` passes on the result. No warnings.

  One fix along the way: `XDocument.Save(path)` emits a UTF-8 BOM, so output now goes through an
  `XmlWriter` with `UTF8Encoding false`. Against the oracle the file is now byte-identical except
  that XLinq writes `'` where the old fsx wrote `&apos;` (1221 lines). Both parse to the same string
  — `'` needs no escaping in element content — so the `Prefix`/`Suffix` values Rider sees are
  unchanged. Not worth post-processing XLinq to match.

  Not covered: `--strict`'s failure path, since the oracle has 0 skips and nothing to hand skips.
- **Phase 4 — done.** `Partas.ExternalAnnotations.Tool` (`net8.0`, `PackAsTool`, command name
  `partas-annotations`). `Program.fs` is 24 lines and contains no logic: it is a `rootCommand` over
  three commands exported by the stages library.

  Two shape changes this forced, both improvements:
  - **The commands live in `Partas.Build.ExternalAnnotations`, not in the tool.** `input` is
    applicative by design (no `Bind`), so a value read from an option cannot be fed to
    `generateTo assembly output` — that would need `InputSpec<InputSpec<_>>` and there is no
    flatten. So each operation now has two entry points over one shared implementation: an
    argument-taking stage for pipelines that know the paths (`generateTo`, `verifyPackage`,
    `initIn`) and an option-driven one for a CLI (`generateStage`, `verifyStage`, `initStage`),
    plus `generateCommand`/`verifyCommand`/`initCommand`. A build CLI can adopt any one of them
    without the tool, and the tool cannot drift from the library.
  - **`verify` gained `--min-members`** (`verifyPackageOf` for pipelines). Found by testing: the
    original check passed on a sidecar that existed but annotated nothing. That is right for an
    assembly with no annotations, and exactly the silent failure to catch for Partas.Solid, so the
    floor is opt-in and defaults to 0. Use `--min-members 596` in Partas.Solid's pipeline.

  Verified with the tool **packed and installed from a local feed** (`dotnet tool install --local`),
  not from the build output:
  - `generate` against the oracle: `596 members, 596 sites, 1786 types`, and the file is
    **byte-identical to the net10 harness output** — the net8 tool host changes nothing;
  - driven through the Phase 2 targets' `Exec` by a plain `dotnet pack` of a `net8.0;net10.0`
    project with `dotnet partas-annotations` pinned by `init`: each `lib/<tfm>/` gets a sidecar
    generated from that TFM's own assembly, 0 warnings;
  - `verify` matrix, exit codes checked (not just output): empty sidecar + default floor -> pass;
    empty + `--min-members 1` -> **exit 1**; 1 member + floor 1 -> pass; 1 member + floor 2 ->
    **exit 1**; no sidecars -> **exit 1**. Failure detail distinguishes `(missing)` from
    `(N members, expected at least M)`.
- **Phase 5 — done, with one deviation from the plan.** `Partas.Solid.2.1.2.nupkg` now ships
  `lib/net6.0/Partas.Solid.ExternalAnnotations.xml`, 237,594 bytes, **596 members**, and
  `verify --min-members 596` passes. The packed bytes are identical to the Rider-validated
  reference except for apostrophe escaping (see Phase 3).

  **Deviation: no `ProjectReference` from Partas.Solid's build CLI.** Decision 6 assumed one, but
  it does not work today and the tool route is better anyway:
  - `partas-solid.fsproj` carries `PackageReference Partas.Build 0.1.3`, while the project chain
    resolves Partas.Build 1.0.0 -> `NU1605` downgrade on both Partas.Build and FSharp.Core;
  - the CLI's working-tree `Build/Program.fs` uses `ActionPath`, which exists in **neither** 0.1.3
    nor local main, so it does not compile regardless (this is unrelated in-flight work of the
    repo owner's - with `HEAD`'s `Build/` the CLI builds fine, and nothing here touched it).

  Instead: `Directory.Build.targets` in **`Partas.Solid/`, not the repo root**. MSBuild takes the
  first one found walking up, so this scopes annotations to the shipped package and leaves the
  plugin, test and scratch projects untouched.

  **The tool is deliberately not pinned in the manifest.** It is unpublished, so a manifest entry
  would break `dotnet tool restore` for the repo and CI. The committed
  `Partas.Solid/ExternalAnnotations/Partas.Solid.ExternalAnnotations.xml` (decision 3's hedge)
  makes packs correct today with no tool at all and no warnings. Once the tool is published:
  `dotnet tool install Partas.ExternalAnnotations.Tool`, re-run `init --directory Partas.Solid
  --annotations-tool "dotnet partas-annotations" --force`, and generation takes over.

  **Defect found by testing, and fixed.** The first Partas.Solid pack without a tool appeared to
  use the committed fallback but was actually shipping a **stale `obj/` file** from the previous
  successful pack: `PartasExternalAnnotationsFile` was set to the obj path before the `Exec`, so it
  stayed set whether or not generation succeeded, and the fallback was unreachable. A failed
  generation would silently ship the last good build's annotations. The targets now capture the
  `Exec` `ExitCode`, delete the previous output first (only for our own obj path - never a
  caller-supplied one), and use the generated file **only on exit code 0**. Re-verified three ways
  on a `net8.0;net10.0` project with a committed fallback present: working tool -> generated file
  wins; broken tool with a stale obj file present -> **committed** file ships, not the stale one;
  no tool -> committed file ships. 0 warnings in all three.

## Phase 6 - whole-namespace default

The generator collected one attribute by simple name, defaulting to `LanguageInjectionAttribute`,
so every consumer had to know which attribute it wanted and a second annotation kind meant a second
run. It now collects **every attribute in the `JetBrains.Annotations` namespace**, and narrowing is
opt-in.

- `AttributeFilter` = `JetBrains` (default) | `Named of string list` | `Where of (ns -> name -> bool)`.
  `AttributeFilter.predicate` flattens it into the one predicate both matching paths use - the
  reflected one over `CustomAttributeData` and the raw-blob one - because a hit is located in the
  blob by its index among the matches at that site, so disagreeing predicates would read another
  attribute's named arguments. `blobCtorTypeName` therefore had to start returning the namespace as
  well as the simple name.
- `generateWith` takes an `AttributeFilter` instead of a name; `generate` is unchanged in shape.
- CLI/stages: `--attribute NAME` (repeatable, `ZeroOrMore`, multiple per token) on `generate`;
  empty means the whole namespace. `generateOnlyTo` fixes the set from a pipeline instead.

**Checked.** Against the oracle assembly, `AttributeFilter.Named ["LanguageInjectionAttribute"]` and
`AttributeFilter.JetBrains` produce **byte-identical** files - 596 members, 596 sites, 0 skipped -
so the widened default is a no-op there and the blob fallback still resolves `Prefix`/`Suffix`.
A purpose-built mixed assembly (`NotNull`, `Pure`, `CanBeNull`, `ContractAnnotation`,
`StringFormatMethod`, `PublicAPI`, `LanguageInjection` across type, member, parameter and return
sites) yields 6 members / 8 sites with named arguments intact and `[<Obsolete>]` correctly excluded;
`--attribute NotNullAttribute PureAttribute` narrows it to 2 members / 3 sites, and the spaced and
repeated forms of the flag agree byte-for-byte.

**Found by testing.** The `JetBrains.Annotations` NuGet package's attributes are
`[<Conditional("JETBRAINS_ANNOTATIONS")>]`. Without that constant defined in the annotated project
they never reach metadata, and the generator finds nothing - the first run of the mixed harness
returned 0 members for that reason alone. Partas.Solid is unaffected because it declares its own
copies. Documented under Known limits.

### Site grouping, and the format checked against ReSharper's own files

Widening the default made a second attribute on one site reachable for the first time, and the
emitter produced **one `<parameter name="x">` element per attribute** instead of one element with
two `<attribute>` children. Same latent bug on `<return>`.

Checked against Rider 2026.2's shipped annotations - 793 files, 251,811 `<member>` elements at
`Rider/r2r/<build>/ExternalAnnotations` - rather than against the written guide:

- element vocabulary is exactly `assembly, member, parameter, return, typeparameter, attribute,
  argument, property`. We emit all but `typeparameter` (see Remaining).
- duplicate `<parameter name>` within a member occurs in **1147 of 251,811** members, in precisely
  the shape we were emitting, so it is tolerated rather than invalid. Grouping is nonetheless the
  convention at 99.5%, and it also rules out the multiple-`<return>` case, which occurs **0** times
  in their corpus. Now grouped by `SiteKey` (`Member | Return | Parameter of name`), with
  `Array.groupBy` preserving first-appearance order so member-level attributes still precede
  parameters. Both `('attribute','parameter')` and `('parameter','attribute')` orderings appear in
  their files, so that order is not significant.
- enum constructor arguments: we emit the **member name** (`Assign`), they emit both that (1200
  occurrences) and the numeric value with a trailing comment. Flag combinations with no single
  matching field fall back to the number, matching their `5` form. No change needed.

Grouping left the oracle byte-identical (596 members, all member-level, 0 parameters). A
parameter-focused harness - ordinary static method, constructor, indexer, and the mangled F#
optional-extension setter - emits 5 members / 8 sites, and all three outputs pass a structural
validator built from the corpus vocabulary.

**Parameter sites were never suppressed** and are not now: the Rider harness result (`a3` does not
inject) is a consuming-side limit, not a reason to withhold the annotation.

### `<typeparameter>` - the last vocabulary gap

Attributes on generic parameters are now collected, on types (`T:` members, 26 in ReSharper's
corpus) and on methods (`M:`, 745). `Site` gained `OnTypeParameter of Type`, `SiteKey` gained
`TypeParameter of name`, and `Hit.siteToken` returns the parameter's own token so the blob fallback
still works for it. That closes the element vocabulary: we now emit all eight of
`assembly, member, parameter, return, typeparameter, attribute, argument, property`.

**Found by testing, and the first fix was wrong.** A nested generic type **redeclares** every
enclosing type's generic parameters as its own in IL, with their custom attributes copied onto the
redeclaration. So `DeclaringType` is the *nested* type for both, and the obvious filter
(`tp.DeclaringType = ty`) does nothing - `Outer<T>.Inner<U>` emitted `T` as well as `U`, repeating
the outer type's annotation on every nested type. Only **position** distinguishes them: the first
`arity(DeclaringType)` parameters are inherited. Filtering on `GenericParameterPosition` took the
harness from 7 sites to the correct 6. Methods need no such filter - `GetGenericArguments` on a
method reports only the method's own.

F# cannot express the nested case (`FS0058: Nested type definitions are not allowed`), so that
harness is C#; the generator reads any .NET assembly, and it is the only shape that exposes the bug.

**Checked.** Type-level, method-level, method-level alongside a parameter annotation, two generic
parameters with one annotated, non-generic method contributing nothing, and the nested case. The
oracle stays byte-identical, and the earlier mixed and parameter harnesses are unchanged
byte-for-byte, so the new site kind costs nothing where it does not apply.

## Remaining

1. Publish `Partas.ExternalAnnotations`, `Partas.Build.ExternalAnnotations` and
   `Partas.ExternalAnnotations.Tool` (lockstep versions), then pin the tool in Partas.Solid.
2. Re-run the Rider check against the packed output (`ExternalAnnotationsTest`) - mechanical
   equivalence is proven, but only Rider can confirm `a1`/`a2` still inject.
3. Nothing is committed in either repo.
