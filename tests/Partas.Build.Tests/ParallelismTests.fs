module Partas.Build.Tests.ParallelismTests

open System
open System.Threading
open Expecto
open Partas.Build
open Partas.Build.Internal

/// `IsParallel` is a function of the stage, so reading it back means applying it. Nothing in these overloads
/// consults the context, so the stage can be its own argument.
let private parallelism (ctx: StageContext) = ctx.IsParallel ctx

/// The number of runs of the same first character: two branches each writing five lines and never
/// interleaving give 2, however finely their writes end up interleaved give up to 10.
let private blocksOf (lines: string seq) =
    lines
    |> Seq.map (fun (l: string) -> l.Substring(0, 1))
    |> Seq.fold (fun acc c -> match acc with | h :: _ when h = c -> acc | _ -> c :: acc) []
    |> List.length

/// Forces two branches into lockstep, one write each before either takes the next: without the fix this
/// interleaves every single line, so the assertion cannot pass by scheduling luck either way.
let private lockstepWrite (barrier: Barrier) ctx prefix =
    for i in 1..5 do
        StageContext.writeLine ctx StdStream.Out $"{prefix}{i}"
        barrier.SignalAndWait(TimeSpan.FromSeconds 5.) |> ignore

let private voptionOf (value: int voption) : StageContext -> int voption = fun _ -> value
let private boolOf (value: bool) : StageContext -> bool = fun _ -> value
let private boolFirst (value: Choice<bool, int>) : StageContext -> Choice<bool, int> = fun _ -> value
let private intFirst (value: Choice<int, bool>) : StageContext -> Choice<int, bool> = fun _ -> value

/// Records how many steps were inside the sleep at once. The peak is what the throttle is supposed to bound;
/// `lock` rather than `Interlocked` because the increment and the comparison have to be one operation.
type private Concurrency () =
    let sync = obj ()
    let mutable current = 0
    let mutable peak = 0

    member _.Peak = peak

    member _.Step = async {
        lock sync (fun () ->
            current <- current + 1
            if current > peak then peak <- current)

        do! Async.Sleep 200
        lock sync (fun () -> current <- current - 1)
    }

/// Eight steps is enough that a throttle of two has to serialise them into four waves, so a runner that
/// ignored the throttle would show a peak well above it rather than a borderline one. They are written out
/// rather than generated because `for` inside a stage yields nested stages, not steps.
let private stepCount = 8

/// Runs `stepCount` overlapping steps in one stage and reports the peak concurrency observed.
let private peakOf (configure: StageContext -> StageContext) =
    let counter = Concurrency ()

    let built =
        pipeline "parallelism" {
            stage "steps" {
                noPrefixForStep

                run counter.Step
                run counter.Step
                run counter.Step
                run counter.Step
                run counter.Step
                run counter.Step
                run counter.Step
                run counter.Step
            }
            |> configure
        }

    PipelineContext.run built
    counter.Peak

[<Tests>]
let tests =
    testList "parallelism" [
        test "a stage is sequential unless told otherwise" {
            let ctx = stage "plain" { echo "hi" }
            Expect.equal (parallelism ctx) ValueNone "IsParallel should default to ValueNone"
        }

        test "parallel' without an argument is unbounded" {
            let ctx = stage "fan out" { parallel' }
            Expect.equal (parallelism ctx) (ValueSome -1) "the bare operation should mean unbounded"
        }

        test "a boolean flag switches unbounded parallelism on and off" {
            Expect.equal (parallelism (stage "on" { parallel' true })) (ValueSome -1) "true should mean unbounded"
            Expect.equal (parallelism (stage "off" { parallel' false })) ValueNone "false should mean sequential"
        }

        test "an integer is taken as the throttle" {
            Expect.equal (parallelism (stage "throttled" { parallel' 4 })) (ValueSome 4) "a positive int should be the throttle"
            Expect.equal (parallelism (stage "one" { parallel' 1 })) (ValueSome 1) "a throttle of one should be recorded as given"
            Expect.equal (parallelism (stage "negative" { parallel' -1 })) (ValueSome -1) "a non-positive int should mean unbounded"
        }

        test "a voption condition passes straight through" {
            Expect.equal (parallelism (stage "c" { parallel' (voptionOf (ValueSome 3)) })) (ValueSome 3) "the condition's result should be used verbatim"
            Expect.equal (parallelism (stage "c" { parallel' (voptionOf ValueNone) })) ValueNone "ValueNone should stay sequential"
        }

        test "a boolean condition means unbounded or sequential" {
            Expect.equal (parallelism (stage "c" { parallel' (boolOf true) })) (ValueSome -1) "true should mean unbounded"
            Expect.equal (parallelism (stage "c" { parallel' (boolOf false) })) ValueNone "false should mean sequential"
        }

        test "a Choice condition takes a flag or a throttle either way round" {
            Expect.equal (parallelism (stage "c" { parallel' (boolFirst (Choice1Of2 true)) })) (ValueSome -1) "Choice<bool,int> true should mean unbounded"
            Expect.equal (parallelism (stage "c" { parallel' (boolFirst (Choice1Of2 false)) })) ValueNone "Choice<bool,int> false should mean sequential"
            Expect.equal (parallelism (stage "c" { parallel' (boolFirst (Choice2Of2 5)) })) (ValueSome 5) "Choice<bool,int> int should be the throttle"

            Expect.equal (parallelism (stage "c" { parallel' (intFirst (Choice1Of2 5)) })) (ValueSome 5) "Choice<int,bool> int should be the throttle"
            Expect.equal (parallelism (stage "c" { parallel' (intFirst (Choice2Of2 true)) })) (ValueSome -1) "Choice<int,bool> true should mean unbounded"
            Expect.equal (parallelism (stage "c" { parallel' (intFirst (Choice2Of2 false)) })) ValueNone "Choice<int,bool> false should mean sequential"
        }

        test "the last parallel' operation wins" {
            let ctx = stage "reconfigured" {
                parallel' 4
                parallel' false
            }

            Expect.equal (parallelism ctx) ValueNone "settings overwrite rather than conjoin, unlike conditions"
        }

        test "steps run one at a time by default" {
            Expect.equal (peakOf id) 1 "an unconfigured stage should never overlap two steps"
        }

        test "a throttle bounds how many steps overlap" {
            let peak = peakOf (fun ctx -> { ctx with IsParallel = fun _ -> ValueSome 2 })
            Expect.equal peak 2 "a throttle of two should reach two and never exceed it"
        }

        test "a throttle of one is sequential" {
            let peak = peakOf (fun ctx -> { ctx with IsParallel = fun _ -> ValueSome 1 })
            Expect.equal peak 1 "a throttle of one should behave like no parallelism at all"
        }

        test "unbounded parallelism runs every step at once" {
            let peak = peakOf (fun ctx -> { ctx with IsParallel = fun _ -> ValueSome -1 })
            Expect.equal peak stepCount "nothing should hold a step back when the stage is unbounded"
        }

        test "parallel sub-stages flush output in blocks, not interleaved" {
            let lines = ResizeArray<string>()
            let write _ line = lock lines (fun () -> lines.Add line)
            use barrier = new Barrier(2)

            let built =
                pipeline "install" {
                    stage "installs" {
                        parallel' 2
                        redirectOutput write

                        stage "a" { run (fun ctx -> lockstepWrite barrier ctx "a") }
                        stage "b" { run (fun ctx -> lockstepWrite barrier ctx "b") }
                    }
                }

            PipelineContext.run built

            Expect.equal (blocksOf lines) 2 "each sub-stage's five lines should arrive as one contiguous run"
        }

        test "parallel steps flush output in blocks, not interleaved" {
            let lines = ResizeArray<string>()
            let write _ line = lock lines (fun () -> lines.Add line)
            use barrier = new Barrier(2)

            let built =
                pipeline "install" {
                    stage "installs" {
                        parallel' 2
                        redirectOutput write

                        run (fun ctx -> lockstepWrite barrier ctx "a")
                        run (fun ctx -> lockstepWrite barrier ctx "b")
                    }
                }

            PipelineContext.run built

            Expect.equal (blocksOf lines) 2 "each step's five lines should arrive as one contiguous run"
        }
    ]
