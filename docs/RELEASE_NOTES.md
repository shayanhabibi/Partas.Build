### 0.1.5

* Stage-level output sinks: `silentOutput`, `captureOutput`, `redirectOutput` and `outputTo` on both the stage
  and pipeline builders. A captured stage prints nothing and lifts what it held — stderr if the process wrote
  any, everything otherwise — into the error message when a step fails.
* `StageContext.writeLine ctx stream line` is the routable way for a step to emit output; `echo` now uses it.
* Error messages are percent-encoded into GitHub Actions annotations, so a multi-line failure survives one.
* A step that failed with something to say reported nothing: the runner matched its error with an inverted
  guard, printing only the empty ones. Fixed, and covered by a test that records the console.
* `Partas.Build.ExternalAnnotations` packs its MSBuild logic as `build/Partas.Build.ExternalAnnotations.targets`.
  NuGet only auto-imports `build/$(PackageId).targets`, so under its own name it imported nothing (NU5129).
* Both of a redirected child's streams are always drained, which removes a pipe-buffer deadlock.

### 0.1.4

* `bump` command: `dotnet run --project Build.fsproj -- bump <major|minor|patch|alpha|beta|rc|preview|SEMVER> -p <project>...`
  rewrites `<Version>` and `<AssemblyVersion>` in the target project files.
* Versions now live in the project files. Nothing on the pack path passes a version property, so a published
  package carries whatever the committed project file says. This file is a changelog only; no build step reads it.
* `Baked.fs`: ready-made inputs (`--configuration`, `--nuget-key`, `--project`, `--ci`, `--bump`), semver
  arithmetic under `Version`, and `IO.writeVersion`/`IO.bumpVersion`.

### 0.1.3

* Initial release.
