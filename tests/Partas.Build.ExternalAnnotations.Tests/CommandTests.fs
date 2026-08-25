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

/// <summary>The help a command renders, read without touching the console.</summary>
/// <remarks>
/// Through <c>InvocationConfiguration.Output</c> rather than <c>Console.SetOut</c>: the console is
/// process-wide, and Expecto runs these lists in parallel, so a captured console reads whatever
/// another test happened to write.
/// </remarks>
let private helpOf (command: Command) (commandLine: string) =
    use writer = new StringWriter ()
    let config = InvocationConfiguration (Output = writer)
    let exitCode = command.Parse(commandLine).Invoke config
    exitCode, writer.ToString ()

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

        ptestList "the help" [
            test "each command's help lists the options its stages declared" {
                // The payoff of harvesting: help text is generated from the pipeline, so a stage
                // that gains an option documents it without anyone writing help text for it.
                let cases = [
                    generateCommand, [ "--assembly"; "--output"; "--attribute"; "--strict" ]
                    verifyCommand, [ "--package"; "--min-members" ]
                    initCommand, [ "--annotations-tool"; "--force"; "--directory" ]
                ]

                for command, options in cases do
                    let exitCode, help = helpOf command "--help"

                    Expect.equal exitCode 0 $"{command.Name} --help exit code"

                    for option in options do
                        Expect.stringContains help option $"{option} is missing from {command.Name}'s help"
            }

            test "each command's help says what the command is for" {
                for command in [ generateCommand; verifyCommand; initCommand ] do
                    let _, help = helpOf command "--help"
                    Expect.stringContains help command.Description $"{command.Name}'s description"
            }
        ]

        testList "the tool" [
            // Asserted through what the tool accepts rather than through captured output: it builds
            // and runs its own root command, so there is nothing to hand a writer to.
            test "accepts each of the three commands" {
                for name in [ "generate"; "verify"; "init" ] do
                    Expect.equal (Partas.ExternalAnnotations.Tool.mainBuilder [| name; "--help" |]) 0 $"{name} --help"
            }

            test "an unknown command fails rather than doing something else" {
                Expect.notEqual (Partas.ExternalAnnotations.Tool.mainBuilder [| "genrate" |]) 0 "the exit code"
            }

            test "generates through the tool exactly as through the command" {
                inDirectory (fun dir ->
                    let output = Path.Combine (dir, "out.xml")

                    let exitCode =
                        Partas.ExternalAnnotations.Tool.mainBuilder
                            [| "generate"; "--assembly"; fixtureAssembly; "--output"; output |]

                    Expect.equal exitCode 0 "the exit code"
                    Expect.isTrue (File.Exists output) "the file")
            }
        ]
    ]
