/// Explicitly register project-version updaters in existing ShipIt changelogs.
[<RequireQualifiedAccess>]
module Partas.Build.EasyBuild.ShipIt.ProjectSetup

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Xml.Linq
open YamlDotNet.RepresentationModel

let private relativePath (directory: string) (path: string) =
    let root = Uri(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + string Path.DirectorySeparatorChar)
    Uri.UnescapeDataString(root.MakeRelativeUri(Uri(Path.GetFullPath path)).ToString())

let private property name (mapping: YamlMappingNode) =
    let mutable node = Unchecked.defaultof<YamlNode>
    if mapping.Children.TryGetValue(YamlScalarNode(name), &node) then Some node else None

let private scalar name (node: YamlNode) =
    match node with
    | :? YamlScalarNode as value when not (String.IsNullOrWhiteSpace value.Value) -> value.Value
    | _ -> invalidOp $"Expected a scalar {name} in ShipIt configuration."

let private validateProject (path: string) =
    let document = XDocument.Load path
    let versions = document.Descendants(XName.Get "Version") |> Seq.toList
    match versions with
    | [ version ] when version.Parent.Name = XName.Get "PropertyGroup"
                       && version.Parent.Parent.Name = XName.Get "Project"
                       && isNull (version.Attribute(XName.Get "Condition"))
                       && isNull (version.Parent.Attribute(XName.Get "Condition"))
                       && not (String.IsNullOrWhiteSpace version.Value)
                       && not (version.Value.Contains "$" || version.Value.Contains "@(") -> ()
    | _ -> invalidArg "projects" $"{path}: requires exactly one unconditional literal /Project/PropertyGroup/Version. Configure externally defined or conditional versions manually."

/// Add missing XML updaters atomically, retaining the existing changelog bytes outside inserted YAML.
let configure (changelog: string) (projects: string list) =
    if projects.IsEmpty then invalidArg "projects" "At least one project is required."
    let changelog = Path.GetFullPath changelog
    let directory = Path.GetDirectoryName changelog
    let projects = projects |> List.map Path.GetFullPath |> List.distinct
    projects |> List.iter validateProject
    let bytes = File.ReadAllBytes changelog
    let hasBom = bytes.Length >= 3 && bytes[0] = 239uy && bytes[1] = 187uy && bytes[2] = 191uy
    let offset = if hasBom then 3 else 0
    let text = UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset)
    let front = Regex.Match(text, "\\A---\\r?\\n(?<yaml>[\\s\\S]*?)^---[ \\t]*(?:\\r?\\n|$)", RegexOptions.Multiline)
    if not front.Success then invalidOp "Changelog must contain delimited YAML front matter; initialize it with ShipIt first."
    let yaml = front.Groups["yaml"]
    let stream = YamlStream()
    use reader = new StringReader(yaml.Value)
    stream.Load reader
    if stream.Documents.Count <> 1 then invalidOp "ShipIt front matter must contain one YAML document."
    let mapping =
        match stream.Documents[0].RootNode with
        | :? YamlMappingNode as value -> value
        | _ -> invalidOp "ShipIt front matter must be a mapping."
    let selector = "/Project/PropertyGroup/Version"
    let existing, insertion, indent, header =
        match property "updaters" mapping with
        | None -> Set.empty, yaml.Length, 2, "updaters:"
        | Some (:? YamlSequenceNode as sequence) ->
            if sequence.Style = YamlDotNet.Core.Events.SequenceStyle.Flow || sequence.Children.Count = 0 then
                invalidOp "Use a nonempty block-style updaters sequence, or remove the empty updaters key before configuring projects."
            let registered =
                sequence.Children |> Seq.choose (fun item ->
                    match item with
                    | :? YamlMappingNode as updater ->
                        match property "xml" updater with
                        | None -> None
                        | Some (:? YamlMappingNode as xml) ->
                            let file = property "file" xml |> Option.map (scalar "xml.file") |> Option.defaultWith (fun () -> invalidOp "XML updater lacks file.")
                            let selected = property "selector" xml |> Option.map (scalar "xml.selector") |> Option.defaultWith (fun () -> invalidOp "XML updater lacks selector.")
                            Some (Path.GetFullPath(Path.Combine(directory, file)), selected)
                        | _ -> invalidOp "XML updater must be a mapping."
                    | _ -> invalidOp "Each updater must be a mapping.") |> Set.ofSeq
            let last = sequence.Children[sequence.Children.Count - 1]
            let lineEnd = yaml.Value.IndexOf('\n', int last.End.Index)
            let insertion = if lineEnd < 0 then yaml.Length else lineEnd + 1
            registered, insertion, int sequence.Start.Column - 1, ""
        | _ -> invalidOp "ShipIt updaters must be a block sequence."
    let missing = projects |> List.filter (fun path -> not (existing.Contains(path, selector)))
    if not missing.IsEmpty then
        let newline = if text.Contains "\r\n" then "\r\n" else "\n"
        let padding = String(' ', indent)
        let entries = missing |> List.map (fun path ->
            let file = relativePath directory path |> fun value -> "'" + value.Replace("'", "''") + "'"
            $"{padding}- xml:{newline}{padding}    file: {file}{newline}{padding}    selector: {selector}{newline}") |> String.concat ""
        let prefix = if header = "" then "" else header + newline
        let result = text.Insert(yaml.Index + insertion, prefix + entries)
        // Parse the result before committing any change.
        let check = YamlStream()
        let checkedFront = Regex.Match(result, "\\A---\\r?\\n(?<yaml>[\\s\\S]*?)^---[ \\t]*(?:\\r?\\n|$)", RegexOptions.Multiline)
        use checkReader = new StringReader(checkedFront.Groups["yaml"].Value)
        check.Load checkReader
        let temporary = changelog + "." + Guid.NewGuid().ToString("N") + ".tmp"
        try
            File.WriteAllText(temporary, result, UTF8Encoding(hasBom))
            File.Replace(temporary, changelog, null)
        finally
            if File.Exists temporary then File.Delete temporary
