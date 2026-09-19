module Partas.Build.CompilerProbe.PipelineProbe

open System
open System.CommandLine
open System.ComponentModel
open Expecto
open Partas.Build
open Partas.Build.Internal

/// Accepts a plain pipeline and nothing else, so that passing a CE result to it asserts the result's type.
let private requiresPipeline (ctx: PipelineContext) = ctx.Name

/// Accepts an input-aware pipeline and nothing else.
let private requiresSpec (spec: InputSpec<PipelineContext>) = spec.Inputs

let private parse (inputs: ActionInput list) =
    let root = RootCommand "pipeline probe"

    for input in inputs do
        match input.Source with
        | ParsedOption option -> root.Options.Add option
        | ParsedArgument argument -> root.Arguments.Add argument
        | _ -> ()

    root.Parse ""

let private configuration () = Input.option<string> "--configuration" |> Input.def "Debug"

let tests = testList "pipeline compiler probe" [
    test "the inherited output settings serve both representations across an assembly boundary" {
        let config = configuration ()

        let child name: InputSpec<StageContext> = {
            Inputs = [ config :> ActionInput ]
            Read = fun _ -> stage name { echo "child" }
        }

        let plain: PipelineContext =
            pipeline "plain" {
                silentOutput
                verbosity Verbosity.Quiet
                stage "one" { echo "one" }
            }

        let before: InputSpec<PipelineContext> =
            pipeline "before" {
                noPrefixForStep
                verbose
                child "a"
            }

        let after: InputSpec<PipelineContext> =
            pipeline "after" {
                child "b"
                noStdRedirectForStep
                quiet
                outputTo StageOutput.Silent
                redirectOutput (fun _ _ -> ())
                captureOutput
            }

        Expect.equal (requiresPipeline plain) "plain" "a pipeline declaring nothing stays a plain context"
        Expect.equal (requiresSpec before).Length 1 "a setting before a declaring stage keeps the specification"
        Expect.equal (requiresSpec after).Length 1 "a setting after a declaring stage keeps the specification"

        Expect.equal plain.Verbosity (ValueSome Verbosity.Quiet) "a plain pipeline keeps the setting"
        Expect.isTrue
            (match plain.Output with ValueSome StageOutput.Silent -> true | _ -> false)
            "a plain pipeline keeps the setting"
        Expect.isTrue (before.Read (parse before.Inputs)).NoPrefixForStep "a setting before a declaring stage reaches the pipeline"
        Expect.equal (before.Read (parse before.Inputs)).Verbosity (ValueSome Verbosity.Verbose) "a setting before a declaring stage reaches the pipeline"
        Expect.isTrue (after.Read (parse after.Inputs)).NoStdRedirectForStep "a setting after a declaring stage reaches the pipeline"
        Expect.equal (after.Read (parse after.Inputs)).Verbosity (ValueSome Verbosity.Quiet) "a setting after a declaring stage reaches the pipeline"
    }
]
