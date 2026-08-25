/// <summary>
/// The three commands as a consumer meets them, and the tool that is nothing but the three.
/// </summary>
/// <remarks>
/// The options a command registers are harvested from the pipelines it contains rather than listed
/// by hand, which is the whole point of the design and also the thing that can go wrong without
/// anyone noticing: a stage that reads an option the command never registered still parses, and
/// reads a CLR default. So these assertions are made through the parser - what it accepts and what
/// it rejects - rather than by reading a collection.
/// </remarks>
module Partas.Build.ExternalAnnotationsTests.CommandTests

open System
open System.IO
open System.CommandLine
open Expecto
open Partas.Build.ExternalAnnotations
open Partas.Build.ExternalAnnotationsTests.Helpers

/// The option names a finished command registered.
let private optionsOf (command: Command) =
    [ for option in command.Options -> option.Name ] |> List.sort

/// Runs <paramref name="body"/> with the console captured, which is how the tool's own output is read.
let private capturing (body: unit -> 'a) =
    let original = Console.Out
    use writer = new StringWriter ()

    try
        Console.SetOut writer
        let result = body ()
        result, writer.ToString ()
    finally
        Console.SetOut original

[<Tests>]
let tests =
    testList "commands" [
        testList "generate" [
            test "is named and described" {
                Expect.equal generateCommand.Name "generate" "the name"
                Expect.isFalse (String.IsNullOrWhiteSpace generateCommand.Description) "the description"
            }

            test "registers exactly what its stage reads" {
                Expect.equal
                    (optionsOf generateCommand)
                    [ "--assembly"; "--attribute"; "--output"; "--strict" ]
                    "the options"
            }

            test "rejects an option belonging to another command" {
                Expect.isNonEmpty (parseErrors generateCommand "--assembly a.dll --output a.xml --package a.nupkg") "--package"
            }
        ]

        testList "verify" [
            test "is named and described" {
                Expect.equal verifyCommand.Name "verify" "the name"
                Expect.isFalse (String.IsNullOrWhiteSpace verifyCommand.Description) "the description"
            }

            test "registers exactly what its stage reads" {
                Expect.equal (optionsOf verifyCommand) [ "--min-members"; "--package" ] "the options"
            }

            test "rejects an option belonging to another command" {
                Expect.isNonEmpty (parseErrors verifyCommand "--package a.nupkg --strict") "--strict"
            }
        ]

        testList "init" [
            test "is named and described" {
                Expect.equal initCommand.Name "init" "the name"
                Expect.isFalse (String.IsNullOrWhiteSpace initCommand.Description) "the description"
            }

            test "registers exactly what its stage reads" {
                Expect.equal (optionsOf initCommand) [ "--annotations-tool"; "--directory"; "--force" ] "the options"
            }
        ]

        testList "the tool" [
            test "is the three commands and no more" {
                let _, help = capturing (fun () -> Partas.ExternalAnnotations.Tool.mainBuilder [| "--help" |])

                for name in [ "generate"; "verify"; "init" ] do
                    Expect.stringContains help name $"{name} is missing from the tool's help"
            }

            test "exits zero for help" {
                let exitCode, _ = capturing (fun () -> Partas.ExternalAnnotations.Tool.mainBuilder [| "--help" |])
                Expect.equal exitCode 0 "the exit code"
            }

            test "a command's help lists the options its stages declared" {
                // This is the payoff of harvesting: the help is generated from the pipeline, so a
                // stage that gains an option documents it without anyone writing help text.
                let _, help = capturing (fun () -> Partas.ExternalAnnotations.Tool.mainBuilder [| "generate"; "--help" |])

                for option in [ "--assembly"; "--output"; "--attribute"; "--strict" ] do
                    Expect.stringContains help option $"{option} is missing from generate's help"
            }

            test "an unknown command fails rather than doing something else" {
                let exitCode, _ = capturing (fun () -> Partas.ExternalAnnotations.Tool.mainBuilder [| "genrate" |])
                Expect.notEqual exitCode 0 "the exit code"
            }

            test "generates through the tool exactly as through the command" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")

                    let exitCode, _ =
                        capturing (fun () ->
                            Partas.ExternalAnnotations.Tool.mainBuilder [| "generate"; "--assembly"; fixtureAssembly; "--output"; output |])

                    Expect.equal exitCode 0 "the exit code"
                    Expect.isTrue (File.Exists output) "the file")
            }
        ]
    ]
