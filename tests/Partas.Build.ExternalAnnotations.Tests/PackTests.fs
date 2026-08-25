/// <summary>
/// The one test that runs a real <c>dotnet pack</c>.
/// </summary>
/// <remarks>
/// Everything else here checks the pieces: the generator writes a file, the targets file contains
/// the right properties, the check reads a .nupkg. None of that proves MSBuild actually runs the
/// generation and puts the result in <c>lib/&lt;tfm&gt;/</c>, which is the only thing a consumer
/// experiences - and the parts of the targets file most likely to be wrong (the per-TFM inner
/// build, the pack hook, the chain import) are exactly the parts no unit test can reach.
///
/// It costs a restore and two packs, so it is opt-in: set <c>PARTAS_ANNOTATIONS_INTEGRATION=1</c>.
/// </remarks>
module Partas.Build.ExternalAnnotationsTests.PackTests

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open Expecto
open Partas.Build.ExternalAnnotations
open Partas.Build.ExternalAnnotationsTests.Helpers

let private enabled =
    match Environment.GetEnvironmentVariable "PARTAS_ANNOTATIONS_INTEGRATION" with
    | null
    | ""
    | "0"
    | "false" -> false
    | _ -> true

/// The generator as a command MSBuild can shell out to, taken from this test run's own output.
let private toolCommand =
    let dll = Path.Combine (AppContext.BaseDirectory, "Partas.ExternalAnnotations.Tool.dll")
    dll, $"dotnet \"{dll}\""

/// <summary>Runs a process to completion, returning its exit code and everything it wrote.</summary>
/// <remarks>
/// Both streams are drained at once. Draining one and then the other deadlocks the moment the child fills the
/// other's pipe buffer - a few kilobytes - which for a <c>dotnet pack</c> that decides to warn about something
/// is a coin toss rather than a rare case.
/// </remarks>
let private exec (workingDirectory: string) (fileName: string) (arguments: string) =
    let info = ProcessStartInfo (fileName, arguments, WorkingDirectory = workingDirectory)
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true

    use proc = Process.Start info
    let output = proc.StandardOutput.ReadToEndAsync ()
    let error = proc.StandardError.ReadToEndAsync ()
    proc.WaitForExit ()
    proc.ExitCode, output.Result + error.Result

/// A minimal library with one annotated member, packed the way a consumer's project would be.
let private project (targetFrameworks: string) = $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>{targetFrameworks}</TargetFrameworks>
    <PackageId>PackFixture</PackageId>
    <Version>1.0.0</Version>
    <AssemblyName>PackFixture</AssemblyName>
    <DefineConstants>$(DefineConstants);JETBRAINS_ANNOTATIONS</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="JetBrains.Annotations" Version="2024.3.0" />
  </ItemGroup>
</Project>
"""

let private source = """
using JetBrains.Annotations;

namespace PackFixture
{
    public static class Api
    {
        [NotNull]
        public static string Clean([NotNull] string value) => value.Trim();
    }
}
"""

/// Sets up a project, packs it with the annotations targets injected, and hands over the .nupkg.
let private packing (targetFrameworks: string) (body: string * string -> unit) =
    inDirectory (fun dir ->
        File.WriteAllText (Path.Combine (dir, "PackFixture.csproj"), project targetFrameworks)
        File.WriteAllText (Path.Combine (dir, "Api.cs"), source)

        // A committed Directory.Build.targets is the normal route, so that is what is exercised;
        // packArgs is asserted separately for the projects one does not own.
        writeTargets (Path.Combine (dir, "Directory.Build.targets")) (Some (snd toolCommand))

        let exitCode, output = exec dir "dotnet" "pack -c Release -o ./out"

        if exitCode <> 0 then
            failtestf "dotnet pack failed:\n%s" output

        let nupkg =
            Directory.GetFiles (Path.Combine (dir, "out"), "*.nupkg")
            |> Array.tryHead
            |> Option.defaultWith (fun () -> failtest "the pack produced no .nupkg")

        body (nupkg, output))

/// The files a package contains.
let private entriesOf (nupkg: string) =
    use archive = ZipFile.OpenRead nupkg
    [ for entry in archive.Entries -> entry.FullName ]

[<Tests>]
let tests =
    testList "pack" [
        testCase "the targets file puts a generated sidecar in lib/" (fun () ->
            if not enabled then
                skiptest "set PARTAS_ANNOTATIONS_INTEGRATION=1 to run the pack integration tests"

            if not (File.Exists (fst toolCommand)) then
                skiptest $"the tool is not beside the tests at {fst toolCommand}"

            packing "net8.0" (fun (nupkg, _) ->
                Expect.contains
                    (entriesOf nupkg)
                    "lib/net8.0/PackFixture.ExternalAnnotations.xml"
                    "the sidecar ReSharper looks for, beside the assembly"))

        testCase "a multi-targeted project gets a sidecar per framework" (fun () ->
            if not enabled then
                skiptest "set PARTAS_ANNOTATIONS_INTEGRATION=1 to run the pack integration tests"

            if not (File.Exists (fst toolCommand)) then
                skiptest $"the tool is not beside the tests at {fst toolCommand}"

            // The characteristic bug: one project-level output path, so every inner build
            // overwrites the last and one TFM's annotations ship under all of them.
            packing "net8.0;net9.0" (fun (nupkg, _) ->
                let entries = entriesOf nupkg

                Expect.contains entries "lib/net8.0/PackFixture.ExternalAnnotations.xml" "net8.0"
                Expect.contains entries "lib/net9.0/PackFixture.ExternalAnnotations.xml" "net9.0"))

        testCase "packing does not double-import anything" (fun () ->
            if not enabled then
                skiptest "set PARTAS_ANNOTATIONS_INTEGRATION=1 to run the pack integration tests"

            if not (File.Exists (fst toolCommand)) then
                skiptest $"the tool is not beside the tests at {fst toolCommand}"

            packing "net8.0" (fun (_, output) ->
                // MSB4011 is what the chain import produces when it is wrong, and it is a warning:
                // the build stays green while the parent's configuration is applied twice.
                Expect.isFalse (output.Contains "MSB4011") $"the pack warned about a duplicate import:\n{output}"))

        testCase "the packed package passes the verify check" (fun () ->
            if not enabled then
                skiptest "set PARTAS_ANNOTATIONS_INTEGRATION=1 to run the pack integration tests"

            if not (File.Exists (fst toolCommand)) then
                skiptest $"the tool is not beside the tests at {fst toolCommand}"

            // End to end: the generator, the targets file and the check agreeing on one artifact.
            packing "net8.0" (fun (nupkg, _) ->
                Expect.equal (invoke verifyCommand $"--package \"{nupkg}\" --min-members 1") 0 "the exit code"))
    ]
