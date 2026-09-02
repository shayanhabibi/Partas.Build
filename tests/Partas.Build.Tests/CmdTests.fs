module Partas.Build.Tests.CmdTests

open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open Expecto
open Partas.Build
open Partas.Build.Internal

let private parts (command: Cmd) = command.Executable, command.Arguments

/// Exits zero wherever the tests can run at all.
let private succeeds = "dotnet --version"

/// Exits one: `dotnet` rejects the flag before it does anything.
let private fails = "dotnet --definitely-not-a-flag"

/// Long enough that finishing on its own would be indistinguishable from a hang, and a grandchild of the
/// process the runner starts on Windows: `cmd` is what gets killed, `ping` is what has to die with it.
let private sleeps =
    if RuntimeInformation.IsOSPlatform OSPlatform.Windows then "cmd /c ping -n 30 127.0.0.1" else "sh -c 'sleep 30'"

let private sleepProcessName = if RuntimeInformation.IsOSPlatform OSPlatform.Windows then "PING" else "sleep"

let private sleepsAlive () = Process.GetProcessesByName sleepProcessName |> Array.length

let private runs (built: PipelineContext) =
    try
        PipelineContext.run built
        true
    with :? PipelineFailedException ->
        false

[<Tests>]
let tests =
    testList "cmd" [
        test "a command line splits into an executable and arguments" {
            Expect.equal (parts (Cmd.ofString "dotnet build -c Release")) ("dotnet", [ "build"; "-c"; "Release" ]) "each token should be one argument"
            Expect.equal (parts (Cmd.ofString "  git   status  ")) ("git", [ "status" ]) "repeated whitespace should not produce empty arguments"
            Expect.equal (parts (Cmd.ofString "   ")) ("", []) "an empty command line should yield an empty command"
        }

        test "quotes hold a token together" {
            Expect.equal
                (parts (Cmd.ofString "dotnet build \"My Project.fsproj\""))
                ("dotnet", [ "build"; "My Project.fsproj" ])
                "a double-quoted argument should survive as one"

            Expect.equal
                (parts (Cmd.ofString "'/usr/local/my tools/git' status"))
                ("/usr/local/my tools/git", [ "status" ])
                "a single-quoted executable should survive as one"
        }

        test "create takes the executable as given" {
            Expect.equal (parts (Cmd.create "my tool.exe" "a b")) ("my tool.exe", [ "a"; "b" ]) "only the arguments should be split"
            Expect.equal (parts (Cmd.ofList "my tool.exe" [ "a b" ])) ("my tool.exe", [ "a b" ]) "ofList should split nothing at all"
        }

        test "an interpolation hole is exactly one argument" {
            let project = "My Project.fsproj"

            Expect.equal
                (parts (cmd $"dotnet build {project}"))
                ("dotnet", [ "build"; "My Project.fsproj" ])
                "a value containing whitespace should not be re-split"
        }

        test "a hole glued to literal text stays in the same argument" {
            let configuration = "Release"
            let first, second = "x", "y"

            Expect.equal
                (parts (cmd $"dotnet build --configuration={configuration}"))
                ("dotnet", [ "build"; "--configuration=Release" ])
                "the hole should extend the argument it is attached to"

            Expect.equal (parts (cmd $"tool {first}{second}")) ("tool", [ "xy" ]) "adjacent holes should concatenate"
        }

        test "braces and format specifiers are honoured" {
            let number = 3.14159
            Expect.equal (parts (cmd $"tool {{0}}")) ("tool", [ "{0}" ]) "an escaped brace should stay literal"
            Expect.equal (parts (cmd $"tool {number:N2}")) ("tool", [ "3.14" ]) "a format specifier should be applied"
        }

        test "a sensitive command passes its values through but does not print them" {
            let password = "hunter two"
            let sensitive = Cmd.ofFormattable true $"docker login -u me -p {password}"

            Expect.equal (parts sensitive) ("docker", [ "login"; "-u"; "me"; "-p"; "hunter two" ]) "the value should reach the process untouched"
            Expect.equal (Cmd.toLogString sensitive) "docker login -u me -p ***" "only the hole should be masked"
        }

        test "a sensitive string passed to a command passes its value through but does not print it" {
            let password = Cmd.secret "hunter two"
            let sensitive = Cmd.ofString $"docker login -u me -p {password}"
            Expect.equal (parts sensitive) ("docker", [ "login"; "-u"; "me"; "-p"; "hunter two" ]) "the value should reach the process untouched"
            Expect.equal (Cmd.toLogString sensitive) "docker login -u me -p ***" "only the hole should be masked"
        }

        test "the log quotes what a shell would need quoted" {
            let project = "My Project.fsproj"

            Expect.equal
                (Cmd.toLogString (cmd $"dotnet build {project}"))
                "dotnet build \"My Project.fsproj\""
                "an argument with whitespace should be quoted"

            Expect.equal (Cmd.toLogString (Cmd.ofString "dotnet build")) "dotnet build" "an argument without whitespace should be left alone"
        }

        test "the working directory and environment are resolved through the parent" {
            let built =
                pipeline "context" {
                    workingDir "/from-the-pipeline"
                    envVars [ "FROM_PIPELINE", "1"; "OVERRIDDEN", "pipeline" ]
                    stage "inherits" { envVars [ "FROM_STAGE", "1"; "OVERRIDDEN", "stage" ] }
                }

            let startInfo = CmdRunner.toStartInfo built.Stages[0] (Cmd.ofString "dotnet --version")

            Expect.equal startInfo.WorkingDirectory "/from-the-pipeline" "the stage should inherit the pipeline's working directory"
            Expect.equal startInfo.Environment["FROM_PIPELINE"] "1" "a pipeline env var should reach the process"
            Expect.equal startInfo.Environment["FROM_STAGE"] "1" "a stage env var should reach the process"
            Expect.equal startInfo.Environment["OVERRIDDEN"] "stage" "the stage should win over the pipeline"
            Expect.sequenceEqual startInfo.ArgumentList [ "--version" ] "the arguments should be passed one by one, not as a blob"
        }

        test "a step runs a real process and reports its exit code" {
            Expect.isTrue (runs (pipeline "ok" { stage "ok" { run succeeds } })) "a zero exit code should succeed"
            Expect.isFalse (runs (pipeline "bad" { stage "bad" { run fails } })) "a non-zero exit code should fail the pipeline"
        }

        test "acceptExitCodes widens what counts as success" {
            let built = pipeline "accepted" { stage "accepted" { acceptExitCodes [ 0; 1 ]; run fails } }
            Expect.isTrue (runs built) "an accepted exit code should succeed"
        }

        test "every run overload reaches the process" {
            let built =
                pipeline "overloads" {
                    stage "exeAndArgs" { run "dotnet" "--version" }
                    stage "commandLine" { run succeeds }
                    stage "prepared" { run (cmd $"dotnet --version") }
                    stage "fromContext" { run (fun (_: StageContext) -> succeeds) }
                    stage "fromContextAsync" { run (fun (_: StageContext) -> async { return succeeds }) }
                    stage "buildsACmd" { run (fun (_: StageContext) -> Cmd.ofString succeeds) }
                    stage "sensitive" { runSensitive $"dotnet --version" }
                }

            Expect.isTrue (runs built) "each overload should run its command"
        }

        test "a stage timeout kills the process and everything it started" {
            let before = sleepsAlive ()
            let watch = Stopwatch.StartNew()
            let timedOut = not (runs (pipeline "timeout" { stage "sleep" { timeout 2.0; run sleeps } }))
            watch.Stop()

            Expect.isTrue timedOut "a timed-out stage should fail the pipeline"
            Expect.isLessThan watch.ElapsedMilliseconds 20000L "the process should be killed at the timeout, not waited out"

            // The kill is asynchronous, and it is the grandchild that used to survive it.
            Thread.Sleep 1500
            Expect.equal (sleepsAlive ()) before "the whole process tree should be gone, not just the process the runner started"
        }

        test "argIf appends only when the condition holds" {
            let baseline = Cmd.ofList "dotnet" [ "test"; "Foo.slnx"; "--no-build" ]

            let withFilter = baseline |> Cmd.argIf true [ "--filter"; "Category=Fast" ]
            let without = baseline |> Cmd.argIf false [ "--filter"; "Category=Fast" ]

            Expect.equal withFilter.Arguments [ "test"; "Foo.slnx"; "--no-build"; "--filter"; "Category=Fast" ] "flag value two arguments, not one"
            Expect.equal without.Arguments baseline.Arguments "false condition leaves command alone"
        }

        test "argWhenSome renders given" {
            let baseline = Cmd.ofList "dotnet" [ "test"; "Foo.slnx" ]
            let applied = baseline |> Cmd.argWhenSome (Some "Category=Fast") (fun filter -> [ "--filter"; filter ])
            let skipped = baseline |> Cmd.argWhenSome None (fun filter -> [ "--filter"; filter ])

            Expect.equal applied.Arguments [ "test"; "Foo.slnx"; "--filter"; "Category=Fast" ] "Some should append rendered"
            Expect.equal skipped.Arguments baseline.Arguments "None should leave unchanged"
        }

        test "a secret argument masked in log string but intact in arguments" {
            let pushed =
                Cmd.ofList "dotnet" [ "nuget"; "push"; "pkg.nupkg" ]
                |> Cmd.secretOption "-k" "super-secret-key"

            Expect.equal pushed.Arguments [ "nuget"; "push"; "pkg.nupkg"; "-k"; "super-secret-key" ] "the process still receives real key"
            Expect.stringContains (Cmd.toLogString pushed) "-k ***" "the log masks key but keeps flag"
            Expect.isFalse ((Cmd.toLogString pushed).Contains "super-secret-key") "the key never reaches log"
        }

        test "secretOptionWhenSome omits flag entirely when there is no value" {
            let baseline = Cmd.ofList "dotnet" [ "nuget"; "push"; "pkg.nupkg" ]
            let applied = baseline |> Cmd.secretOptionWhenSome "-k" (Some "key")
            let skipped = baseline |> Cmd.secretOptionWhenSome "-k" None

            Expect.equal applied.Arguments [ "nuget"; "push"; "pkg.nupkg"; "-k"; "key" ] "Some appends flag value"
            Expect.equal skipped.Arguments baseline.Arguments "None appends neither"
            Expect.isEmpty skipped.Secrets "and marks nothing secret"
        }
    ]
