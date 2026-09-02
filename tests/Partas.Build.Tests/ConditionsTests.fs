module Partas.Build.Tests.ConditionsTests

open System.Runtime.InteropServices
open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.Tests.Helpers

let private noop (_: StageContext) = ()

/// Conditions are read off the stage they belong to, so a stage is its own context here.
let private isActive (stage: StageContext) = stage.IsActive stage

let private isWindows = RuntimeInformation.IsOSPlatform OSPlatform.Windows

[<Tests>]
let tests =
    testList "conditions" [
        test "a condition applies on either side of the steps" {
            let before = stage "before" { whenEnvVar "SET"; envVars [ "SET", "1" ]; run noop }
            let after = stage "after" { envVars [ "SET", "1" ]; run noop; whenEnvVar "SET" }

            Expect.isTrue (isActive before) "a condition before the steps should apply"
            Expect.isTrue (isActive after) "a condition after the steps should apply"
            Expect.equal before.Steps.Length 1 "the step should survive a leading condition"
            Expect.equal after.Steps.Length 1 "the step should survive a trailing condition"
        }

        test "two conditions conjoin rather than replace" {
            let built =
                stage "both" {
                    envVars [ "SET", "1" ]
                    whenEnvVar "SET"
                    whenEnvVar "UNSET"
                    run noop
                }

            Expect.isFalse (isActive built) "the second condition should not replace the first"
        }

        test "whenAll requires every condition" {
            let met = stage "met" { envVars [ "A", "1"; "B", "1" ]; whenAll { envVar "A"; envVar "B" }; run noop }
            let unmet = stage "unmet" { envVars [ "A", "1" ]; whenAll { envVar "A"; envVar "B" }; run noop }

            Expect.isTrue (isActive met) "both conditions met should be active"
            Expect.isFalse (isActive unmet) "one condition unmet should be inactive"
        }

        test "whenAny requires one condition" {
            let met = stage "met" { envVars [ "B", "1" ]; whenAny { envVar "A"; envVar "B" }; run noop }
            let unmet = stage "unmet" { whenAny { envVar "A"; envVar "B" }; run noop }

            Expect.isTrue (isActive met) "one condition met should be active"
            Expect.isFalse (isActive unmet) "no condition met should be inactive"
        }

        test "whenNot requires every condition to fail" {
            let met = stage "met" { whenNot { envVar "A"; envVar "B" }; run noop }
            let unmet = stage "unmet" { envVars [ "B", "1" ]; whenNot { envVar "A"; envVar "B" }; run noop }

            Expect.isTrue (isActive met) "no condition met should be active"
            Expect.isFalse (isActive unmet) "one condition met should be inactive"
        }

        test "an empty body is the identity of its fold" {
            let all = stage "all" { whenAll { () }; run noop }
            let any = stage "any" { whenAny { () }; run noop }
            let notAny = stage "not" { whenNot { () }; run noop }

            Expect.isTrue (isActive all) "an empty whenAll should be active"
            Expect.isFalse (isActive any) "an empty whenAny should be inactive"
            Expect.isTrue (isActive notAny) "an empty whenNot should be active"
        }

        test "condition builders nest" {
            let built =
                stage "nested" {
                    envVars [ "B", "1" ]
                    whenAll {
                        envVar "B"
                        whenAny {
                            envVar "A"
                            envVar "B"
                        }
                    }
                    run noop
                }

            Expect.isTrue (isActive built) "a whenAny inside a whenAll should be one of its conditions"
        }

        test "an env var condition can require a value" {
            let matching = stage "matching" { envVars [ "MODE", "release" ]; whenEnvVar "MODE" "release"; run noop }
            let other = stage "other" { envVars [ "MODE", "debug" ]; whenEnvVar "MODE" "release"; run noop }

            Expect.isTrue (isActive matching) "a matching value should be active"
            Expect.isFalse (isActive other) "a different value should be inactive"
        }

        test "whenEnv describes one variable" {
            let accepted =
                stage "accepted" {
                    envVars [ "MODE", "release" ]
                    whenEnv {
                        name "MODE"
                        acceptValues [ "release"; "preview" ]
                    }
                    run noop
                }

            let optional = stage "optional" { whenEnv { name "MISSING"; optional }; run noop }
            let required = stage "required" { whenEnv { name "MISSING" }; run noop }

            Expect.isTrue (isActive accepted) "a value in acceptValues should be active"
            Expect.isTrue (isActive optional) "an optional variable should be active even when unset"
            Expect.isFalse (isActive required) "a required variable that is unset should be inactive"
        }

        test "a platform condition reports the current platform" {
            let windows = stage "windows" { whenWindows; run noop }
            let viaAll = stage "viaAll" { whenAll { platformWindows }; run noop }

            Expect.equal (isActive windows) isWindows "whenWindows should match the running platform"
            Expect.equal (isActive viaAll) isWindows "platformWindows should agree with whenWindows"
        }

        test "whenStage runs the stage and takes its result" {
            let mutable ran = 0
            let succeeding = stage "gated" { whenStage "probe" { run (fun _ -> ran <- ran + 1) }; run noop }

            Expect.isTrue (isActive succeeding) "a succeeding condition stage should activate"
            Expect.equal ran 1 "the condition stage should actually run"

            // The runner reports the failure on the console; that output belongs to this test.
            let failing = stage "gatedFail" { whenStage "probe" { run (fun (_: StageContext) -> failwith "boom") }; run noop }
            Expect.isFalse (isActive failing) "a failing condition stage should not activate"
        }

        test "a condition can test a bound input" {
            let config = Input.option<string> "--configuration" |> Input.def "Debug"

            let spec =
                input {
                    let! cfg = config
                    return stage "pack" { when' (cfg = "Release"); run noop }
                }

            let release = spec.Read (parse spec.Inputs "--configuration Release")
            let debug = spec.Read (parse spec.Inputs "")

            Expect.isTrue (isActive release) "the stage should be active for the matching value"
            Expect.isFalse (isActive debug) "the stage should be inactive otherwise"
        }

        test "the runner skips an inactive stage" {
            let ran = ResizeArray<string>()

            pipeline "conditions" {
                stage "active" { whenAll { () }; run (fun _ -> ran.Add "active") }
                stage "inactive" { whenAny { () }; run (fun _ -> ran.Add "inactive") }
            }
            |> PipelineContext.run

            Expect.sequenceEqual ran [ "active" ] "only the active stage should run"
        }

        test "whenSome yields a stage that closes over the value" {
            let observed = ResizeArray<string>()

            let built =
                pipeline "publish" {
                    whenSome (Some "the-key") (fun key ->
                        stage "push" { run (fun (_: StageContext) -> observed.Add key) })
                }

            built |> PipelineContext.run
            Expect.equal (List.ofSeq observed) [ "the-key" ] "the stage ran with the bound value"
        }

        test "whenSome yields nothing at all when there is no value" {
            let observed = ResizeArray<string>()

            let built =
                pipeline "publish" {
                    whenSome None (fun key -> stage "push" { run (fun (_: StageContext) -> observed.Add key) })
                    stage "always" { run (fun (_: StageContext) -> observed.Add "always") }
                }

            built |> PipelineContext.run
            Expect.equal (List.ofSeq observed) [ "always" ] "the guarded stage did not run"
            Expect.equal (built.Stages |> List.map (fun s -> s.Name)) [ "always" ]
                "and left no phantom stage behind for the log to name"
        }

        test "whenOk yields the stage only for Ok" {
            let observed = ResizeArray<string>()

            let built =
                pipeline "publish" {
                    whenOk (Ok "yes") (fun v -> stage "ok" { run (fun (_: StageContext) -> observed.Add v) })
                    whenOk (Error "no") (fun v -> stage "err" { run (fun (_: StageContext) -> observed.Add v) })
                }

            built |> PipelineContext.run
            Expect.equal (List.ofSeq observed) [ "yes" ] "only the Ok branch contributed a stage"
        }
    ]
