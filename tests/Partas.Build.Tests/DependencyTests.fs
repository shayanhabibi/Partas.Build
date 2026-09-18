module Partas.Build.Tests.DependencyTests

open Expecto
open Partas.Build
open Partas.Build.Internal

let private producer name =
    Producer.define name (InputSpec.ret ()) DependencySpec.empty (fun _ _ -> Operation.ret 42)

let private consumer name source =
    Stage.consuming name (DependencySpec.require source) (fun _ -> Operation.ret ())

/// A producer of <paramref name="value"/> that appends its name to <paramref name="log"/> when it executes.
let private logging (log: ResizeArray<string>) name value =
    Producer.define name (InputSpec.ret ()) DependencySpec.empty (fun _ _ ->
        Operation.ofAsync (async {
            log.Add name
            return value
        }))

/// A consumer of <paramref name="source"/> that appends its name and the value it read to <paramref name="log"/>.
let private reading (log: ResizeArray<string>) name (source: Producer<int>) =
    Stage.consuming name (DependencySpec.require source) (fun value -> Operation.ofAsync (async { log.Add $"%s{name}:%i{value}" }))

/// A stage appending its name to <paramref name="log"/>, consuming nothing.
let private noting (log: ResizeArray<string>) name =
    stage name { run (fun (_: StageContext) -> log.Add name) }

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

        test "a consumer written in a stage builder keeps its own retry and conditions" {
            let option = Input.option<string> "--consumer-settings" |> Input.def "default"
            let source =
                Producer.define "compile" (InputSpec.ofInput option) DependencySpec.empty (fun _ _ -> Operation.ret 42)
            let built = stage "use" {
                retry 2
                when' false
                consumes (DependencySpec.require source) (fun _ -> Operation.ret ())
            }
            let registered = command "build" { pipeline "work" { built } }

            Expect.equal built.Retry 2 "the retry sits on the consumer stage itself"
            Expect.isNonEmpty built.Conditions "the condition sits on the consumer stage itself"
            Expect.isFalse (built.IsActive built) "the consumer's own condition governs it"
            Expect.equal (built.Requires |> List.map _.Id) [ source.Id ] "the consumer still requires the producer"
            Expect.contains [ for option in registered.Options -> option.Name ] "--consumer-settings"
                "the consumer still declares the producer's option"
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

        test "an explicitly listed producer is placed at the stage that lists it" {
            let source = producer "compile"
            let built = pipeline "work" { Producer.stage source; consumer "use" source }

            match DependencyPlan.validate [ built ] with
            | Error message -> failtest message
            | Ok plan ->
                Expect.equal plan.Placements.Length 1 "listing a producer places it once"
                let placement = plan.Placements.Head
                Expect.isTrue placement.IsExplicit "the placement records that the author listed it"
                Expect.equal placement.Before.Path [ 0 ] "the placement addresses the listed stage"
                Expect.equal placement.Owner ValueNone "a producer listed at pipeline scope is owned by the pipeline"
        }

        test "listing the same handle twice still places it once" {
            let source = producer "compile"
            let built = pipeline "work" { Producer.stage source; Producer.stage source; consumer "use" source }

            match DependencyPlan.validate [ built ] with
            | Error message -> failtest message
            | Ok plan -> Expect.equal plan.Placements.Length 1 "one identity has one placement however often it is listed"
        }

        test "a producer's prerequisites are placed before it" {
            let upstream = producer "restore"
            let downstream =
                Producer.define "compile" (InputSpec.ret ()) (DependencySpec.require upstream) (fun _ _ -> Operation.ret 7)
            let built = pipeline "work" { consumer "use" downstream }

            match DependencyPlan.validate [ built ] with
            | Error message -> failtest message
            | Ok plan ->
                Expect.equal (plan.Placements |> List.map _.Producer.Name) [ "restore"; "compile" ]
                    "placements are emitted prerequisite first"
        }

        test "a producer listed before a parallel scope serves a consumer inside it" {
            let source = producer "compile"
            let built = pipeline "work" {
                Producer.stage source
                stage "workers" { parallel' 2; consumer "use" source }
            }

            match DependencyPlan.validate [ built ] with
            | Error message -> failtest message
            | Ok plan ->
                Expect.equal plan.Placements.Length 1 "the listed producer is placed once"
                Expect.isTrue plan.Placements.Head.IsExplicit "the placement stays where the author put it"
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

        test "two sequential consumers share one execution, and the next invocation executes again" {
            let log = ResizeArray()
            let source = logging log "compile" 42
            let built = command "build" { pipeline "work" { quiet; reading log "test" source; reading log "pack" source } }

            Expect.equal (built.Parse("").Invoke()) 0 "the invocation succeeds"
            Expect.sequenceEqual log [ "compile"; "test:42"; "pack:42" ] "one execution serves both consumers"

            log.Clear()
            Expect.equal (built.Parse("").Invoke()) 0 "the second invocation succeeds"
            Expect.sequenceEqual log [ "compile"; "test:42"; "pack:42" ] "a second invocation of the same command executes the producer again"
        }

        test "an explicitly listed producer a consumer also requires executes once, where it is listed" {
            let log = ResizeArray()
            let source = logging log "compile" 7
            let built = command "build" {
                pipeline "work" {
                    quiet
                    noting log "restore"
                    Producer.stage source
                    noting log "package"
                    reading log "publish" source
                }
            }

            Expect.equal (built.Parse("").Invoke()) 0 "the invocation succeeds"
            Expect.sequenceEqual log [ "restore"; "compile"; "package"; "publish:7" ]
                "the listed producer runs once, in the place the author listed it"
        }

        test "an unlisted producer runs immediately before its first consumer" {
            let log = ResizeArray()
            let source = logging log "compile" 3
            let built = command "build" {
                pipeline "work" {
                    quiet
                    noting log "restore"
                    noting log "lint"
                    reading log "test" source
                    noting log "package"
                }
            }

            Expect.equal (built.Parse("").Invoke()) 0 "the invocation succeeds"
            Expect.sequenceEqual log [ "restore"; "lint"; "compile"; "test:3"; "package" ]
                "the producer runs before its first consumer, leaving the preceding stages where they were"
        }

        test "explain places no producer work and runs none" {
            let log = ResizeArray()
            let source = logging log "compile" 1
            let built = command "build" { pipeline "work" { quiet; reading log "test" source } }

            Expect.equal (built.Parse("--explain").Invoke()) 0 "explain succeeds"
            Expect.isEmpty log "the explained invocation executes neither the producer nor its consumer"
        }

        test "a producer publishing None publishes a value its consumer reads" {
            let seen = ResizeArray<string option>()
            let source: Producer<string option> =
                Producer.define "lookup" (InputSpec.ret ()) DependencySpec.empty (fun _ _ -> Operation.ofAsync (async { return None }))
            let built = command "build" {
                pipeline "work" {
                    quiet
                    Stage.consuming "use" (DependencySpec.require source) (fun value -> Operation.ofAsync (async { seen.Add value }))
                }
            }

            Expect.equal (built.Parse("").Invoke()) 0 "intentional absence is a successful result"
            Expect.sequenceEqual seen [ None ] "the consumer reads the absence the producer published"
        }

        test "a producer publishing ValueNone publishes a value its consumer reads" {
            let seen = ResizeArray<string voption>()
            let source: Producer<string voption> =
                Producer.define "lookup" (InputSpec.ret ()) DependencySpec.empty (fun _ _ -> Operation.ofAsync (async { return ValueNone }))
            let built = command "build" {
                pipeline "work" {
                    quiet
                    Stage.consuming "use" (DependencySpec.require source) (fun value -> Operation.ofAsync (async { seen.Add value }))
                }
            }

            Expect.equal (built.Parse("").Invoke()) 0 "intentional absence is a successful result"
            Expect.sequenceEqual seen [ ValueNone ] "the consumer reads the absence the producer published"
        }

        test "an implicit producer stays unrun while its only consumer is inactive" {
            let log = ResizeArray()
            let source = logging log "compile" 42
            let built = command "build" {
                pipeline "work" {
                    quiet

                    stage "use" {
                        when' false
                        consumes (DependencySpec.require source) (fun value -> Operation.ofAsync (async { log.Add $"use:%i{value}" }))
                    }
                }
            }

            Expect.equal (built.Parse("").Invoke()) 0 "an inactive consumer fails nothing"
            Expect.isEmpty log "the producer of an inactive consumer holds its side effects back"
        }

        test "an implicit producer runs once for two consumers while one of them is inactive" {
            let log = ResizeArray()
            let source = logging log "compile" 42
            let built = command "build" {
                pipeline "work" {
                    quiet

                    stage "skipped" {
                        when' false
                        consumes (DependencySpec.require source) (fun value -> Operation.ofAsync (async { log.Add $"skipped:%i{value}" }))
                    }

                    reading log "active" source
                }
            }

            Expect.equal (built.Parse("").Invoke()) 0 "the invocation succeeds"
            Expect.sequenceEqual log [ "compile"; "active:42" ] "the active consumer keeps the one execution the inactive one placed"
        }

        test "a producer and its consumer run among the post stages" {
            let log = ResizeArray()
            let source = logging log "compile" 5
            let built = command "build" {
                pipeline "work" {
                    quiet
                    noting log "main"
                    post [ reading log "report" source ]
                }
            }

            Expect.equal (built.Parse("").Invoke()) 0 "the invocation succeeds"
            Expect.sequenceEqual log [ "main"; "compile"; "report:5" ] "the producer runs among the post stages, before its consumer"
        }

        test "a producer's prerequisites run before it, each reading the one before" {
            let log = ResizeArray()
            let restore =
                Producer.define "restore" (InputSpec.ret ()) DependencySpec.empty (fun _ _ ->
                    Operation.ofAsync (async {
                        log.Add "restore"
                        return 1
                    }))
            let compile =
                Producer.define "compile" (InputSpec.ret ()) (DependencySpec.require restore) (fun _ restored ->
                    Operation.ofAsync (async {
                        log.Add $"compile:%i{restored}"
                        return restored + 1
                    }))
            let pack =
                Producer.define "pack" (InputSpec.ret ()) (DependencySpec.require compile) (fun _ compiled ->
                    Operation.ofAsync (async {
                        log.Add $"pack:%i{compiled}"
                        return compiled + 1
                    }))
            let built = command "build" { pipeline "work" { quiet; reading log "publish" pack } }

            Expect.equal (built.Parse("").Invoke()) 0 "the invocation succeeds"
            Expect.sequenceEqual log [ "restore"; "compile:1"; "pack:2"; "publish:3" ]
                "each producer runs after the prerequisite it reads"
        }

        test "a placement addressing no stage fails the invocation instead of running without it" {
            let source = producer "compile"
            let built = pipeline "work" { consumer "use" source }

            match DependencyPlan.validate [ built ] with
            | Error message -> failtest message
            | Ok plan ->
                let elsewhere =
                    { plan with
                        Placements = plan.Placements |> List.map (fun placement -> { placement with Before = { placement.Before with Path = [ 7 ] } }) }

                Expect.throwsT<PipelineFailedException>
                    (fun () -> ExecutionSchedule.schedule (Helpers.parse [] "") elsewhere [ built ] |> ignore)
                    "an unplaceable producer stops the invocation"
        }
    ]
