module Partas.Build.Tests.InputsTests

open Expecto
open Partas.Build
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

        test "choices binds a token to its typed value" {
            let input = Input.choices<Layer> "--layer" choiceTable
            let command = Command "generate"
            command.Options.Add (getOption input)

            let parsed = command.Parse [| "--layer"; "proto" |]

            Expect.isEmpty parsed.Errors "a legal token parses cleanly"
            Expect.equal (input.GetValue parsed).Path "src/Proto" "the stage receives the record, not the token"
        }

        test "choices rejects an unknown token as a parse error rather than an exception" {
            let input = Input.choices<Layer> "--layer" choiceTable
            let command = Command "generate"
            command.Options.Add (getOption input)

            let parsed = command.Parse [| "--layer"; "nope" |]

            Expect.isNonEmpty parsed.Errors "an illegal token is a CLI diagnostic"
            let message = parsed.Errors |> Seq.map (fun e -> e.Message) |> String.concat " "
            Expect.stringContains message "nope" "the message names the offending token"
            Expect.stringContains message "ast" "and lists what was legal"
        }

        test "choicesCI accepts a differently-cased token" {
            let input = Input.choicesCI<Layer> "--layer" choiceTable
            let command = Command "generate"
            command.Options.Add (getOption input)

            let parsed = command.Parse [| "--layer"; "PROTO" |]

            Expect.isEmpty parsed.Errors "case-insensitive lookup accepts it"
            Expect.equal (input.GetValue parsed).Name "proto" "and yields the canonical entry"
        }

        test "choicesMany binds every token it was given" {
            let input = Input.choicesMany<Layer> "--layer" choiceTable
            let command = Command "generate"
            command.Options.Add (getOption input)

            let parsed = command.Parse [| "--layer"; "ast"; "--layer"; "proto" |]

            Expect.isEmpty parsed.Errors "both tokens are legal"
            Expect.equal (input.GetValue parsed |> List.map (fun l -> l.Name)) [ "ast"; "proto" ] "in the order given"
        }
    ]
