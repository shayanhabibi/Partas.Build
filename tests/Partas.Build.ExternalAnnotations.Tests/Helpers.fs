/// <summary>
/// Scratch directories, a package to verify, and a way to run a command the way the CLI does.
/// </summary>
module Partas.Build.ExternalAnnotationsTests.Helpers

open System
open System.IO
open System.IO.Compression
open System.Xml.Linq
open System.CommandLine
open Partas.Build

/// <summary>The C# fixture assembly, which is what these stages are pointed at.</summary>
let fixtureAssembly = typeof<Fixture.Surface.Basic>.Assembly.Location

/// <summary>
/// A directory that exists for the duration of <paramref name="body"/> and is gone afterwards.
/// </summary>
let inDirectory (body: string -> 'a) : 'a =
    let root = Path.Combine (Path.GetTempPath (), "partas-build-annotations-tests", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory root |> ignore

    try
        body root
    finally
        try
            Directory.Delete (root, true)
        with _ ->
            ()

/// <summary>
/// Runs a command the way the CLI does: parse a command line, invoke it, and report the exit code.
/// </summary>
/// <remarks>
/// Going through <c>Parse</c> and <c>Invoke</c> rather than reaching for the pipeline is the point.
/// An option a stage declares but the command never registered parses as an error, and a value read
/// without being registered silently yields the CLR default - neither is visible from any shorter
/// route.
/// </remarks>
let invoke (command: Command) (commandLine: string) = command.Parse(commandLine).Invoke ()

/// <summary>The parse errors a command line produces, which is how a rejection is told from a failure.</summary>
let parseErrors (command: Command) (commandLine: string) =
    [ for error in command.Parse(commandLine).Errors -> error.Message ]

module Package =
    /// <summary>An annotations file naming <paramref name="members"/> members, as a string.</summary>
    let annotationsFor (assemblyName: string) (members: int) =
        let root = XElement (XName.Get "assembly", XAttribute (XName.Get "name", assemblyName))

        for i in 1..members do
            root.Add (XElement (XName.Get "member", XAttribute (XName.Get "name", $"M:{assemblyName}.Type.Member{i}")))

        XDocument(root).ToString ()

    /// <summary>
    /// Writes a .nupkg containing <paramref name="entries"/>, which is all
    /// <c>verify</c> ever looks at.
    /// </summary>
    /// <remarks>
    /// A real <c>dotnet pack</c> would be a slower way to assert the same thing, and would make a
    /// test of the check into a test of the SDK.
    /// </remarks>
    let write (path: string) (entries: (string * string) list) =
        Path.GetDirectoryName path |> Directory.CreateDirectory |> ignore
        use archive = ZipFile.Open (path, ZipArchiveMode.Create)

        for name, content in entries do
            let entry = archive.CreateEntry name
            use writer = new StreamWriter (entry.Open ())
            writer.Write content

    /// <summary>
    /// A package with one assembly per target framework, each with a sidecar of
    /// <paramref name="members"/> members. A negative count means no sidecar at all.
    /// </summary>
    let of' (name: string) (frameworks: (string * int) list) =
        [
            yield "package.nuspec", $"<package><metadata><id>{name}</id></metadata></package>"

            for framework, members in frameworks do
                yield $"lib/{framework}/{name}.dll", "not really an assembly, and never read as one"

                if members >= 0 then
                    yield $"lib/{framework}/{name}.ExternalAnnotations.xml", annotationsFor name members
        ]
