module Partas.Build.Tests.ShipItTests

open Expecto
open System.IO
open Partas.Build
open Partas.Build.EasyBuild.ShipIt
open Partas.Build.Tests.Helpers
open Partas.Build.Internal

let private labels (stage: StageContext) = stage.Steps |> List.choose (function
    | Step.StepFn(ValueSome label, _) | Step.Operation(ValueSome label, _) -> Some label
    | _ -> None)

[<Tests>]
let tests = testList "shipit" [
    test "bump is local even with consumer Push mode" {
        let spec = Stages.bumpWith (InputSpec.ret { ReleaseOptions.defaults with Mode = Push })
        let resolved = spec.Read (parse spec.Inputs "")
        Expect.isTrue (labels resolved |> List.exists (fun label -> label.Contains "--mode local")) "local only"
        Expect.isFalse (spec.Inputs |> List.exists (fun i -> match i.Source with ParsedOption o -> o.Name = "--mode" | _ -> false)) "no mode input"
    }
    test "release input parsing and validation" {
        let spec = Inputs.release
        let defaults = spec.Read (parse spec.Inputs "")
        Expect.equal defaults ReleaseOptions.defaults "defaults"
        let resolved = spec.Read (parse spec.Inputs "--mode local --pre-release beta --skip-merge-commit --dry-run")
        Expect.equal resolved.Mode Local "mode"
        Expect.equal resolved.PreRelease (Some "beta") "prefix"
        Expect.isTrue resolved.SkipMergeCommit "filter"
        Expect.isTrue resolved.DryRun "preview"
        let command = command "release" { Command.pipeline { Stages.generate } }
        Expect.isGreaterThan (command.Parse("--mode unknown").Errors.Count) 0 "invalid mode"
    }
    test "ready command tree exposes setup init and bump without executing" {
        let tree = Command.root { addCommand Commands.shipit }
        for args in [ "bump --help"; "generate --schema"; "setup --explain"; "init project --help"; "github --token SECRET --explain" ] do
            let tokens = Cmd.ofString ("shipit " + args)
            Expect.equal (quietly (fun () -> (tree |> Command.invoke (tokens.Executable :: tokens.Arguments)).ExitCode)) 0 "discovery succeeds"
    }
    test "project setup preserves comments body and identity and is idempotent" {
        let directory = Directory.CreateTempSubdirectory "shipit-project"
        try
            let project = Path.Combine(directory.FullName, "My Library.fsproj")
            let changelog = Path.Combine(directory.FullName, "CHANGELOG.md")
            let xml = "<Project><PropertyGroup><Version>1.2.3</Version><AssemblyVersion>0.0.0.0</AssemblyVersion></PropertyGroup></Project>"
            File.WriteAllText(project, xml)
            File.WriteAllText(changelog, "---\r\n# retained comment\r\nlast_commit_released: abc\r\ncustom: {thing: yes}\r\n---\r\n# Changelog\r\nbody\r\n", System.Text.UTF8Encoding(true))
            ProjectSetup.configure changelog [ project; project ]
            let once = File.ReadAllBytes changelog
            ProjectSetup.configure changelog [ project ]
            Expect.sequenceEqual (File.ReadAllBytes changelog) once "idempotent bytes"
            let text = File.ReadAllText changelog
            Expect.stringContains text "# retained comment\r\n" "comment"
            Expect.stringContains text "custom: {thing: yes}" "unknown mapping"
            Expect.stringContains text "selector: /Project/PropertyGroup/Version" "selector"
            Expect.stringContains text "# Changelog\r\nbody\r\n" "body"
            Expect.equal (File.ReadAllText project) xml "project unchanged until bump"
            Expect.sequenceEqual once[..2] [| 239uy; 187uy; 191uy |] "BOM"
        finally directory.Delete true
    }
    test "project setup appends to existing updaters and validates all projects before writing" {
        let directory = Directory.CreateTempSubdirectory "shipit-project"
        try
            let project = Path.Combine(directory.FullName, "A.fsproj")
            let invalid = Path.Combine(directory.FullName, "Invalid.fsproj")
            let changelog = Path.Combine(directory.FullName, "CHANGELOG.md")
            let original = "---\nupdaters:\n  - command: echo existing\n# retained\nname: A\n---\nbody\n"
            File.WriteAllText(project, "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>")
            File.WriteAllText(invalid, "<Project><PropertyGroup Condition='x'><Version>1</Version></PropertyGroup></Project>")
            File.WriteAllText(changelog, original)
            Expect.throws (fun () -> ProjectSetup.configure changelog [ project; invalid ]) "conditional version"
            Expect.equal (File.ReadAllText changelog) original "no partial update"
            ProjectSetup.configure changelog [ project ]
            let once = File.ReadAllText changelog
            Expect.stringContains once "  - command: echo existing\n" "existing updater"
            Expect.stringContains once "# retained\nname: A\n" "neighboring key"
            ProjectSetup.configure changelog [ project ]
            Expect.equal (File.ReadAllText changelog) once "no duplicates"
        finally directory.Delete true
    }
    test "project setup rejects missing duplicate or computed versions" {
        let directory = Directory.CreateTempSubdirectory "shipit-project"
        try
            let project = Path.Combine(directory.FullName, "A.fsproj")
            let changelog = Path.Combine(directory.FullName, "CHANGELOG.md")
            File.WriteAllText(changelog, "---\nname: A\n---\nbody\n")
            for xml in [ "<Project/>"; "<Project><PropertyGroup><Version>$(V)</Version></PropertyGroup></Project>"
                         "<Project><PropertyGroup><Version>1</Version><Version>2</Version></PropertyGroup></Project>" ] do
                File.WriteAllText(project, xml)
                Expect.throws (fun () -> ProjectSetup.configure changelog [ project ]) "ambiguous version"
        finally directory.Delete true
    }
    test "tool setup preserves registration and restores explicitly" {
        let directory = Directory.CreateTempSubdirectory "shipit-setup"
        try
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".git")) |> ignore
            let config = Directory.CreateDirectory(Path.Combine(directory.FullName, ".config"))
            let manifest = Path.Combine(config.FullName, "dotnet-tools.json")
            File.WriteAllText(manifest, """{"version":1,"isRoot":true,"tools":{"easybuild.shipit":{"version":"3.0.0","commands":["shipit"]}}}""")
            let calls = ResizeArray<Cmd>()
            let execute operation = async { calls.Add operation; return 0 }
            ToolSetup.runWith execute directory.FullName "3.1.0" |> Async.RunSynchronously
            Expect.equal calls.Count 1 "restore only"
            Expect.equal calls[0].Arguments [ "tool"; "restore"; "--tool-manifest"; manifest ] "explicit manifest"
            Expect.stringContains (File.ReadAllText manifest) "3.0.0" "pinned registration retained"
        finally directory.Delete true
    }
    test "tool setup creates then installs then restores and repeats safely" {
        let directory = Directory.CreateTempSubdirectory "shipit-setup"
        try
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".git")) |> ignore
            let nested = Directory.CreateDirectory(Path.Combine(directory.FullName, "nested"))
            let manifest = Path.Combine(directory.FullName, ".config", "dotnet-tools.json")
            let calls = ResizeArray<Cmd>()
            let execute operation = async {
                calls.Add operation
                if operation.Arguments |> List.contains "tool-manifest" then
                    Directory.CreateDirectory(Path.GetDirectoryName manifest) |> ignore
                    File.WriteAllText(manifest, """{"version":1,"isRoot":true,"tools":{}}""")
                if operation.Arguments |> List.contains "install" then
                    File.WriteAllText(manifest, """{"version":1,"isRoot":true,"tools":{"easybuild.shipit":{"version":"3.1.0","commands":["shipit"]}}}""")
                return 0
            }
            ToolSetup.runWith execute nested.FullName "3.1.0" |> Async.RunSynchronously
            Expect.equal calls.Count 3 "create install restore"
            Expect.isTrue (calls[1].Arguments |> List.contains manifest) "repository manifest"
            ToolSetup.runWith execute nested.FullName "3.1.0" |> Async.RunSynchronously
            Expect.equal calls.Count 4 "second run only restores"
        finally directory.Delete true
    }
    test "malformed manifest fails before executing tools" {
        let directory = Directory.CreateTempSubdirectory "shipit-setup"
        try
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".git")) |> ignore
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".config")) |> ignore
            File.WriteAllText(Path.Combine(directory.FullName, ".config", "dotnet-tools.json"), "{invalid}")
            let mutable called = false
            let execute _ = async { called <- true; return 0 }
            Expect.throws (fun () -> ToolSetup.runWith execute directory.FullName "3.1.0" |> Async.RunSynchronously) "invalid manifest"
            Expect.isFalse called "no process"
        finally directory.Delete true
    }
    test "local generation retains argument boundaries" {
        let options = { ReleaseOptions.defaults with Mode = Local; PreRelease = Some "beta candidate" }
        let operation = Operations.generate options
        Expect.equal operation.Executable "dotnet" "launcher"
        Expect.sequenceEqual operation.Arguments
            [ "shipit"; "--allow-branch"; "main"; "--mode"; "local"; "--pre-release"; "beta candidate" ] "literal prefix"
    }
    test "GitHub token is secret" {
        let operation = Operations.github ReleaseOptions.defaults (Some "secret value")
        let index = operation.Arguments |> List.findIndex ((=) "secret value")
        Expect.isTrue (operation.Secrets.Contains index) "secret index"
        Expect.isFalse ((Cmd.toLogString operation).Contains "secret value") "masked label"
    }
    test "all release inputs reach upstream" {
        let operation = Operations.generate {
            ReleaseOptions.defaults with
                AllowedBranches = [ "main"; "release" ]; Mode = Push
                RemoteHostname = Some "github.com"; RemoteOwner = Some "owner name"; RemoteRepository = Some "repo"
                SkipInvalidCommit = true; SkipMergeCommit = true; DryRun = true
        }
        Expect.sequenceEqual operation.Arguments
            [ "shipit"; "--allow-branch"; "main"; "--allow-branch"; "release"; "--mode"; "push"
              "--remote-hostname"; "github.com"; "--remote-owner"; "owner name"; "--remote-repo"; "repo"
              "--skip-invalid-commit"; "--skip-merge-commit"; "--dry-run" ] "arguments"
    }
    test "initialization and informational operations" {
        Expect.equal (Operations.initChangelog (Some "docs/my changelog.md")).Arguments
            [ "shipit"; "init"; "changelog"; "docs/my changelog.md" ] "path"
        Expect.equal (Operations.initChangelog None).Arguments [ "shipit"; "init"; "changelog" ] "default"
        Expect.equal (Operations.initWorkflows ()).Arguments [ "shipit"; "init"; "workflows" ] "workflows"
        Expect.equal (Operations.initGithub true true).Arguments [ "shipit"; "init"; "github"; "--org"; "--dry-run" ] "settings"
        Expect.equal (Operations.version ()).Arguments [ "shipit"; "version" ] "version"
        Expect.equal (Operations.conventions ()).Arguments [ "shipit"; "conventions" ] "conventions"
    }
    test "invalid options fail before launching" {
        Expect.throws (fun () -> Operations.generate { ReleaseOptions.defaults with AllowedBranches = [] } |> ignore) "no branches"
        Expect.throws (fun () -> Operations.generate { ReleaseOptions.defaults with PreRelease = Some " " } |> ignore) "blank prefix"
    }
]
