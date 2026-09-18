module Partas.Build.Cmd.NetStandard.Tests.NetStandardCmdTests

open System
open System.IO
open Expecto
open Partas.Build

/// <summary>The deterministic console child under <c>tests/Fixtures/ProcessFixture</c>.</summary>
/// <remarks>
/// Resolved the same way as in <c>tests/Partas.Build.Tests</c>: the fixture is built in this assembly's own
/// configuration, and is run from its own output directory rather than copied here.
/// </remarks>
module private ProcessFixture =
    let private repositoryRoot =
        let rec walk (directory: DirectoryInfo) =
            if isNull (box directory) then failwith "the repository root carrying Partas.Build.slnx is above no ancestor of the test assembly"
            elif File.Exists (Path.Combine (directory.FullName, "Partas.Build.slnx")) then directory.FullName
            else walk directory.Parent
        walk (DirectoryInfo AppContext.BaseDirectory)

    let assemblyPath =
        let binary = Path.Combine (repositoryRoot, "tests", "Fixtures", "ProcessFixture", "bin")
        let configuration = DirectoryInfo(AppContext.BaseDirectory).Parent.Name
        let expected = Path.Combine (binary, configuration, "net10.0", "ProcessFixture.dll")
        if File.Exists expected then expected
        else
            match Directory.GetFiles (binary, "ProcessFixture.dll", SearchOption.AllDirectories) with
            | [||] -> failwith $"the process fixture is not built; expected {expected}"
            | found -> found |> Array.maxBy File.GetLastWriteTimeUtc

    let command (arguments: string list) = Cmd.ofList "dotnet" (assemblyPath :: arguments)

[<Tests>]
let tests =
    testList "cmd netstandard2.0" [
        test "the netstandard build reaches the process through Arguments, not ArgumentList" {
            let startInfo = Cmd.toStartInfo ValueNone Map.empty (Cmd.ofList "tool" [ "plain" ])
            Expect.isEmpty startInfo.ArgumentList "netstandard2.0 has no ArgumentList to fill"
            Expect.isNotEmpty startInfo.Arguments "the arguments should travel in the Arguments string"
        }

        test "the netstandard build quotes an argument that contains whitespace or a quote" {
            let startInfo = Cmd.toStartInfo ValueNone Map.empty (Cmd.ofList "tool" [ "a b"; "plain"; "say \"hi\"" ])
            Expect.equal (startInfo.Arguments.Trim()) "\"a b\" plain \"say \\\"hi\\\"\"" "each argument should survive the round trip through one Arguments string"
        }

        test "the netstandard build delivers an awkward argument to a real child intact" {
            let awkward = [ "a b"; "plain"; "say \"hi\""; "trailing\\"; "back\\\\slash"; "" ]

            let result =
                Cmd.run ValueNone Map.empty (ProcessFixture.command ("args" :: awkward))
                |> Async.AwaitTask
                |> Async.RunSynchronously

            // The child prefixes each argument with its length, so a re-split argument shows up as an extra
            // line and a mangled one as a different length.
            let delivered = [ for line in result.output -> line.Substring (line.IndexOf ':' + 1) ]

            Expect.equal result.exitCode 0 "the child should accept its arguments"
            Expect.equal delivered awkward "the Arguments string should deliver the boundaries ArgumentList does"
        }
    ]
