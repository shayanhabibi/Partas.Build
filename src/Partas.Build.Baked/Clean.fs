/// <summary>Emptying directories and deleting files by glob, relative to a root directory.</summary>
/// <remarks>
/// A pattern is a <c>/</c>-separated path relative to the root. <c>**</c> matches any number of directories,
/// <c>*</c> any run of characters within one segment, <c>?</c> one character. A pattern starting with <c>!</c>
/// excludes what it matches. Matching is case-insensitive on Windows and case-sensitive elsewhere.
/// The walk skips <c>.git</c> and <c>node_modules</c>.
/// Symbolic links and other reparse points are never followed: a linked directory is neither walked nor selected,
/// and emptying a directory removes the links inside it, leaving their targets intact. Every path read or deleted
/// lies under the root.
/// </remarks>
module Partas.Build.Baked.Clean

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.RegularExpressions

let private ignoreCase = RuntimeInformation.IsOSPlatform OSPlatform.Windows

let private pruned = set [ ".git"; "node_modules" ]

let private segments (path: string) =
    path.Replace('\\', '/').Split([| '/' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.filter ((<>) ".")
    |> List.ofArray

let private isWildcard (pattern: string) = pattern.IndexOfAny [| '*'; '?' |] >= 0

let private segmentMatcher (segment: string) =
    let options = if ignoreCase then RegexOptions.IgnoreCase else RegexOptions.None
    let regex = Regex("^" + Regex.Escape(segment).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", options)
    fun (segment: string) -> regex.IsMatch segment

/// <summary>Whether <paramref name="path"/>, relative to the root, is matched by <paramref name="pattern"/>.</summary>
let isMatch (pattern: string) (path: string) =
    let rec go (pattern: string list) (path: string list) =
        match pattern, path with
        | [], [] -> true
        | [ "**" ], _ -> true
        | "**" :: rest, _ -> go rest path || (match path with [] -> false | _ :: tail -> go pattern tail)
        | segment :: rest, head :: tail -> segmentMatcher segment head && go rest tail
        | _ -> false
    go (segments pattern) (segments path)

type private Patterns = { Include: string list; Exclude: string list }

let private partition (patterns: string list) =
    let exclude, include' = patterns |> List.partition _.StartsWith("!")
    { Include = include'; Exclude = exclude |> List.map _.Substring(1) }

let private matchesAny patterns path = patterns |> List.exists (fun pattern -> isMatch pattern path)

let private isLink (entry: FileSystemInfo) =
    entry.Attributes.HasFlag FileAttributes.ReparsePoint

/// Whether `relative`, or a directory on the way to it from root, is a link.
let private throughLink (root: string) (relative: string) =
    segments relative
    |> List.scan (fun path segment -> Path.Combine(path, segment)) root
    |> List.tail
    |> List.exists (fun path -> Directory.Exists path && isLink (DirectoryInfo path))

/// Every directory below root, relative to it, depth first, excluding links. `descend` decides whether the walk enters one.
let rec private walkDirectories (root: string) (relative: string) (descend: string -> bool) = seq {
    for directory in DirectoryInfo(Path.Combine(root, relative)).EnumerateDirectories() do
        if not (pruned.Contains directory.Name || isLink directory) then
            let path = if relative = "" then directory.Name else relative + "/" + directory.Name
            yield path
            if descend path then yield! walkDirectories root path descend
}

/// Deletes the contents of `directory`. A link is removed itself; its target is left intact.
let rec private empty (directory: DirectoryInfo) =
    for entry in directory.EnumerateFileSystemInfos() do
        match entry with
        | :? DirectoryInfo as child when not (isLink child) ->
            empty child
            child.Delete()
        | _ ->
            if not (isLink entry) then entry.Attributes <- FileAttributes.Normal
            entry.Delete()

/// <summary>The directories below <paramref name="root"/> that <paramref name="patterns"/> select, relative to it.</summary>
/// <remarks>
/// The result holds only the outermost selected directories: a selected directory nested inside another selected
/// directory, literal or matched, is omitted. A literal directory that is a link, or lies beneath one, is omitted.
/// </remarks>
let directories (root: string) (patterns: string list) =
    let patterns = partition patterns
    let selected path = matchesAny patterns.Include path && not (matchesAny patterns.Exclude path)
    let literals = patterns.Include |> List.filter (isWildcard >> not) |> List.map (segments >> String.concat "/")
    let found =
        if patterns.Include |> List.exists isWildcard && Directory.Exists root then
            walkDirectories root "" (fun path -> not (matchesAny patterns.Include path))
            |> Seq.filter selected
            |> List.ofSeq
        else []
    let comparison = if ignoreCase then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal
    let literals = literals |> List.filter (fun path -> not (matchesAny patterns.Exclude path || throughLink root path))
    let candidates = literals @ found |> List.distinct
    let isNested (path: string) =
        candidates |> List.exists (fun (outer: string) -> path.StartsWith(outer + "/", comparison))
    candidates |> List.filter (isNested >> not)

/// <summary>The files below <paramref name="root"/> that <paramref name="patterns"/> select, relative to it.</summary>
let files (root: string) (patterns: string list) =
    let patterns = partition patterns
    if patterns.Include.IsEmpty || not (Directory.Exists root) then [] else
    let inDirectory (relative: string) =
        Directory.EnumerateFiles(Path.Combine(root, relative))
        |> Seq.map (fun file -> if relative = "" then Path.GetFileName file else relative + "/" + Path.GetFileName file)
    Seq.append (inDirectory "") (walkDirectories root "" (fun _ -> true) |> Seq.collect inDirectory)
    |> Seq.filter (fun path -> matchesAny patterns.Include path && not (matchesAny patterns.Exclude path))
    |> List.ofSeq

/// <summary>Empties the directories <paramref name="directoryPatterns"/> select and deletes the files
/// <paramref name="filePatterns"/> select, and answers the relative paths of both.</summary>
/// <remarks>
/// A directory pattern without wildcards names a directory to create if it is missing, and to empty otherwise.
/// A directory is emptied rather than deleted.
/// </remarks>
let run (root: string) (directoryPatterns: string list) (filePatterns: string list) =
    let deleted = files root filePatterns
    for file in deleted do
        let file = FileInfo(Path.Combine(root, file))
        if not (isLink file) then file.Attributes <- FileAttributes.Normal
        file.Delete()
    let emptied = directories root directoryPatterns
    for directory in emptied do
        let path = Path.Combine(root, directory)
        if Directory.Exists path then empty (DirectoryInfo path) else Directory.CreateDirectory path |> ignore
    emptied, deleted
