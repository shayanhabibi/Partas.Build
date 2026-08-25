/// <summary>
/// The options the stages declare, read through a real parse.
/// </summary>
/// <remarks>
/// These are the whole command-line surface of the tool, and each one is bound in exactly one place
/// - the stage that reads it - so a wrong default or arity is invisible until a build silently does
/// the wrong thing. Every assertion here goes through <c>parse</c>, which registers the option and
/// nothing else, so a value that arrives without being declared would show up as a CLR default
/// rather than as a pass.
/// </remarks>
module Partas.Build.ExternalAnnotationsTests.OptionsTests

open Expecto
open Partas.Build
open Partas.Build.Internal
open Partas.Build.ExternalAnnotations
open Partas.Build.Tests.Helpers

/// Reads one option's value from a command line, having registered that option alone.
let private read (spec: InputSpec<'T>) (commandLine: string) = spec.Read (parse spec.Inputs commandLine)

let private only (source: ActionInput<'T>) = InputSpec.ofInput source

[<Tests>]
let tests =
    testList "options" [
        test "--strict is a flag, off unless given" {
            let spec = only Options.strict

            Expect.equal (inputNames spec.Inputs) [ "--strict" ] "the name"
            Expect.isFalse (read spec "") "absent means off, so skips warn rather than fail"
            Expect.isTrue (read spec "--strict") "present with no value means on"
            Expect.isTrue (read spec "--strict true") "and an explicit value is still accepted"
        }

        test "--attribute defaults to nothing, meaning the whole JetBrains namespace" {
            let spec = only Options.attribute

            // Empty is not "no attributes": the stage reads it as "do not narrow", which is the
            // difference between a full sidecar and an empty one.
            Expect.equal (read spec "") [||] "absent"
            Expect.equal (read spec "--attribute NotNullAttribute") [| "NotNullAttribute" |] "one"
        }

        test "--attribute takes several, however they are spelled" {
            let spec = only Options.attribute

            Expect.equal
                (read spec "--attribute NotNullAttribute PureAttribute")
                [| "NotNullAttribute"; "PureAttribute" |]
                "several tokens after one flag"

            Expect.equal
                (read spec "--attribute NotNullAttribute --attribute PureAttribute")
                [| "NotNullAttribute"; "PureAttribute" |]
                "or the flag repeated"
        }

        test "--assembly and --output are required" {
            let spec = input {
                let! assembly = Options.assembly
                and! output = Options.output
                return assembly, output
            }

            let missing = parse spec.Inputs ""
            Expect.isNonEmpty missing.Errors "a generate with no assembly is a mistake to reject, not a default to invent"

            let complete = parse spec.Inputs "--assembly a.dll --output a.xml"
            Expect.isEmpty complete.Errors "both supplied"
            Expect.equal (spec.Read complete) ("a.dll", "a.xml") "and both readable"
        }

        test "--package is required and --min-members defaults to zero" {
            let spec = input {
                let! package = Options.package
                and! minMembers = Options.minMembers
                return package, minMembers
            }

            Expect.isNonEmpty (parse spec.Inputs "").Errors "there is no sensible default package to check"

            // Zero, not one: an assembly with nothing to annotate legitimately yields an empty
            // sidecar, so a count is only a failure when the caller says what to expect.
            Expect.equal (read spec "--package a.nupkg") ("a.nupkg", 0) "the default"
            Expect.equal (read spec "--package a.nupkg --min-members 12") ("a.nupkg", 12) "and an explicit floor"
        }

        test "--min-members rejects what is not a number" {
            let spec = only Options.minMembers
            Expect.isNonEmpty (parse spec.Inputs "--min-members lots").Errors "the type should be enforced at the parse"
        }

        test "--directory defaults to the working directory" {
            let spec = only Options.directory

            Expect.equal (read spec "") "." "init writes where it is run unless told otherwise"
            Expect.equal (read spec "--directory ./sub") "./sub" "and takes a path when given one"
        }

        test "--annotations-tool is absent rather than empty when not given" {
            let spec = only Options.annotationsTool

            // The difference matters: None makes the targets file fall back to packing a committed
            // file, where an empty string would pin an unrunnable command.
            Expect.equal (read spec "") None "absent"
            Expect.equal (read spec "--annotations-tool \"dotnet partas-annotations\"") (Some "dotnet partas-annotations") "given"
        }

        test "--force is a flag, off unless given" {
            let spec = only Options.force

            Expect.isFalse (read spec "") "an existing Directory.Build.targets is not overwritten by accident"
            Expect.isTrue (read spec "--force") "and is when asked"
        }

        test "every option is described, because the help is the documentation" {
            let described = [
                "--strict", Options.strict.Source
                "--force", Options.force.Source
                "--min-members", Options.minMembers.Source
            ]

            let undescribed = [
                for name, source in described do
                    match source with
                    | ParsedOption option when System.String.IsNullOrWhiteSpace option.Description -> yield name
                    | _ -> ()
            ]

            Expect.isEmpty undescribed "these options carry no description"
        }
    ]
