/// <summary>Emptying directories and deleting files by glob, relative to a root directory.</summary>
/// <remarks>
/// A pattern is a <c>/</c>-separated path relative to the root. <c>**</c> matches any number of directories,
/// <c>*</c> any run of characters within one segment, <c>?</c> one character. A pattern starting with <c>!</c>
/// excludes what it matches. Matching is case-insensitive on Windows and case-sensitive elsewhere.
/// The walk skips <c>.git</c> and <c>node_modules</c>.
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

/// Every directory below root, relative to it, depth first. `descend` decides whether the walk enters one.
let rec private walkDirectories (root: string) (relative: string) (descend: string -> bool) = seq {
    for directory in Directory.EnumerateDirectories(Path.Combine(root, relative)) do
        let name = Path.GetFileName directory
        if not (pruned.Contains name) then
            let path = if relative = "" then name else relative + "/" + name
            yield path
            if descend path then yield! walkDirectories root path descend
}

let private empty (directory: string) =
    for file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) do
        File.SetAttributes(file, FileAttributes.Normal)
    for file in Directory.GetFiles directory do
        File.Delete file
    for child in Directory.GetDirectories directory do
        Directory.Delete(child, true)

/// <summary>The directories below <paramref name="root"/> that <paramref name="patterns"/> select, relative to it.</summary>
/// <remarks>The result holds no directory nested inside another selected directory.</remarks>
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
    (literals |> List.filter (matchesAny patterns.Exclude >> not)) @ found |> List.distinct

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
        let path = Path.Combine(root, file)
        File.SetAttributes(path, FileAttributes.Normal)
        File.Delete path
    let emptied = directories root directoryPatterns
    for directory in emptied do
        let path = Path.Combine(root, directory)
        if Directory.Exists path then empty path else Directory.CreateDirectory path |> ignore
    emptied, deleted
