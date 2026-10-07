namespace Partas.Build

/// Structured diagnostics emitted by build scripts and by the runner.
[<AutoOpen>]
module Annotations =
    [<Struct; RequireQualifiedAccess>]
    type AnnotationLevel =
        | Notice
        | Warning
        | Error

    /// A diagnostic with an optional title and repository-relative source location.
    /// Line and column numbers are one-based. Emitting an error does not itself fail the stage.
    [<Struct>]
    type Annotation = {
        Level: AnnotationLevel
        Message: string
        Title: string voption
        File: string voption
        Line: int voption
        EndLine: int voption
        Column: int voption
        EndColumn: int voption
    }

    module Annotation =
        let private create level message = {
            Level = level
            Message = message
            Title = ValueNone
            File = ValueNone
            Line = ValueNone
            EndLine = ValueNone
            Column = ValueNone
            EndColumn = ValueNone
        }

        /// An informational diagnostic, with no source location or title until supplied by the caller.
        let notice message = create AnnotationLevel.Notice message
        /// A warning diagnostic, with no source location or title until supplied by the caller.
        let warning message = create AnnotationLevel.Warning message
        /// An error diagnostic. Reporting it leaves execution failure policy to the caller.
        let error message = create AnnotationLevel.Error message

namespace Partas.Build.Internal

open System
open System.IO
open Partas.Build
open Spectre.Console

/// GitHub's stdout protocol, kept independent of the execution model and stage output sinks.
module internal WorkflowCommands =
    let isEnabled (envVars: Map<string, string>) =
        envVars
        |> Map.tryFind "GITHUB_ACTIONS"
        |> Option.exists (fun value -> String.Equals(value, "true", StringComparison.OrdinalIgnoreCase))

    let encodeData (message: string) =
        message.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A")

    let private encodeProperty value = (encodeData value).Replace(":", "%3A").Replace(",", "%2C")

    let tryWrite (command: string) =
        try
            // Spectre wraps even plain WriteLine calls. Runner commands must remain one physical line.
            (Terminal.out ()).WriteLine command
            true
        with
        | :? IOException
        | :? ObjectDisposedException -> false

    let private properties (annotation: Annotation) =
        if annotation.EndLine.IsSome && annotation.Line.IsNone then
            invalidArg "endLine" "An annotation's ending line requires a starting line."
        if (annotation.Column.IsSome || annotation.EndColumn.IsSome) && annotation.Line.IsNone then
            invalidArg "col" "Annotation columns require a starting line."
        if annotation.EndColumn.IsSome && annotation.Column.IsNone then
            invalidArg "endColumn" "An annotation's ending column requires a starting column."
        let startLine = ValueOption.defaultValue 1 annotation.Line
        let endLine = ValueOption.defaultValue startLine annotation.EndLine
        if endLine < startLine then invalidArg "endLine" "An annotation cannot end before its starting line."
        if endLine <> startLine && (annotation.Column.IsSome || annotation.EndColumn.IsSome) then
            invalidArg "col" "Annotation columns can only describe a range within one line."
        if endLine = startLine then
            match annotation.Column, annotation.EndColumn with
            | ValueSome first, ValueSome last when last < first ->
                invalidArg "endColumn" "An annotation cannot end before its starting column."
            | _ -> ()
        let positions = [
            "line", annotation.Line
            "endLine", annotation.EndLine
            "col", annotation.Column
            "endColumn", annotation.EndColumn
        ]
        [
            match annotation.Title with ValueSome title when not (String.IsNullOrEmpty title) -> "title", title | _ -> ()
            match annotation.File with ValueSome file when not (String.IsNullOrEmpty file) -> "file", file | _ -> ()
            for key, number in positions do
                match number with
                | ValueSome number when number > 0 -> key, string number
                | ValueSome _ -> invalidArg key "Annotation line and column numbers must be positive."
                | ValueNone -> ()
        ]

    let writeAnnotation envVars (annotation: Annotation) =
        let level, color =
            match annotation.Level with
            | AnnotationLevel.Notice -> "notice", "blue"
            | AnnotationLevel.Warning -> "warning", "yellow"
            | AnnotationLevel.Error -> "error", "red"
        let properties = properties annotation

        if isEnabled envVars then
            let properties = properties |> List.map (fun (key, value) -> key + "=" + encodeProperty value) |> String.concat ","
            let prefix = if String.IsNullOrEmpty properties then level else level + " " + properties
            $"::{prefix}::{encodeData annotation.Message}" |> tryWrite |> ignore
        else
            let location =
                properties
                |> List.filter (fun (key, _) -> key <> "title")
                |> List.map (fun (key, value) -> if key = "file" then value else key + " " + value)
                |> String.concat ", "
            let title = annotation.Title |> ValueOption.defaultValue ""
            let details = [ title; location ] |> List.filter (String.IsNullOrWhiteSpace >> not) |> String.concat " — "
            let details = if details = "" then "" else " " + details + ":"
            let label = Char.ToUpperInvariant(level[0]).ToString() + level.Substring(1)
            let text = Markup.Escape (label + ":" + details + " " + annotation.Message)
            (Terminal.ansi ()).MarkupLine $"[{color}]{text}[/]"
