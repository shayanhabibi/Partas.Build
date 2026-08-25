### 0.1.4

* `bump` command: `dotnet run --project Build.fsproj -- bump <major|minor|patch|alpha|beta|rc|preview|SEMVER> -p <project>...`
  rewrites `<Version>` and `<AssemblyVersion>` in the target project files.
* Versions now live in the project files. Nothing on the pack path passes a version property, so a published
  package carries whatever the committed project file says. This file is a changelog only; no build step reads it.
* `Baked.fs`: ready-made inputs (`--configuration`, `--nuget-key`, `--project`, `--ci`, `--bump`), semver
  arithmetic under `Version`, and `IO.writeVersion`/`IO.bumpVersion`.

### 0.1.3

* Initial release.
