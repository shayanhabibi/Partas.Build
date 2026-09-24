/// <summary>The console the library writes to, resolved at the moment of writing.</summary>
/// <remarks>
/// Spectre's static <c>AnsiConsole</c> binds to the <c>Console.Out</c> current when it is first used, and keeps
/// writing there after a host swaps <c>Console.Out</c> for another writer. Every write the library makes goes
/// through <c>ansi</c> or <c>out</c> instead, which follow the current writer.
/// </remarks>
module Partas.Build.Internal.Terminal

open System
open System.IO
open System.Runtime.CompilerServices
open System.Text
open System.Threading
open Spectre.Console

/// The width a console takes when its writer reports none, as a writer without a terminal does.
[<Literal>]
let FallbackWidth = 80

let private runOutput = AsyncLocal<TextWriter>()
let private plainConsoles = ConditionalWeakTable<TextWriter, IAnsiConsole>()
let private gate = obj ()
let mutable private lastConsole: IAnsiConsole = null
let mutable private lastOut: TextWriter = null

let private withWidth (console: IAnsiConsole) =
    if console.Profile.Width <= 0 then console.Profile.Width <- FallbackWidth
    console

/// <summary>A console over <paramref name="writer"/> that writes plain text: no ANSI sequences, no colour.</summary>
let plain (writer: TextWriter) : IAnsiConsole =
    AnsiConsoleSettings(Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Out = AnsiConsoleOutput writer)
    |> AnsiConsole.Create
    |> withWidth

/// <summary>The writer set by <c>withOutput</c> for the current run, if any.</summary>
let runWriter () : TextWriter voption =
    match runOutput.Value with
    | null -> ValueNone
    | writer -> ValueSome writer

/// <summary>The writer step output and console lines go to: the run's writer, else the current <c>Console.Out</c>.</summary>
let out () : TextWriter =
    match runOutput.Value with
    | null -> Console.Out
    | writer -> writer

/// <summary>The Spectre console the library renders to.</summary>
/// <remarks>
/// Under <c>withOutput</c>, a plain console over that writer. Otherwise <c>AnsiConsole.Console</c>, rebound to
/// the current <c>Console.Out</c> when <c>Console.Out</c> has changed since the last call and nothing else has
/// replaced <c>AnsiConsole.Console</c> in the meantime; a console assigned explicitly is kept. A console whose
/// writer reports no width is given <c>FallbackWidth</c>.
/// </remarks>
let ansi () : IAnsiConsole =
    match runOutput.Value with
    | null ->
        lock gate (fun () ->
            let current = Console.Out
            let console = AnsiConsole.Console

            let console =
                if isNull lastConsole || not (obj.ReferenceEquals(console, lastConsole)) || obj.ReferenceEquals(current, lastOut) then
                    console
                else
                    let rebound = AnsiConsole.Create(AnsiConsoleSettings(Out = AnsiConsoleOutput current))
                    AnsiConsole.Console <- rebound
                    rebound

            lastConsole <- console
            lastOut <- current
            withWidth console)
    | writer -> plainConsoles.GetValue(writer, ConditionalWeakTable<_, _>.CreateValueCallback plain)

/// <summary>The width of <c>ansi ()</c>.</summary>
let width () = (ansi ()).Profile.Width

/// <summary>Runs <paramref name="fn"/> with <paramref name="writer"/> as the run's writer.</summary>
/// <remarks>The setting flows to the work <paramref name="fn"/> starts, across threads, and ends when it returns.</remarks>
let withOutput (writer: TextWriter) (fn: unit -> 'T) : 'T =
    let previous = runOutput.Value
    runOutput.Value <- writer
    try fn () finally runOutput.Value <- previous

let private encodingSet = ref 0

/// <summary>Sets the console's input and output encodings to UTF-8, at most once per process.</summary>
/// <remarks>
/// A redirected stream keeps its encoding. A failure to set either encoding — a process with no console
/// attached — leaves it as it was.
/// </remarks>
let ensureUtf8 () =
    if Interlocked.CompareExchange(&encodingSet.contents, 1, 0) = 0 then
        if not Console.IsOutputRedirected then
            try Console.OutputEncoding <- Encoding.UTF8 with _ -> ()
        if not Console.IsInputRedirected then
            try Console.InputEncoding <- Encoding.UTF8 with _ -> ()
