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

    test "the inherited budget settings serve both representations across an assembly boundary" {
        let config = configuration ()

        let child name: InputSpec<StageContext> = {
            Inputs = [ config :> ActionInput ]
            Read = fun _ -> stage name { echo "child" }
        }

        let plain: PipelineContext =
            pipeline "plain" {
                timeout 30<second>
                timeoutForStage 20.0
                timeoutForStep (TimeSpan.FromSeconds 10.0)
                workingDir "/tmp"
                envVars [ "PROBE", "plain" ]
                acceptExitCodes [ 0; 2 ]
                stage "one" { echo "one" }
            }

        let before: InputSpec<PipelineContext> =
            pipeline "before" {
                timeout 45.0
                workingDir (IO.DirectoryInfo "/tmp")
                child "a"
            }

        let after: InputSpec<PipelineContext> =
            pipeline "after" {
                child "b"
                timeoutForStage 15<second>
                timeoutForStep 5.0
                envVars [ "PROBE", "after" ]
                acceptExitCodes [ 0; 3 ]
            }

        Expect.equal (requiresPipeline plain) "plain" "a pipeline declaring nothing stays a plain context"
        Expect.equal (requiresSpec before).Length 1 "a setting before a declaring stage keeps the specification"
        Expect.equal (requiresSpec after).Length 1 "a setting after a declaring stage keeps the specification"

        Expect.equal plain.Timeout (ValueSome (TimeSpan.FromSeconds 30.0)) "a plain pipeline keeps the setting"
        Expect.equal plain.WorkingDir (ValueSome "/tmp") "a plain pipeline keeps the setting"
        Expect.equal plain.AcceptableExitCodes (set [ 0; 2 ]) "a plain pipeline keeps the setting"
        Expect.equal (before.Read (parse before.Inputs)).Timeout (ValueSome (TimeSpan.FromSeconds 45.0)) "a setting before a declaring stage reaches the pipeline"
        Expect.equal (after.Read (parse after.Inputs)).TimeoutForStep (ValueSome (TimeSpan.FromSeconds 5.0)) "a setting after a declaring stage reaches the pipeline"
        Expect.equal (after.Read (parse after.Inputs)).AcceptableExitCodes (set [ 0; 3 ]) "a setting after a declaring stage reaches the pipeline"
    }

    test "the inherited lifecycle settings serve both representations across an assembly boundary" {
        let config = configuration ()

        let child name: InputSpec<StageContext> = {
            Inputs = [ config :> ActionInput ]
            Read = fun _ -> stage name { echo "child" }
        }

        let teardown = stage "teardown" { echo "teardown" }
        let handler: FailureHandler = fun _ -> ()

        let plain: PipelineContext =
            pipeline "plain" {
                description "a plain pipeline"
                runBeforeEachStage ignore
                runAfterEachStage ignore
                post [ teardown ]
                onFailure handler
                stage "one" { echo "one" }
            }

        let before: InputSpec<PipelineContext> =
            pipeline "before" {
                description "before a declaring stage"
                onFailure handler
                child "a"
            }

        let after: InputSpec<PipelineContext> =
            pipeline "after" {
                child "b"
                description "after a declaring stage"
                runBeforeEachStage ignore
                runAfterEachStage ignore
                post [ teardown ]
                onFailure handler
            }

        Expect.equal (requiresPipeline plain) "plain" "a pipeline declaring nothing stays a plain context"
        Expect.equal (requiresSpec before).Length 1 "a setting before a declaring stage keeps the specification"
        Expect.equal (requiresSpec after).Length 1 "a setting after a declaring stage keeps the specification"

        Expect.equal plain.Description (ValueSome "a plain pipeline") "a plain pipeline keeps the setting"
        Expect.equal plain.OnFailure.Length 1 "a plain pipeline keeps the handler"
        Expect.equal [ for stage in plain.PostStages -> stage.Name ] [ "teardown" ] "a plain pipeline keeps the post stage"

        Expect.equal
            (before.Read (parse before.Inputs)).Description
            (ValueSome "before a declaring stage")
            "a setting before a declaring stage reaches the pipeline"
        Expect.equal
            [ for stage in (after.Read (parse after.Inputs)).PostStages -> stage.Name ]
            [ "teardown" ]
            "a setting after a declaring stage reaches the pipeline"
        Expect.equal (after.Read (parse after.Inputs)).OnFailure.Length 1 "a setting after a declaring stage reaches the pipeline"
    }
]
