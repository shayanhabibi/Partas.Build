/// <summary>
/// The check that a package would not ship without its annotations.
/// </summary>
/// <remarks>
/// The failure this guards against is silent: a green build, a published package, and a sidecar
/// that quietly is not in it. So the check looks inside the .nupkg a consumer downloads, and these
/// tests hand it real archives - built by hand, because a <c>dotnet pack</c> here would make this a
/// test of the SDK rather than of the check.
/// </remarks>
module Partas.Build.ExternalAnnotationsTests.VerifyTests

open System.IO
open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.ExternalAnnotations
open Partas.Build.Tests.Helpers
open Partas.Build.ExternalAnnotationsTests.Helpers

/// Writes a package into a scratch directory and runs a stage over it.
let private verifying (stage: string -> StageContext) (entries: (string * string) list) (body: int -> unit) =
    inDirectory (fun dir ->
        let path = Path.Combine (dir, "Test.1.0.0.nupkg")
        Package.write path entries

        let cmd = command "verify-under-test" {
            description "under test"
            pipeline "verify" { stage path }
        }

        body (invoke cmd ""))

[<Tests>]
let tests =
    testList "verify" [
        test "verifyStage reads the package and the floor, and nothing else" {
            Expect.equal (inputNames verifyStage.Inputs) [ "--package"; "--min-members" ] "the options"
            Expect.equal (verifyStage.Read (parse verifyStage.Inputs "--package a.nupkg")).Name "verify annotations" "the stage name"
        }

        testList "presence" [
            test "a package with a sidecar beside its assembly passes" {
                verifying verifyPackage (Package.of' "Test" [ "net8.0", 12 ]) (fun exitCode ->
                    Expect.equal exitCode 0 "the exit code")
            }

            test "a package whose assembly has no sidecar fails" {
                // Negative means no sidecar written at all, which is the shipping accident itself.
                verifying verifyPackage (Package.of' "Test" [ "net8.0", -1 ]) (fun exitCode ->
                    Expect.equal exitCode 1 "the exit code")
            }

            test "a package with no lib assemblies fails rather than passing vacuously" {
                // Nothing to check is not the same as nothing wrong: it means the package was not
                // built the way the check assumes, and a silent pass would hide that forever.
                verifying verifyPackage [ "package.nuspec", "<package/>" ] (fun exitCode ->
                    Expect.equal exitCode 1 "the exit code")
            }

            test "an empty sidecar passes when no floor was given" {
                // An assembly with nothing to annotate legitimately produces an empty file.
                verifying verifyPackage (Package.of' "Test" [ "net8.0", 0 ]) (fun exitCode ->
                    Expect.equal exitCode 0 "the exit code")
            }
        ]

        testList "every framework" [
            test "all of a multi-targeted package's assemblies pass together" {
                verifying verifyPackage (Package.of' "Test" [ "net8.0", 3; "net9.0", 3; "netstandard2.0", 3 ]) (fun exitCode ->
                    Expect.equal exitCode 0 "the exit code")
            }

            test "one framework missing its sidecar fails the whole package" {
                // The characteristic multi-target bug: the targets file fires for one tfm only, and
                // a check that looked at the first assembly would call that a pass.
                verifying verifyPackage (Package.of' "Test" [ "net8.0", 3; "net9.0", -1 ]) (fun exitCode ->
                    Expect.equal exitCode 1 "the exit code")
            }
        ]

        testList "--min-members" [
            test "a sidecar at the floor passes" {
                verifying (verifyPackageOf 5) (Package.of' "Test" [ "net8.0", 5 ]) (fun exitCode ->
                    Expect.equal exitCode 0 "the exit code")
            }

            test "a sidecar under the floor fails" {
                // A file that exists but annotates almost nothing is what a half-working generator
                // leaves behind, and presence alone would call it a pass.
                verifying (verifyPackageOf 5) (Package.of' "Test" [ "net8.0", 4 ]) (fun exitCode ->
                    Expect.equal exitCode 1 "the exit code")
            }

            test "an empty sidecar fails once a floor is given" {
                verifying (verifyPackageOf 1) (Package.of' "Test" [ "net8.0", 0 ]) (fun exitCode ->
                    Expect.equal exitCode 1 "the exit code")
            }

            test "the floor applies to every framework, not to their total" {
                verifying (verifyPackageOf 5) (Package.of' "Test" [ "net8.0", 9; "net9.0", 1 ]) (fun exitCode ->
                    Expect.equal exitCode 1 "the exit code")
            }

            test "the command line's floor reaches the check" {
                inDirectory (fun dir ->
                    let path = Path.Combine (dir, "Test.1.0.0.nupkg")
                    Package.write path (Package.of' "Test" [ "net8.0", 4 ])

                    Expect.equal (invoke verifyCommand $"--package \"{path}\"") 0 "no floor"
                    Expect.equal (invoke verifyCommand $"--package \"{path}\" --min-members 4") 0 "at the floor"
                    Expect.equal (invoke verifyCommand $"--package \"{path}\" --min-members 5") 1 "under it")
            }
        ]

        test "a package that is not there fails rather than passing" {
            inDirectory (fun dir ->
                let missing = Path.Combine (dir, "absent.nupkg")
                Expect.notEqual (invoke verifyCommand $"--package \"{missing}\"") 0 "the exit code")
        }
    ]
