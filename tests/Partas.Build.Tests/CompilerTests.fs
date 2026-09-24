module Partas.Build.Tests.CompilerTests

open System.Diagnostics
open System.IO
open System.Text
open Expecto

/// The fixtures live outside every solution, so a case is named by its directory under
/// <c>tests/Partas.Build.CompilerProbe.Negative</c> and compiled by this harness alone.
let private fixtureRoot =
    let rec ascend (directory: DirectoryInfo) =
        if isNull directory then failwith "No repository root above the test assembly holds Partas.Build.slnx."
        elif File.Exists(Path.Combine(directory.FullName, "Partas.Build.slnx")) then directory.FullName
        else ascend directory.Parent

    Path.Combine(ascend (DirectoryInfo __SOURCE_DIRECTORY__), "tests", "Partas.Build.CompilerProbe.Negative")

/// Compiles one fixture and answers its exit code with stdout and stderr interleaved.
let private compile (caseName: string) =
    let project = Path.Combine(fixtureRoot, caseName, $"{caseName}.fsproj")
    Expect.isTrue (File.Exists project) $"The fixture project %s{project} should exist."

    let startInfo = ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)

    for argument in [ "build"; project; "-c"; "Debug"; "--nologo"; "-v:minimal" ] do
        startInfo.ArgumentList.Add argument

    use process' = Process.Start startInfo
    let output = StringBuilder()
    let stdout = process'.StandardOutput.ReadToEndAsync()
    let stderr = process'.StandardError.ReadToEndAsync()
    process'.WaitForExit()
    output.Append(stdout.Result).Append(stderr.Result) |> ignore
    process'.ExitCode, output.ToString()

/// Asserts that <paramref name="caseName"/> fails to compile with <paramref name="code"/>, and that a failure for
/// any other reason - a restore, a missing SDK - reports the whole build output rather than passing.
let private mustNotCompile caseName (code: string) =
    test $"{caseName} does not compile" {
        let exitCode, output = compile caseName

        if exitCode = 0 then
            failtestf "%s compiled. The case it pins is no longer rejected.\n%s" caseName output

        if not (output.Contains code) then
            failtestf "%s failed without %s, so the failure is infrastructural rather than the pinned diagnostic.\n%s" caseName code output

        Expect.stringContains output $"{caseName}.fs" "the diagnostic should come from the fixture's own source file"
    }

[<Tests>]
let tests =
    testList "CompilerTests" [
        // Each case builds a project of its own, so the list is minutes rather than milliseconds.
        mustNotCompile "NestedInputSpec" "FS0193"
        mustNotCompile "MonadicNeeds" "FS0708"
        mustNotCompile "UnsupportedSettingState" "FS0001"
        mustNotCompile "UnsupportedPipelineState" "FS0001"
    ]
    // SageFs live testing classifies a test whose full name contains "integration" as Integration, run on demand.
    |> testLabel "integration"
