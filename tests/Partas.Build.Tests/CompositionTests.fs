module Partas.Build.Tests.CompositionTests

open System.CommandLine
open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.Tests.Helpers

let private noop (_: StageContext) = ()

/// The names of a stage's nested stages, in order. A `fn` marks a plain step.
let private stepNames (ctx: StageContext) = [
    for step in ctx.Steps do
        match step with
        | Step.StepOfStage nested -> nested.Name
        | Step.StepFn _ -> "fn"
]

let private stageNames (ctx: PipelineContext) = [ for stage in ctx.Stages -> stage.Name ]

let private configuration () = Input.option<string> "--configuration" |> Input.def "Debug"

/// A ready-made block: the shape a reusable stage takes once it reads a flag of its own.
let private block name (config: ActionInput<string>) = input {
    let! cfg = config
    return stage name { run (fun (_: StageContext) -> ignore cfg) }
}

/// The command's own options, minus the `--help` System.CommandLine adds to every command and the
/// `--explain` the library adds to every command that runs a pipeline.
let private declared (command: Command) =
    [ for option in command.Options -> option.Name ]
    |> List.filter (fun name -> name <> "--help" && name <> "--explain")

[<Tests>]
let tests =
    testList "composition" [
        // ---------------------------------------------------------------- sequences of ready-made stages
        test "a pipeline yields a list of stages as a unit" {
            let stages = [ for name in [ "a"; "b" ] -> stage name { run noop } ]
            let built = pipeline "seq" { [ yield! stages; yield stage "c" { run noop } ] }

            Expect.equal (stageNames built) [ "a"; "b"; "c" ] "every stage in the list should be kept, in order"
        }

        test "a stage yields a list of stages as nested steps" {
            let built = stage "outer" { [ for name in [ "a"; "b" ] -> stage name { run noop } ] }

            Expect.equal (stepNames built) [ "a"; "b" ] "the list should become one nested stage per element"
        }

        test "a list of input-declaring blocks unions its inputs and keeps its order" {
            let config = configuration ()
            let built = pipeline "blocks" { [ block "restore" config; block "build" config ] }

            Expect.equal (inputNames built.Inputs) [ "--configuration" ] "the shared option should be declared once"
            Expect.equal (stageNames (built.Read (parse built.Inputs ""))) [ "restore"; "build" ] "both blocks should survive, in order"
        }

        // ---------------------------------------------------------------- for loops
        test "a pipeline loops over a collection of input-declaring blocks" {
            let config = configuration ()

            let built = pipeline "loop" {
                for name in [ "one"; "two" ] do
                    block name config
            }

            Expect.equal (inputNames built.Inputs) [ "--configuration" ] "the loop body's input should reach the pipeline"
            Expect.equal (stageNames (built.Read (parse built.Inputs ""))) [ "one"; "two" ] "one stage per iteration"
        }

        test "a stage loops over a collection of input-declaring blocks" {
            let config = configuration ()

            let built = stage "outer" {
                for name in [ "one"; "two" ] do
                    block name config
            }

            Expect.equal (stepNames (built.Read (parse built.Inputs ""))) [ "one"; "two" ] "one nested stage per iteration"
        }

        // ---------------------------------------------------------------- if/then with no else
        test "a stage skipped by an if is simply absent" {
            let built = stage "outer" {
                stage "always" { run noop }
                if false then stage "never" { run noop }
                if true then stage "sometimes" { run noop }
            }

            Expect.equal (stepNames built) [ "always"; "sometimes" ] "only the taken branch should contribute a stage"
        }

        test "a pipeline stage skipped by an if is simply absent" {
            let built = pipeline "cond" {
                stage "always" { run noop }
                if false then stage "never" { run noop }
            }

            Expect.equal (stageNames built) [ "always" ] "the untaken branch should leave no stage behind"
        }

        // ---------------------------------------------------------------- nested input-declaring stages
        test "a nested block turns its parent stage into an InputSpec" {
            let config = configuration ()

            let built = stage "outer" {
                stage "first" { run noop }
                block "second" config
                run noop
            }

            Expect.equal (inputNames built.Inputs) [ "--configuration" ] "the nested block's input should surface on the parent"
            Expect.equal (stepNames (built.Read (parse built.Inputs ""))) [ "first"; "second"; "fn" ]
                "the trailing step should stay after the block that declared the input"
        }

        test "a setting placed after a nested block still applies" {
            let config = configuration ()

            let built = stage "outer" {
                block "inner" config
                timeout 5.0
                whenNot { when' true }
            }

            let ctx: StageContext = built.Read (parse built.Inputs "")
            Expect.equal ctx.Timeout (ValueSome (System.TimeSpan.FromSeconds 5.0)) "the mirrored operation should reach the stage"
            Expect.isFalse (ctx.IsActive ctx) "the mirrored condition should reach the stage"
        }

        // ---------------------------------------------------------------- commands over stages
        test "stages yielded straight into a command form one pipeline named after it" {
            let config = configuration ()

            let built = command "build" {
                description "builds"
                stage "restore" { run noop }
                block "compile" config
            }

            Expect.equal (declared built) [ "--configuration" ] "the block's option should be registered on the command"
            Expect.equal built.Name "build" "the command keeps its name"
        }

        test "a command yields a subcommand and an extra input directly" {
            let extra = Input.option<bool> "--ci" |> Input.def false

            let built = command "root" {
                extra
                command "child" { description "a child" }
            }

            Expect.equal (declared built) [ "--ci" ] "a yielded input should be registered"
            Expect.equal [ for sub in built.Subcommands -> sub.Name ] [ "child" ] "a yielded command should become a subcommand"
        }

        test "a command loops over a collection of subcommands" {
            let built = command "root" {
                for name in [ "one"; "two" ] do
                    command name { description name }
            }

            Expect.equal [ for sub in built.Subcommands -> sub.Name ] [ "one"; "two" ] "one subcommand per iteration"
        }
    ]
