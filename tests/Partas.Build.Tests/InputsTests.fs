module Partas.Build.Tests.InputsTests

open Expecto
open Partas.Build
open Partas.Build.Tests.Helpers

/// Fresh options per test: a `System.CommandLine` option is a mutable object, and dedup is by
/// reference, so sharing them across tests would make one test's registrations another's fixture.
let private options () =
    Input.option<string> "--configuration" |> Input.def "Debug",
    Input.option<bool> "--quick" |> Input.def false,
    Input.option<bool> "--watch" |> Input.def false

[<Tests>]
let tests =
    testList "inputs" [
        test "collects one input per distinct option, before any parsing" {
            let config, quick, watch = options ()

            let spec =
                inputs {
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
                inputs {
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
                inputs {
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
                inputs {
                    let! c = config
                    and! q = quick
                    return $"{c}/{q}"
                }

            let composed =
                inputs {
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
                inputs {
                    let! a = first
                    and! b = second
                    return a || b
                }

            Expect.equal spec.Inputs.Length 2 "identically named options built separately are not the same input"
        }

        test "a spec that binds nothing declares nothing" {
            let spec = inputs { return 42 }
            Expect.isEmpty spec.Inputs "return alone should collect no inputs"
            Expect.equal (spec.Read (parse [] "")) 42 "the value should survive with no options registered"
        }

        test "an unbound spec value is still readable" {
            let spec = InputSpec.ret "constant"
            Expect.isEmpty spec.Inputs "ret declares nothing"
            Expect.equal (spec.Read (parse [] "")) "constant" "ret ignores the ParseResult"
        }
    ]
