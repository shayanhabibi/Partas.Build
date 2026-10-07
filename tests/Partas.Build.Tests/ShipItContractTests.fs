/// Opt-in real-tool contract checks. Requires SHIPIT_TEST_TOOL pointing to a preinstalled ShipIt 3.1.0 executable.
module Partas.Build.Tests.ShipItContractTests

open System
open System.IO
open System.Diagnostics
open Expecto
open Partas.Build
open Partas.Build.EasyBuild.ShipIt

let private execute directory executable arguments =
    let start = ProcessStartInfo(executable)
    start.WorkingDirectory <- directory
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    for argument in arguments do start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEndAsync()
    let error = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit 30000) then
        child.Kill true
        failwith "Contract process timed out."
    let text = output.Result + error.Result
    if child.ExitCode <> 0 then failwith $"{executable} failed ({child.ExitCode}): {text}"
    text

[<Tests>]
let tests = testList "shipit contract" [
    test "real tool local bump updates project and changelog together" {
        let tool = Environment.GetEnvironmentVariable "SHIPIT_TEST_TOOL"
        if String.IsNullOrWhiteSpace tool then skiptest "Set SHIPIT_TEST_TOOL to a preinstalled ShipIt 3.1.0 executable."
        let directory = Directory.CreateTempSubdirectory "shipit-contract"
        try
            let root = directory.FullName
            let git args = execute root "git" args
            git [ "init"; "-b"; "main" ] |> ignore
            git [ "config"; "user.email"; "shipit-test@example.test" ] |> ignore
            git [ "config"; "user.name"; "ShipIt contract" ] |> ignore
            git [ "remote"; "add"; "origin"; "https://github.com/example/fixture.git" ] |> ignore
            let project = Path.Combine(root, "Library.fsproj")
            let changelog = Path.Combine(root, "CHANGELOG.md")
            File.WriteAllText(project, "<Project><PropertyGroup><Version>1.2.3</Version><AssemblyVersion>0.0.0.0</AssemblyVersion></PropertyGroup></Project>")
            File.WriteAllText(changelog, "---\nname: fixture\n---\n# Changelog\n\n## 1.2.3 - 2026-01-01\n\nBaseline.\n")
            ProjectSetup.configure changelog [ project ]
            git [ "add"; "." ] |> ignore
            git [ "commit"; "-m"; "chore: baseline" ] |> ignore
            let baseline = (git [ "rev-parse"; "HEAD" ]).Trim()
            File.WriteAllText(changelog, (File.ReadAllText changelog).Replace("name: fixture", $"name: fixture\nlast_commit_released: {baseline}"))
            File.WriteAllText(Path.Combine(root, "feature.txt"), "feature")
            git [ "add"; "." ] |> ignore
            git [ "commit"; "-m"; "feat: add workflow" ] |> ignore
            let command = Operations.generate { ReleaseOptions.defaults with Mode = Local; AllowedBranches = [ "main"; "release" ] }
            execute root tool (command.Arguments |> List.tail) |> ignore
            Expect.stringContains (File.ReadAllText project) "<Version>1.3.0</Version>" "calculated minor version"
            Expect.stringContains (File.ReadAllText changelog) "## 1.3.0" "same changelog version"
            Expect.stringContains (File.ReadAllText project) "<AssemblyVersion>0.0.0.0</AssemblyVersion>" "assembly compatibility retained"
            Expect.equal ((git [ "log"; "-1"; "--format=%s" ]).Trim()) "feat: add workflow" "local mode never commits"
        finally
            for file in Directory.GetFiles(directory.FullName, "*", SearchOption.AllDirectories) do
                File.SetAttributes(file, FileAttributes.Normal)
            directory.Delete true
    }
]
