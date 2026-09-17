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
        | Step.Operation _ -> "operation"
]

let private stageNames (ctx: PipelineContext) = [ for stage in ctx.Stages -> stage.Name ]

let private configuration () = Input.option<string> "--configuration" |> Input.def "Debug"

/// A ready-made block: the shape a reusable stage takes once it reads a flag of its own.
let private block name (config: ActionInput<string>) = input {
    let! cfg = config
    return stage name { run (fun (_: StageContext) -> ignore cfg) }
}

/// A block that counts how often it is materialized, so a test can assert that construction reads nothing.
let private countingBlock (reads: int ref) name (config: ActionInput<string>) = {
    Inputs = [ config :> ActionInput ]
    Read = fun _ ->
        reads.Value <- reads.Value + 1
        stage name { run noop }
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

        // ---------------------------------------------------------------- one setting over both builder states
        test "a setting applies on either side of an input-aware child" {
            let config = configuration ()
            let reads = ref 0
            let child name = countingBlock reads name config

            let plain: StageContext = stage "plain" { retry 2 }
            let before: InputSpec<StageContext> = stage "before" { retry 3; child "a" }
            let after: InputSpec<StageContext> = stage "after" { child "b"; retry 4 }
            let both: InputSpec<StageContext> = stage "both" { retry 5; child "c"; retry 6 }
            let wrapped: InputSpec<StageContext> = input { return stage "wrapped" { retry 7 } }

            Expect.equal reads.Value 0 "declaring a stage should read no input"
            Expect.equal (inputNames before.Inputs) [ "--configuration" ] "the child's input should surface on the parent"

            let parsed = parse before.Inputs ""
            Expect.equal plain.Retry 2 "a plain stage keeps its setting"
            Expect.equal (before.Read parsed).Retry 3 "a setting before the child should reach the stage"
            Expect.equal (after.Read parsed).Retry 4 "a setting after the child should reach the stage"
            Expect.equal (both.Read parsed).Retry 6 "the last of two settings around the child should win"
            Expect.equal (wrapped.Read parsed).Retry 7 "a returned stage stays singly wrapped"
            Expect.equal reads.Value 3 "one read per materialized child, and none before"
        }

        test "a negative retry count is clamped in both builder states" {
            let config = configuration ()
            let reads = ref 0

            let plain: StageContext = stage "plain" { retry -1 }
            let spec: InputSpec<StageContext> = stage "spec" { countingBlock reads "child" config; retry -1 }

            Expect.equal plain.Retry 0 "a plain stage clamps"
            Expect.equal ((spec.Read (parse spec.Inputs "")).Retry) 0 "an input-aware stage clamps the same way"
        }

        test "one retry implementation serves every builder state" {
            let builder = typeof<Partas.Build.StageBuilder.StageBuilder>
            let retries = builder.GetMethods() |> Array.filter (fun method -> method.Name = "retry")

            Expect.equal retries.Length 1 "the mirrored pair should collapse into one operation"
            Expect.isTrue retries[0].IsGenericMethodDefinition "the surviving operation should be generic in the builder state"
            Expect.notEqual retries[0].DeclaringType builder "the surviving operation should be inherited from the shared settings builder"
            Expect.isNull
                (System.Attribute.GetCustomAttribute(retries[0], typeof<System.ComponentModel.EditorBrowsableAttribute>))
                "the operation itself should stay visible to completion"
        }

        // ---------------------------------------------------------------- producers and their consumers
        test "declaring a producer declares its inputs and runs nothing" {
            let calls = ref 0
            let release = Input.option<string> "--release" |> Input.def "latest"
            let target = Input.option<string> "--target" |> Input.def "local"

            let resolve =
                Producer.define "resolve" (InputSpec.ofInput release) DependencySpec.empty (fun requested () ->
                    calls.Value <- calls.Value + 1
                    Operation.ret requested)

            let download =
                Producer.define "download" (InputSpec.ofInput target) (DependencySpec.require resolve) (fun destination resolved ->
                    calls.Value <- calls.Value + 1
                    Operation.ret $"{destination}/{resolved}")

            Expect.equal calls.Value 0 "declaring a producer should invoke no callback"
            Expect.equal (inputNames download.Inputs) [ "--target"; "--release" ] "a producer should declare its dependencies' inputs too"
            Expect.equal [ for required in download.Requires -> required.Name ] [ "resolve" ] "the declared prerequisite should be listed"
            Expect.notEqual resolve.Id download.Id "each declaration should take its own identity"
        }

        test "two producers declared alike stay distinct" {
            let define () = Producer.define "same" (InputSpec.ret ()) DependencySpec.empty (fun () () -> Operation.ret 1)

            Expect.notEqual (define ()).Id (define ()).Id "identity is allocated per declaration, not derived from the arguments"
        }

        test "dependencies compose applicatively into a typed tuple" {
            let calls = ref 0
            let release = Input.option<string> "--release" |> Input.def "latest"

            let resolve =
                Producer.define "resolve" (InputSpec.ofInput release) DependencySpec.empty (fun requested () ->
                    calls.Value <- calls.Value + 1
                    Operation.ret requested)

            let generate =
                Producer.define "generate" (InputSpec.ret ()) DependencySpec.empty (fun () () ->
                    calls.Value <- calls.Value + 1
                    Operation.ret 7)

            let both: DependencySpec<string * int> = DependencySpec.zip resolve generate

            Expect.equal calls.Value 0 "composing dependencies should invoke no callback"
            Expect.equal (inputNames both.Inputs) [ "--release" ] "the composed specification should carry its producers' inputs"
            Expect.equal [ for required in both.Requires -> required.Name ] [ "resolve"; "generate" ] "both producers should be required, in order"

            let published =
                ProducerValues.Empty
                    .Add(resolve.Id, "v1")
                    .Add(generate.Id, 7)

            Expect.equal (both.Read published) (Ok("v1", 7)) "the composed specification should read a typed tuple"
        }

        test "a dependency read reports an unavailable prerequisite and lets its own failures through" {
            let resolve = Producer.define "resolve" (InputSpec.ret ()) DependencySpec.empty (fun () () -> Operation.ret "v1")
            let required = DependencySpec.require resolve
            let reshaped = required |> DependencySpec.map (fun _ -> failwith "the caller's own function")

            match required.Read ProducerValues.Empty with
            | Ok value -> failtestf "an unpublished prerequisite should not read as %s" value
            | Error unavailable -> Expect.stringContains unavailable "resolve" "the report should name the producer"

            Expect.equal (required.Read (ProducerValues.Empty.Add(resolve.Id, "v1"))) (Ok "v1") "a published value should read back"

            Expect.throwsC
                (fun () -> reshaped.Read (ProducerValues.Empty.Add(resolve.Id, "v1")) |> ignore)
                (fun raised ->
                    Expect.stringContains raised.Message "the caller's own function"
                        "a function the caller supplied should raise rather than read as a missing prerequisite")
        }

        test "a consuming stage keeps the representation its declarations imply" {
            let calls = ref 0
            let target = Input.option<string> "--target" |> Input.def "local"
            let release = Input.option<string> "--release" |> Input.def "latest"

            let resolve =
                Producer.define "resolve" (InputSpec.ofInput release) DependencySpec.empty (fun _ () ->
                    calls.Value <- calls.Value + 1
                    Operation.ret "v1")

            let count =
                Producer.define "count" (InputSpec.ret ()) DependencySpec.empty (fun () () ->
                    calls.Value <- calls.Value + 1
                    Operation.ret 1)

            let publish: StageContext =
                Stage.consuming "publish" (DependencySpec.require count) (fun _ ->
                    calls.Value <- calls.Value + 1
                    Operation.ret ())

            let publishTo: InputSpec<StageContext> =
                Stage.consumingWith "publishTo" (InputSpec.ofInput target) (DependencySpec.require resolve) (fun _ _ ->
                    calls.Value <- calls.Value + 1
                    Operation.ret ())

            Expect.equal calls.Value 0 "declaring a consumer should invoke no callback"
            Expect.equal publish.Name "publish" "a dependency-only consumer is a plain stage"
            Expect.equal (inputNames publishTo.Inputs) [ "--target"; "--release" ]
                "a consumer should declare its own input and its prerequisites'"
            Expect.equal (publishTo.Read (parse publishTo.Inputs "")).Name "publishTo" "an input-aware consumer materializes to a stage"
            Expect.equal calls.Value 0 "materializing a consumer should still invoke no callback"
        }

        test "a consumer runs its operation and blocks on an unpublished prerequisite" {
            let ran = ref 0

            let resolve =
                Producer.define "resolve" (InputSpec.ret ()) DependencySpec.empty (fun () () -> Operation.ret "v1")

            let independent =
                Stage.consuming "independent" DependencySpec.empty (fun () ->
                    Operation.ofAsync (async { ran.Value <- ran.Value + 1 }))

            let dependent =
                Stage.consuming "dependent" (DependencySpec.require resolve) (fun _ ->
                    Operation.ofAsync (async { ran.Value <- ran.Value + 1 }))

            Expect.equal (runStage independent) (Ok ()) "a consumer requiring nothing should run its operation"
            Expect.equal ran.Value 1 "the operation should run exactly once"
            Expect.equal (runStage dependent) (Error []) "a consumer should fail while its prerequisite has published nothing"
            Expect.equal ran.Value 1 "the blocked consumer's operation should not have run"
        }

        test "a consumer of producers that read the command line must declare its inputs" {
            let release = Input.option<string> "--release" |> Input.def "latest"
            let resolve = Producer.define "resolve" (InputSpec.ofInput release) DependencySpec.empty (fun _ () -> Operation.ret "v1")

            Expect.throwsC
                (fun () -> Stage.consuming "publish" (DependencySpec.require resolve) (fun _ -> Operation.ret ()) |> ignore)
                (fun rejected ->
                    Expect.stringContains rejected.Message "consumingWith"
                        "the rejection should name the form that carries a prerequisite's inputs to the command")

            let declared = Stage.consumingWith "publish" (InputSpec.ret ()) (DependencySpec.require resolve) (fun () _ -> Operation.ret ())
            Expect.equal (inputNames declared.Inputs) [ "--release" ] "the input-aware form should register the prerequisite's input"
        }

        test "explain describes a consumer's dependencies without running a producer" {
            let calls = ref 0
            let target = Input.option<string> "--target" |> Input.def "local"

            let resolve =
                Producer.define "resolve" (InputSpec.ret ()) DependencySpec.empty (fun () () ->
                    calls.Value <- calls.Value + 1
                    Operation.ret "v1")

            let generate =
                Producer.define "generate" (InputSpec.ret ()) DependencySpec.empty (fun () () ->
                    calls.Value <- calls.Value + 1
                    Operation.ret 7)

            let publishTo =
                Stage.consumingWith "publishTo" (InputSpec.ofInput target) (DependencySpec.require generate) (fun _ _ ->
                    calls.Value <- calls.Value + 1
                    Operation.ret ())

            let built = pipeline "release" {
                Stage.consuming "publish" (DependencySpec.zip resolve generate) (fun _ ->
                    calls.Value <- calls.Value + 1
                    Operation.ret ())
                publishTo
            }

            let text = Explain.render [ built.Read (parse built.Inputs "") ]

            Expect.stringContains text "needs resolve, generate" "a consumer's step should name what it requires"
            Expect.stringContains text "needs generate" "an input-aware consumer should name its prerequisite too"
            Expect.equal calls.Value 0 "rendering the tree should invoke no producer callback"
        }
    ]
