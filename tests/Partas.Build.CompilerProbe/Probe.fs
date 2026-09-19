module Partas.Build.CompilerProbe.Probe

open System
open System.CommandLine
open System.ComponentModel
open Expecto
open Partas.Build
open Partas.Build.Internal

/// Accepts a plain stage and nothing else, so that passing a CE result to it asserts the result's type.
let private requiresStage (ctx: StageContext) = ctx.Name

/// Accepts an input-aware stage and nothing else.
let private requiresSpec (spec: InputSpec<StageContext>) = spec.Inputs

let private parse (inputs: ActionInput list) =
    let root = RootCommand "probe"

    for input in inputs do
        match input.Source with
        | ParsedOption option -> root.Options.Add option
        | ParsedArgument argument -> root.Arguments.Add argument
        | _ -> ()

    root.Parse ""

let private configuration () = Input.option<string> "--configuration" |> Input.def "Debug"

let private tests = testList "compiler probe" [
    PipelineProbe.tests
    test "the inherited setting serves both representations across an assembly boundary" {
        let reads = ref 0
        let config = configuration ()

        let child name = {
            Inputs = [ config :> ActionInput ]
            Read = fun _ ->
                reads.Value <- reads.Value + 1
                stage name { echo "child" }
        }

        let plain: StageContext = stage "plain" { retry 2 }
        let before: InputSpec<StageContext> = stage "before" { retry 3; child "a" }
        let after: InputSpec<StageContext> = stage "after" { child "b"; retry 4 }
        let both: InputSpec<StageContext> = stage "both" { retry 5; child "c"; retry 6 }
        let wrapped: InputSpec<StageContext> = input { return stage "wrapped" { retry 7 } }

        Expect.equal reads.Value 0 "declaring a stage reads no input"

        let parsed = parse before.Inputs
        Expect.equal plain.Retry 2 "a plain stage keeps its setting"
        Expect.equal (before.Read parsed).Retry 3 "a setting before the child reaches the stage"
        Expect.equal (after.Read parsed).Retry 4 "a setting after the child reaches the stage"
        Expect.equal (both.Read parsed).Retry 6 "the last setting around the child wins"
        Expect.equal (wrapped.Read parsed).Retry 7 "a returned stage stays singly wrapped"
        Expect.equal reads.Value 3 "one read per materialized child"
    }

    test "an unannotated CE result satisfies the exact type its declarations imply" {
        let config = configuration ()

        let child = {
            Inputs = [ config :> ActionInput ]
            Read = fun _ -> stage "child" { echo "child" }
        }

        // No annotation on either binding: the functions below are what fixes the type.
        let plain = stage "plain" { retry 1 }
        let inputAware = stage "input aware" { child; retry 1 }

        Expect.equal (requiresStage plain) "plain" "a stage declaring no input stays a StageContext"
        Expect.equal (requiresSpec inputAware |> List.length) 1 "a stage declaring an input stays an InputSpec<StageContext>"
    }

    test "the shared mapping applies to every supported state" {
        let config = configuration ()
        let spec: InputSpec<BuildStage> = InputSpec.ret (fun (ctx: StageContext) -> { ctx with Name = "mapped" })
        let stageSpec: InputSpec<StageContext> = {
            Inputs = [ config :> ActionInput ]
            Read = fun _ -> stage "read" { echo "read" }
        }

        let retried (ctx: StageContext) = { ctx with Retry = 9 }
        let build: BuildStage = StageMap.mapStage retried id
        let mappedSpec: InputSpec<BuildStage> = StageMap.mapStage retried spec
        let mappedStage: InputSpec<StageContext> = StageMap.mapStage retried stageSpec

        let parsed = parse stageSpec.Inputs
        Expect.equal (build (StageContext.create "build")).Retry 9 "a build function keeps its representation"
        Expect.equal (mappedSpec.Read parsed (StageContext.create "spec")).Retry 9 "a specification of a build function keeps its representation"
        Expect.equal (mappedStage.Read parsed).Retry 9 "a specification of a stage keeps its representation"
    }

    test "one generic operation replaces the mirrored pair" {
        let builder = typeof<Partas.Build.StageBuilder.StageBuilder>
        let retries = builder.GetMethods() |> Array.filter (fun method -> method.Name = "retry")

        Expect.equal retries.Length 1 "the mirrored pair collapses into one operation"
        Expect.isTrue retries[0].IsGenericMethodDefinition "the operation is generic in the builder state"
        Expect.notEqual retries[0].DeclaringType builder "the operation is inherited"
        Expect.isNull
            (Attribute.GetCustomAttribute(retries[0], typeof<EditorBrowsableAttribute>))
            "the operation stays visible to completion"
    }

    test "producers and their consumers compose without executing" {
        let calls = ref 0
        let release = Input.option<string> "--release" |> Input.def "latest"
        let target = Input.option<string> "--target" |> Input.def "local"

        let resolve =
            Producer.define "resolve" (InputSpec.ofInput release) DependencySpec.empty (fun requested () ->
                calls.Value <- calls.Value + 1
                Operation.ofAsync (async {
                    calls.Value <- calls.Value + 1
                    return requested
                }))

        let generate =
            Producer.define "generate" (InputSpec.ret ()) (DependencySpec.require resolve) (fun () resolved ->
                calls.Value <- calls.Value + 1
                Operation.ret (String.length resolved))

        let both: DependencySpec<string * int> = DependencySpec.zip resolve generate

        // A plain stage registers no option, so `consuming` takes prerequisites that read no command line.
        let count =
            Producer.define "count" (InputSpec.ret ()) DependencySpec.empty (fun () () ->
                calls.Value <- calls.Value + 1
                Operation.ret 1)

        let publish: StageContext =
            Stage.consuming "publish" (DependencySpec.require count) (fun counted ->
                calls.Value <- calls.Value + 1
                Operation.ret (printfn "%d" counted))

        let publishTo: InputSpec<StageContext> =
            Stage.consumingWith "publishTo" (InputSpec.ofInput target) (DependencySpec.require resolve) (fun destination resolved ->
                calls.Value <- calls.Value + 1
                Operation.ret (printfn "%s %s" destination resolved))

        Expect.equal calls.Value 0 "declaring producers and consumers invokes no callback"
        Expect.equal [ for required in both.Requires -> required.Name ] [ "resolve"; "generate" ] "both producers are required, in order"
        Expect.equal (requiresStage publish) "publish" "a dependency-only consumer is a plain stage"
        Expect.equal (requiresSpec publishTo |> List.length) 2
            "an input-aware consumer is an InputSpec<StageContext> declaring its own input and its prerequisite's"

        let published = ProducerValues.empty |> ProducerValues.add resolve.Id "v1" |> ProducerValues.add generate.Id 2
        let reshaped: DependencySpec<string> = both |> DependencySpec.map (fun (resolved, generated) -> $"%s{resolved}+%d{generated}")
        Expect.equal (both.Read published) (Ok("v1", 2)) "the composed specification reads a typed tuple"
        Expect.equal (reshaped.Read published) (Ok "v1+2") "a composed specification reshapes its value applicatively"
        Expect.equal calls.Value 0 "reading published values invokes no callback"
    }
]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv tests
