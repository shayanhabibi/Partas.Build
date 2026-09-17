module Partas.Build.Tests.DependencyTests

open Expecto
open Partas.Build
open Partas.Build.Internal

let private producer name =
    Producer.define name (InputSpec.ret ()) DependencySpec.empty (fun _ _ -> Operation.ret 42)

let private consumer name source =
    Stage.consuming name (DependencySpec.require source) (fun _ -> Operation.ret ())

[<Tests>]
let tests =
    testList "dependencies" [
        test "one handle shared by two consumers gets one implicit placement" {
            let source = producer "compile"
            let built = pipeline "work" { consumer "test" source; consumer "pack" source }

            match DependencyPlan.validate [ built ] with
            | Error message -> failtest message
            | Ok plan ->
                Expect.equal plan.Placements.Length 1 "one identity has one placement"
                Expect.equal plan.Placements.Head.Producer.Id source.Id "the placement keeps the handle identity"
        }

        test "runner's stage copy retains the explicitly listed handle identity" {
            let source = producer "compile"
            let mutable observed = ValueNone
            let listed = Producer.stage source
            let listed = { listed with IsActive = fun stage -> observed <- stage.Producer |> ValueOption.map _.Id; true }
            let built = pipeline "work" { listed }

            PipelineContext.run built
            Expect.equal observed (ValueSome source.Id) "the runner's reparented copy carries the allocated identity"
        }

        test "equal names and arguments still allocate distinct identities" {
            let first = producer "compile"
            let second = producer "compile"
            let built = pipeline "work" { consumer "use first" first; consumer "use second" second }

            Expect.notEqual first.Id second.Id "each declaration allocates a fresh key"
            match DependencyPlan.validate [ built ] with
            | Error message -> failtest message
            | Ok plan -> Expect.equal plan.Placements.Length 2 "both distinct handles are placed"
        }

        test "producer callback waits until execution after validation and explain" {
            let mutable prepared = 0
            let source =
                Producer.define "compile" (InputSpec.ret ()) DependencySpec.empty (fun _ _ ->
                    prepared <- prepared + 1
                    Operation.ret 42)
            let built = pipeline "work" { consumer "use" source }

            DependencyPlan.validate [ built ] |> ignore
            let explained = Explain.render [ built ]
            Expect.equal prepared 0 "declaration, validation and explain do not prepare producer work"
            Expect.stringContains explained "needs compile" "explain displays the declared dependency"
        }

        test "a consumer before its explicitly placed producer is rejected" {
            let source = producer "compile"
            let built = pipeline "work" { consumer "use" source; Producer.stage source }

            match DependencyPlan.validate [ built ] with
            | Ok _ -> failtest "a later explicit producer must not be moved before use"
            | Error message ->
                Expect.stringContains message "compile" "diagnostic names the producer"
                Expect.stringContains message "use" "diagnostic names the consumer"
        }

        test "command rejects an invalid arrangement before earlier ordinary work runs" {
            let source = producer "compile"
            let mutable ran = false
            let built = command "build" {
                pipeline "work" {
                    stage "earlier" { run (fun (_: StageContext) -> ran <- true) }
                    consumer "use" source
                    Producer.stage source
                }
            }

            Expect.equal (built.Parse("").Invoke()) 1 "validation fails invocation"
            Expect.isFalse ran "even earlier work waits for whole-plan validation"
        }

        test "an implicit producer first needed inside a parallel scope is rejected" {
            let source = producer "compile"
            let built = pipeline "work" { stage "workers" { parallel' 2; consumer "use" source } }

            match DependencyPlan.validate [ built ] with
            | Ok _ -> failtest "implicit placement within parallel scope is ambiguous"
            | Error message ->
                Expect.stringContains message "compile" "diagnostic names the producer"
                Expect.stringContains message "workers" "diagnostic names the scope"
        }

        test "an implicit producer first needed inside a shuffled scope is rejected" {
            let source = producer "compile"
            let built = pipeline "work" { stage "mixed" { shuffleExecuteSequence; consumer "use" source } }

            match DependencyPlan.validate [ built ] with
            | Ok _ -> failtest "implicit placement within shuffled scope is ambiguous"
            | Error message ->
                Expect.stringContains message "compile" "diagnostic names the producer"
                Expect.stringContains message "mixed" "diagnostic names the scope"
        }

        test "an implicit producer owned by a nested scope cannot serve an outer consumer" {
            let source = producer "compile"
            let built = pipeline "work" {
                stage "inner scope" { consumer "inner use" source }
                consumer "outer use" source
            }

            match DependencyPlan.validate [ built ] with
            | Ok _ -> failtest "the nested value is unavailable outside its owning scope"
            | Error message ->
                Expect.stringContains message "compile" "diagnostic names the producer"
                Expect.stringContains message "outer use" "diagnostic names the unreachable consumer"
        }

        test "a dependency cycle is rejected before producer effects" {
            let original = producer "compile"
            let cyclic = { original with Requires = [ original.Ref ] }
            let built = pipeline "work" { consumer "use" cyclic }

            match DependencyPlan.validate [ built ] with
            | Ok _ -> failtest "cycle must fail validation"
            | Error message -> Expect.stringContains message "cycle" "diagnostic identifies a cycle"
        }
    ]
