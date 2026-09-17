module Partas.Build.Cmd.NetStandard.Tests.NetStandardCmdTests

open Expecto
open Partas.Build

[<Tests>]
let tests =
    testList "cmd netstandard2.0" [
        test "the netstandard build reaches the process through Arguments, not ArgumentList" {
            let startInfo = Cmd.toStartInfo ValueNone Map.empty (Cmd.ofList "tool" [ "plain" ])
            Expect.isEmpty startInfo.ArgumentList "netstandard2.0 has no ArgumentList to fill"
            Expect.isNotEmpty startInfo.Arguments "the arguments should travel in the Arguments string"
        }

        // Pending until T2 (PLAN-Execution C01, pre-existing defects): the netstandard2.0 branch of `toStartInfo`
        // joins arguments with spaces, so an argument that contains whitespace or a quote reaches the child as
        // several arguments, or as a broken one.
        ptest "the netstandard build quotes an argument that contains whitespace or a quote" {
            let startInfo = Cmd.toStartInfo ValueNone Map.empty (Cmd.ofList "tool" [ "a b"; "plain"; "say \"hi\"" ])
            Expect.equal (startInfo.Arguments.Trim()) "\"a b\" plain \"say \\\"hi\\\"\"" "each argument should survive the round trip through one Arguments string"
        }
    ]
