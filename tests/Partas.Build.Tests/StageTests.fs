module Partas.Build.Tests.StageTests

open Expecto
open Partas.Build
open Partas.Build.Internal

let private yes: BuildStageIsActive = fun _ -> true
let private no: BuildStageIsActive = fun _ -> false
let private noop (_: StageContext) = ()

let private stageNames (ctx: StageContext) = [
    for step in ctx.Steps do
        match step with
        | Step.StepOfStage nested -> "stage:" + nested.Name
        | Step.StepFn _ -> "fn"
]

[<Tests>]
let tests =
    testList "stage" [
        // Stages are pure: nothing below constructs or consults a ParseResult.
        test "builds its steps in order" {
            let ctx =
                stage "build" {
                    run noop
                    run noop
                }

            Expect.equal ctx.Name "build" "the stage should keep its name"
            Expect.equal (stageNames ctx) [ "fn"; "fn" ] "both steps should be recorded"
        }

        test "a nested stage survives as a step of its parent" {
            let ctx =
                stage "outer" {
                    stage "inner" { run noop }
                    run noop
                }

            Expect.equal (stageNames ctx) [ "stage:inner"; "fn" ] "the nested stage must not be discarded"
        }

        test "is active by default" {
            let ctx = stage "plain" { run noop }
            Expect.isTrue (ctx.IsActive ctx) "a stage declaring no condition is active"
        }

        test "a condition declared before the steps applies" {
            let ctx =
                stage "condFirst" {
                    no
                    run noop
                    run noop
                }

            Expect.isFalse (ctx.IsActive ctx) "the condition should reach the stage"
            Expect.equal (stageNames ctx) [ "fn"; "fn" ] "the condition should not disturb the steps"
        }

        test "a condition declared after the steps applies" {
            let ctx =
                stage "condLast" {
                    run noop
                    no
                }

            Expect.isFalse (ctx.IsActive ctx) "the condition should reach the stage"
            Expect.equal (stageNames ctx) [ "fn" ] "the condition should not disturb the steps"
        }

        test "conditions conjoin rather than replace" {
            let allTrue =
                stage "allTrue" {
                    yes
                    yes
                    run noop
                }

            let oneFalse =
                stage "oneFalse" {
                    yes
                    no
                    run noop
                }

            let firstFalse =
                stage "firstFalse" {
                    no
                    yes
                    run noop
                }

            Expect.isTrue (allTrue.IsActive allTrue) "every condition holds"
            Expect.isFalse (oneFalse.IsActive oneFalse) "a later condition must not overwrite an earlier one"
            Expect.isFalse (firstFalse.IsActive firstFalse) "an earlier condition must not be overwritten by a later one"
        }

        test "a condition leaves nested stages intact" {
            let ctx =
                stage "outer" {
                    no
                    stage "inner" { run noop }
                    run noop
                }

            Expect.isFalse (ctx.IsActive ctx) "the condition should reach the stage"
            Expect.equal (stageNames ctx) [ "stage:inner"; "fn" ] "the nested stage must survive alongside a condition"
        }

        test "settings reach the stage" {
            let ctx =
                stage "configured" {
                    workingDir "/tmp"
                    acceptExitCodes [ 0; 2 ]
                    run noop
                }

            Expect.equal ctx.WorkingDir (ValueSome "/tmp") "workingDir should be recorded"
            Expect.equal ctx.AcceptableExitCodes (set [ 0; 2 ]) "acceptExitCodes should be recorded"
        }

        test "a step built from a literal command line carries it as a label" {
            let built = stage "test" { run "dotnet test --no-build" }

            let labels = [ for step in built.Steps do match step with Step.StepFn(label, _) -> label | _ -> () ]
            Expect.equal labels [ ValueSome "dotnet test --no-build" ] "the literal is the label"
        }

        test "a step built from cmd carries the printable command line, with secrets masked" {
            let key = "super-secret-key"
            let built = stage "push" { runSensitive $"dotnet nuget push pkg.nupkg -k {key}" }

            let labels = [ for step in built.Steps do match step with Step.StepFn(label, _) -> label | _ -> () ]
            match labels with
            | [ ValueSome label ] ->
                Expect.stringContains label "nuget push" "the label shows the command"
                Expect.isFalse (label.Contains key) "and never the secret"
            | other -> failtestf "expected one labelled step, got %A" other
        }

        test "a step built from a function has no label to show" {
            let built = stage "compute" { run (fun (_: StageContext) -> ()) }

            let labels = [ for step in built.Steps do match step with Step.StepFn(label, _) -> label | _ -> () ]
            Expect.equal labels [ ValueNone ] "an opaque closure claims nothing"
        }
    ]
