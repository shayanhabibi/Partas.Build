/// Repository-local tool registration. These operations execute only when explicitly invoked.
module internal Partas.Build.EasyBuild.ShipIt.ToolSetup

open System
open System.IO
open System.Text.Json
open Partas.Build

let rec repositoryRoot (directory: string) =
    let directory = Path.GetFullPath directory
    if not (Directory.Exists directory) then invalidArg "directory" "Working directory does not exist."
    let marker = Path.Combine(directory, ".git")
    if Directory.Exists marker || File.Exists marker then directory
    else
        let parent = Directory.GetParent directory
        if isNull parent then invalidArg "directory" "ShipIt setup requires a Git repository."
        repositoryRoot parent.FullName

/// Reject manifests that would make bare dotnet shipit select a different tool boundary.
let validateBoundary (directory: string) =
    let root = repositoryRoot directory
    let canonical = Path.Combine(root, ".config", "dotnet-tools.json")
    let rec inspect current =
        let alternate = Path.Combine(current, "dotnet-tools.json")
        let nested = Path.Combine(current, ".config", "dotnet-tools.json")
        if File.Exists alternate || (current <> root && File.Exists nested) then
            invalidOp $"Ambiguous tool manifest at {current}; ShipIt uses {canonical}. Remove competing manifests or invoke from the repository root."
        if current <> root then inspect (Directory.GetParent current).FullName
    inspect (Path.GetFullPath directory)
    root

let private registered manifest =
    use document = JsonDocument.Parse(File.ReadAllText manifest)
    let root = document.RootElement
    let tools = root.GetProperty "tools"
    if tools.ValueKind <> JsonValueKind.Object then invalidOp $"Invalid tool manifest: {manifest}"
    let mutable registration = Unchecked.defaultof<JsonElement>
    let present = tools.TryGetProperty("easybuild.shipit", &registration)
    if present then
        if String.IsNullOrWhiteSpace(registration.GetProperty("version").GetString()) then invalidOp "ShipIt registration has no version."
        let commands = registration.GetProperty "commands"
        if commands.ValueKind <> JsonValueKind.Array || not (commands.EnumerateArray() |> Seq.exists (fun item -> item.GetString() = "shipit")) then
            invalidOp "ShipIt tool registration must expose the shipit command."
    present

let runWith (execute: Cmd -> Async<int>) (directory: string) (version: string) = async {
    if String.IsNullOrWhiteSpace version then invalidArg "version" "Tool version must not be blank."
    let root = validateBoundary directory
    let manifest = Path.Combine(root, ".config", "dotnet-tools.json")
    let run command = async {
        let! code = execute command
        if code <> 0 then invalidOp $"ShipIt setup command failed with exit code {code}: {Cmd.toLogString command}"
    }
    if not (File.Exists manifest) then
        do! run (Cmd.ofList "dotnet" [ "new"; "tool-manifest"; "--output"; Path.GetDirectoryName manifest; "--no-update-check" ])
    if not (registered manifest) then
        do! run (Cmd.ofList "dotnet" [ "tool"; "install"; "EasyBuild.ShipIt"; "--version"; version; "--tool-manifest"; manifest ])
    do! run (Cmd.ofList "dotnet" [ "tool"; "restore"; "--tool-manifest"; manifest ])
}
