module Partas.Build.Tests.Main

open System
open Expecto
open Spectre.Console

[<EntryPoint>]
let main argv =
    // Expecto installs a Console.Out that takes Expecto's lock and then writes to the terminal; on Unix, the
    // runtime locks Console.Out for every terminal write. Library output written through that Console.Out acquires
    // the two locks in the reverse order of Expecto's logger and deadlocks under a pseudo-terminal, so the
    // library's console is pinned to the process's stdout before Expecto starts.
    AnsiConsole.Console <- AnsiConsole.Create(AnsiConsoleSettings(Out = AnsiConsoleOutput Console.Out))
    runTestsInAssemblyWithCLIArgs [] argv
