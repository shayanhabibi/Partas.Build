module Partas.Build.Tests.Helpers

open System
open System.CommandLine
open System.IO
open System.Threading
open Partas.Build
open Partas.Build.Internal

/// <summary>The deterministic console child under <c>tests/Fixtures/ProcessFixture</c>.</summary>
/// <remarks>
/// The fixture is referenced with <c>ReferenceOutputAssembly="false"</c>, so the built dll stays in its own
/// project directory and is run through <c>dotnet</c>. It is built in the configuration this assembly was
/// built in, which is the one named two directories above <c>AppContext.BaseDirectory</c>.
/// </remarks>
module ProcessFixture =
    let private repositoryRoot =
        let rec walk (directory: DirectoryInfo) =
            if isNull (box directory) then failwith "the repository root carrying Partas.Build.slnx is above no ancestor of the test assembly"
            elif File.Exists (Path.Combine (directory.FullName, "Partas.Build.slnx")) then directory.FullName
            else walk directory.Parent
        walk (DirectoryInfo AppContext.BaseDirectory)

    /// The built fixture assembly.
    let assemblyPath =
        let binary = Path.Combine (repositoryRoot, "tests", "Fixtures", "ProcessFixture", "bin")
        let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
        let expected = Path.Combine (binary, configuration, "net10.0", "ProcessFixture.dll")
        if File.Exists expected then expected
        else
            match Directory.GetFiles (binary, "ProcessFixture.dll", SearchOption.AllDirectories) with
            | [||] -> failwith $"the process fixture is not built; expected {expected}"
            | found -> found |> Array.maxBy File.GetLastWriteTimeUtc

    /// A command that runs the fixture in the given mode.
    let command (arguments: string list) = Cmd.ofList "dotnet" (assemblyPath :: arguments)

/// Registers a spec's inputs on a throwaway command and parses <c>commandLine</c> against it.
/// Options are registered here and nowhere else — a test that reads a value it never declared
/// gets the CLR default rather than a parse error, so assertions read real parsed values only
/// when the input reached this function through <c>InputSpec.Inputs</c>.
let parse (inputs: ActionInput list) (commandLine: string) =
    let root = RootCommand "test"

    for input in inputs do
        match input.Source with
        | ParsedOption option -> root.Options.Add option
        | ParsedArgument argument -> root.Arguments.Add argument
        | _ -> ()

    root.Parse commandLine

/// Runs a stage as the first stage of no pipeline and reports its outcome. <c>Error</c> carries the
/// exceptions the stage's steps raised, so a step that returned <c>Error</c> rather than raising gives
/// an empty list.
let runStage (stage: StageContext) : Result<unit, exn list> =
    match StageContext.run stage (StageIndex.Stage 0) CancellationToken.None with
    | true, _ -> Ok ()
    | false, exns -> Error (List.ofSeq exns)

/// The option names a spec declares, in declaration order.
let inputNames (inputs: ActionInput list) = [
    for input in inputs do
        match input.Source with
        | ParsedOption option -> option.Name
        | ParsedArgument argument -> argument.Name
        | _ -> "?"
]
