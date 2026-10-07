module Partas.Build.Tests.ShipItTests

open Expecto
open System.IO
open Partas.Build
open Partas.Build.EasyBuild.ShipIt

[<Tests>]
let tests = testList "shipit" [
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
