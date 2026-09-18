[<AutoOpen>]
module Partas.Build.OutputHandling

open System

[<Struct; RequireQualifiedAccess>]
type Verbosity =
    | Quiet
    | Normal
    | Verbose
    static member Default = Normal

/// <summary>Which of a step's two streams a line of output came from.</summary>
[<Struct; RequireQualifiedAccess>]
type StdStream =
    | Out
    | Err

/// <summary>The lines a stage held back, in the order they were written.</summary>
/// <remarks>
/// One capture is shared by every step of the stage that declared it and by its sub-stages, so it locks:
/// steps run in parallel, and a process's two streams are read on two threads of their own.
/// </remarks>
[<ReferenceEquality>]
type OutputCapture = private {
    lines: ResizeArray<struct (StdStream * string)>
}

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module OutputCapture =
    let create() = { lines = ResizeArray() }
    let inline private withLock<'T> (fn: ResizeArray<struct(StdStream * string)> -> 'T) (outputCapture: OutputCapture) =
        lock outputCapture.lines (fun () -> fn outputCapture.lines)
    let add stream line outputCapture = withLock _.Add(struct (stream, line)) outputCapture
    let clear = withLock _.Clear()
    let count = withLock _.Count
    let isEmpty = withLock _.Count.Equals(0)
    /// <summary>Discards every line after the first <paramref name="count"/>.</summary>
    /// <remarks>A <paramref name="count"/> at or above <see cref="P:Count"/> leaves the capture as it is.</remarks>
    /// <param name="count"></param>
    let trimTo count = withLock (fun lines ->
        let count = max 0 count
        if lines.Count > count then lines.RemoveRange (count, lines.Count - count)
        )
    /// Everything written, both streams, interleaved in the order it arrived.
    let lines = withLock (fun lines -> [ for struct (_, line) in lines do line ])
    /// Everything written, each line paired with the stream it arrived on, in write order.
    let entries = withLock List.ofSeq
    /// Only what went to stderr.
    let errors = withLock (fun lines -> [ for struct (stream, line) in lines do if stream.IsErr then line ])
    let text = lines >> String.concat Environment.NewLine
    let errorText = errors >> String.concat Environment.NewLine
    // TODO - optimize lock thrash
    /// <summary>What a failure lifts.</summary>
    /// <remarks>
    /// stderr when the process used it, and everything otherwise: a test runner that reports its failures on
    /// stdout is the ordinary case, and lifting only stderr there would lift nothing at all.
    /// </remarks>
    let failureText = fun oc ->
        if errors oc |> List.isEmpty then text oc
        else errorText oc

/// <summary>Where the output of a stage's steps goes.</summary>
/// <remarks>
/// This is the steps' output only — what the child processes write, and what <c>echo</c> says. The pipeline's
/// own log (the stage rules, the command lines, the timings) always goes to the console; <c>verbosity</c> is
/// what controls that.
/// </remarks>
[<RequireQualifiedAccess>]
type StageOutput =
    | Console
    /// Dropped.
    | Silent
    /// Held, and lifted into the error message if a step fails.
    | Captured of capture: OutputCapture
    /// Handed to a function, line by line, as it arrives.
    | Redirect of write: (StdStream -> string -> unit)

module Markup =
    open Spectre.Console
    let inline escape (str: string) = Markup.Escape str
    let inline red (str: string): string = $"[red]{str}[/]"
    let inline turquoise2 (str: string): string = $"[turquoise2]{str}[/]"
    let inline turquoise4 (str: string): string = $"[turquoise4]{str}[/]"
    let inline bold (str: string): string = $"[bold]{str}[/]"
    let inline grey (str: string): string = $"[grey50]{str}[/]"
    let inline yellow (str: string): string = $"[yellow]{str}[/]"
    let inline green (str: string): string = $"[green]{str}[/]"
    let inline lime (str: string): string = $"[lime]{str}[/]"
