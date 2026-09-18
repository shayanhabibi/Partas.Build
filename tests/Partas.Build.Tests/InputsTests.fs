module Partas.Build.Tests.InputsTests

open System
open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.Tests.Helpers
open System.CommandLine

/// Fresh options per test: a `System.CommandLine` option is a mutable object, and dedup is by
/// reference, so sharing them across tests would make one test's registrations another's fixture.
let private options () =
    Input.option<string> "--configuration" |> Input.def "Debug",
    Input.option<bool> "--quick" |> Input.def false,
    Input.option<bool> "--watch" |> Input.def false

type private Layer = { Name: string; Path: string }

let private layers =
    [ { Name = "ast"; Path = "src/Ast" }
      { Name = "proto"; Path = "src/Proto" } ]

let private choiceTable = layers |> List.map (fun layer -> layer.Name, layer)

/// Unwraps the option a `choices`-family input wraps, for direct registration outside `input { }`.
let private getOption (input: ActionInput<'T>) =
    match input.Source with
    | ParsedOption option -> option
    | _ -> failwith "expected a ParsedOption input"

[<Tests>]
let tests =
    testList "inputs" [
        test "a plain consumer harvests its producer's CLI option before parsing" {
            let flavor = Input.option<string> "--producer-flavor" |> Input.def "default"
            let producer =
                Producer.define "flavor" (InputSpec.ofInput flavor) DependencySpec.empty
                    (fun _ _ -> Operation.ret "ready")

            let consumer = Stage.consuming "use flavor" (DependencySpec.require producer) (fun _ -> Operation.ret ())
            let built = command "build" { pipeline "work" { consumer } }

            Expect.contains [ for option in built.Options -> option.Name ] "--producer-flavor"
                "the option is registered before there is a ParseResult"
        }
        test "a producer's transitive CLI option survives an inputful pipeline" {
            let upstreamOption = Input.option<string> "--upstream" |> Input.def "default"
            let ownOption = Input.option<bool> "--own" |> Input.def false
            let unrelatedOption = Input.option<bool> "--unrelated" |> Input.def false
            let upstream =
                Producer.define "upstream" (InputSpec.ofInput upstreamOption) DependencySpec.empty
                    (fun _ _ -> Operation.ret 1)
            let downstream =
                Producer.define "downstream" (InputSpec.ofInput ownOption) (DependencySpec.require upstream)
                    (fun _ _ -> Operation.ret 2)
            let other = InputSpec.map (fun _ -> stage "other" { run (fun (_: StageContext) -> ()) }) (InputSpec.ofInput unrelatedOption)

            let pipelineSpec = pipeline "work" { other; Stage.consuming "use" (DependencySpec.require downstream) (fun _ -> Operation.ret ()) }
            Expect.contains (inputNames pipelineSpec.Inputs) "--upstream" "pipeline preserves the producer option"
            let built = command "build" { pipelineSpec }
            let names = [ for option in built.Options -> option.Name ]

            Expect.contains names "--upstream" "transitive producer option is registered"
            Expect.contains names "--own" "direct producer option is registered"
            Expect.contains names "--unrelated" "existing inputful stage option is retained"
        }
        test "a nested consumer's producer option survives an inputful stage" {
            let producerOption = Input.option<string> "--nested-producer" |> Input.def "default"
            let otherOption = Input.option<bool> "--other" |> Input.def false
            let source =
                Producer.define "source" (InputSpec.ofInput producerOption) DependencySpec.empty
                    (fun _ _ -> Operation.ret 1)
            let other = InputSpec.map (fun _ -> stage "other" { run (fun (_: StageContext) -> ()) }) (InputSpec.ofInput otherOption)
            let nested = stage "parent" { other; Stage.consuming "use" (DependencySpec.require source) (fun _ -> Operation.ret ()) }
            let built = command "build" { pipeline "work" { nested } }

            Expect.contains [ for option in built.Options -> option.Name ] "--nested-producer"
                "nested requirement is visible before parsing"
        }
        test "producer options survive stages yielded directly into a command" {
            let option = Input.option<string> "--direct-producer" |> Input.def "default"
            let source = Producer.define "source" (InputSpec.ofInput option) DependencySpec.empty (fun _ _ -> Operation.ret 1)
            let stages = [ Stage.consuming "use" (DependencySpec.require source) (fun _ -> Operation.ret ()) ]
            let built = command "build" { yield! stages }

            Expect.contains [ for option in built.Options -> option.Name ] "--direct-producer"
                "command stage list carries producer inputs"
        }
        test "a custom operation between branches keeps a plain consumer's producer option in a stage" {
            let option = Input.option<string> "--stage-op-between" |> Input.def "default"
            let source = Producer.define "between" (InputSpec.ofInput option) DependencySpec.empty (fun _ _ -> Operation.ret 1)
            let other = Input.option<bool> "--stage-op-between-other" |> Input.def false
            let inputful = InputSpec.map (fun _ -> stage "other" { run (fun (_: StageContext) -> ()) }) (InputSpec.ofInput other)
            let parent = stage "parent" {
                inputful
                when' true
                Stage.consuming "use" (DependencySpec.require source) (fun _ -> Operation.ret ())
            }
            let built = command "build" { pipeline "work" { parent } }

            Expect.contains [ for option in built.Options -> option.Name ] "--stage-op-between"
                "a plain consumer behind a custom operation is still harvested"
        }
        test "a plain consumer ahead of a custom operation keeps its producer option in a stage" {
            let option = Input.option<string> "--stage-op-ahead" |> Input.def "default"
            let source = Producer.define "ahead" (InputSpec.ofInput option) DependencySpec.empty (fun _ _ -> Operation.ret 1)
            let other = Input.option<bool> "--stage-op-ahead-other" |> Input.def false
            let inputful = InputSpec.map (fun _ -> stage "other" { run (fun (_: StageContext) -> ()) }) (InputSpec.ofInput other)
            let parent = stage "parent" {
                Stage.consuming "use" (DependencySpec.require source) (fun _ -> Operation.ret ())
                when' true
                inputful
            }
            let built = command "build" { pipeline "work" { parent } }

            Expect.contains [ for option in built.Options -> option.Name ] "--stage-op-ahead"
                "a plain consumer ahead of a custom operation is still harvested"
        }
        test "a plain consumer ahead of a custom operation keeps its producer option in a pipeline" {
            let option = Input.option<string> "--pipeline-op-ahead" |> Input.def "default"
            let source = Producer.define "ahead" (InputSpec.ofInput option) DependencySpec.empty (fun _ _ -> Operation.ret 1)
            let other = Input.option<bool> "--pipeline-op-ahead-other" |> Input.def false
            let inputful = InputSpec.map (fun _ -> stage "other" { run (fun (_: StageContext) -> ()) }) (InputSpec.ofInput other)
            let work = pipeline "work" {
                Stage.consuming "use" (DependencySpec.require source) (fun _ -> Operation.ret ())
                description "mixed"
                inputful
            }
            let built = command "build" { work }

            Expect.contains [ for option in built.Options -> option.Name ] "--pipeline-op-ahead"
                "a plain consumer ahead of a custom operation is still harvested"
        }
        test "collects one input per distinct option, before any parsing" {
            let config, quick, watch = options ()

            let spec =
                input {
                    let! a = config
                    and! b = quick
                    and! c = watch
                    and! d = config
                    and! e = quick
                    and! f = config
                    and! g = watch
                    return $"{a} {b} {c} {d} {e} {f} {g}"
                }

            // No ParseResult exists at this point — that is the whole mechanism.
            Expect.equal spec.Inputs.Length 3 "seven bindings over three options should collect three inputs"

            Expect.equal
                (inputNames spec.Inputs)
                [ "--configuration"; "--quick"; "--watch" ]
                "inputs should keep first-declaration order"
        }

        test "a list option reads every token it was given" {
            let defines = Input.option<string list> "--define"
            let spec = input { let! d = defines in return d }

            Expect.equal (spec.Read(parse spec.Inputs "--define FOO --define BAR")) [ "FOO"; "BAR" ] "each occurrence should be one element"
            Expect.equal (spec.Read(parse spec.Inputs "--define FOO")) [ "FOO" ] "one token should be a singleton"
            Expect.equal (spec.Read(parse spec.Inputs "")) [] "an absent option should read as empty"
        }

        test "reads the parsed value of every binding" {
            let config, quick, watch = options ()

            let spec =
                input {
                    let! c = config
                    and! q = quick
                    and! w = watch
                    return $"{c}|{q}|{w}"
                }

            let parseResult = parse spec.Inputs "--configuration Release --quick true"
            Expect.equal (spec.Read parseResult) "Release|True|False" "unsupplied options should fall back to their defaults"
        }

        test "a binding repeated across the group reads the same value" {
            let config, _, _ = options ()

            let spec =
                input {
                    let! a = config
                    and! b = config
                    return a, b
                }

            let parseResult = parse spec.Inputs "--configuration Release"
            Expect.equal (spec.Read parseResult) ("Release", "Release") "both bindings read the one registered option"
        }

        test "merging specs unions their inputs" {
            let config, quick, _ = options ()

            let two =
                input {
                    let! c = config
                    and! q = quick
                    return $"{c}/{q}"
                }

            let composed =
                input {
                    let! x = two
                    and! y = config
                    return $"{x} y={y}"
                }

            Expect.equal composed.Inputs.Length 2 "the shared option should not be registered twice"
            let parseResult = parse composed.Inputs "--configuration Release --quick true"
            Expect.equal (composed.Read parseResult) "Release/True y=Release" "the union should not disturb either reader"
        }

        test "separately created options of the same name stay distinct" {
            // Dedup is by reference, so this is two inputs. A genuine name clash is
            // System.CommandLine's to report; collapsing it here would hide it.
            let first = Input.option<bool> "--flag"
            let second = Input.option<bool> "--flag"

            let spec =
                input {
                    let! a = first
                    and! b = second
                    return a || b
                }

            Expect.equal spec.Inputs.Length 2 "identically named options built separately are not the same input"
        }

        test "a spec that binds nothing declares nothing" {
            let spec = input { return 42 }
            Expect.isEmpty spec.Inputs "return alone should collect no inputs"
            Expect.equal (spec.Read (parse [] "")) 42 "the value should survive with no options registered"
        }

        test "an unbound spec value is still readable" {
            let spec = InputSpec.ret "constant"
            Expect.isEmpty spec.Inputs "ret declares nothing"
            Expect.equal (spec.Read (parse [] "")) "constant" "ret ignores the ParseResult"
        }

        test "InputSpec is nameable without the Internal namespace" {
            // The type annotation is the assertion: this file must not need `open Partas.Build.Internal`
            // to write a stage factory parameterised by an option. See FEEDBACK-Xantham.md §2.1.
            let factory (projects: Partas.Build.InputSpec<string list>) =
                input {
                    let! ps = projects
                    return stage "build" { run (fun _ -> ignore ps) }
                }

            let spec = factory (InputSpec.ret [ "a"; "b" ])
            Expect.equal spec.Inputs [] "a pure spec declares no inputs"
        }

        test "mapFromAmong binds a token to its typed value" {
            let input =
                Input.option<Layer> "--layer"
                |> Input.mapFromAmong choiceTable
            let command = Command "generate"
            command.Options.Add (getOption input)

            let parsed = command.Parse [| "--layer"; "proto" |]

            Expect.isEmpty parsed.Errors "a legal token parses cleanly"
            Expect.equal (input.GetValue parsed).Path "src/Proto" "the stage receives the record, not the token"
        }

        test "mapFromAmong rejects an unknown token as a parse error rather than an exception" {
            let input =
                Input.option<Layer> "--layer"
                |> Input.mapFromAmong choiceTable
            let command = Command "generate"
            command.Options.Add (getOption input)

            let parsed = command.Parse [| "--layer"; "nope" |]

            Expect.isNonEmpty parsed.Errors "an illegal token is a CLI diagnostic"
            let message = parsed.Errors |> Seq.map _.Message |> String.concat " "
            Expect.stringContains message "nope" "the message names the offending token"
            Expect.stringContains message "ast" "and lists what was legal"
        }

        test "choicesCI accepts a differently-cased token" {
            let input =
                Input.option<Layer> "--layer"
                |> Input.mapFromAmongWith StringComparer.OrdinalIgnoreCase choiceTable
            let command = Command "generate"
            command.Options.Add (getOption input)

            let parsed = command.Parse [| "--layer"; "PROTO" |]

            Expect.isEmpty parsed.Errors "case-insensitive lookup accepts it"
            Expect.equal (input.GetValue parsed).Name "proto" "and yields the canonical entry"
        }

        test "choicesMany binds every token it was given" {
            let input =
                Input.option<Layer list> "--layer"
                |> Input.mapFromMany choiceTable
            let command = Command "generate"
            command.Options.Add (getOption input)

            let parsed = command.Parse [| "--layer"; "ast"; "--layer"; "proto" |]

            Expect.isEmpty parsed.Errors "both tokens are legal"
            Expect.equal (input.GetValue parsed |> List.map (fun l -> l.Name)) [ "ast"; "proto" ] "in the order given"
        }
    ]
