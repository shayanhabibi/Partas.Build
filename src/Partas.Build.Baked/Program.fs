namespace Partas.Build.Baked

open Partas.Build

type BuildOption<'T> = {
    argument: ActionInput<'T>
    option: ActionInput<'T>
}
/// Aliases will only apply to options;  arguments only present the base name
type BuildOptionInput =
    private
    | Simple of string
    | WithAlias of name: string * aliases: string list

module BuildOptionInput =
    let create (name: string) =
        name.Trim('-')
        |> Simple
    let private normaliseAlias (alias: string) =
        match alias.Replace(' ', '-') with
#if NETSTANDARD2_0
        | s when s.StartsWith("-") -> s
#else
        | s when s.StartsWith('-') -> s
#endif
        | s when s.Length = 1 -> $"-{s}"
        | s -> $"--{s}"
    let withAlias alias (buildOptionInput: BuildOptionInput) =
        let alias = normaliseAlias alias
        match buildOptionInput with
        | Simple name ->
            WithAlias(name, [alias])
        | WithAlias(name, aliases) -> WithAlias(name, alias :: aliases)
    let withAliases aliases (input: BuildOptionInput) =
        let aliases = List.foldBack (fun alias acc -> normaliseAlias alias :: acc) [] aliases
        match input with
        | WithAlias(name, _)
        | Simple name -> WithAlias(name, aliases)
    let createWith name aliases = create name |> withAliases aliases
    let name = function
        | Simple value
        | WithAlias(value, _) -> value
    let aliases = function
        | Simple _ -> []
        | WithAlias(_, values) -> values
    let toArgument<'T> = name >> Input.argument<'T>
    let toArgumentMaybe<'T> = name >> Input.argumentMaybe<'T>
    let private toOptionImpl<'T> (fn: string -> ActionInput<'T>) (input: BuildOptionInput) =
        let aliases: ActionInput<'T> -> ActionInput<'T> =
            List.foldBack (fun alias fn ->
                fn |> Input.alias alias
                ) (aliases input)
        name input
        |> sprintf "--%s"
        |> fn
        |> aliases
    let toOption<'T> input = toOptionImpl Input.option<'T> input
    let toOptionMaybe<'T> input = toOptionImpl Input.optionMaybe<'T> input

module BuildOption =
    let createWith<'T>
        (optMap: ActionInput<'T> -> ActionInput<'T>)
        (argMap: ActionInput<'T> -> ActionInput<'T>)
        (buildOptionInput: BuildOptionInput) =
        {
            argument =
                BuildOptionInput.toArgument buildOptionInput
                |> argMap
            option =
                BuildOptionInput.toOption buildOptionInput
                |> optMap
        }
    let inline create<'T> (buildOptionInput: BuildOptionInput) =
        createWith<'T> id id buildOptionInput
    let inline createMaybe<'T> (input: BuildOptionInput) =
        { argument = BuildOptionInput.toArgumentMaybe<'T> input
          option = BuildOptionInput.toOptionMaybe<'T> input }
    let createForName<'T> (name: string) =
        BuildOptionInput.create name
        |> create<'T>
    let createForNameAliases<'T> (name: string) (aliases: string list) =
        BuildOptionInput.createWith name aliases
        |> create<'T>
    let map<'T> (fn: ActionInput<'T> -> _) { argument = arg; option = opt }: BuildOption<'T> =
        { argument = fn arg; option = fn opt }
    let mapArg<'T> (fn: ActionInput<'T> -> _) buildOption = { buildOption with argument = fn buildOption.argument }
    let mapOpt<'T> (fn: ActionInput<'T> -> _) buildOption = { buildOption with option = fn buildOption.option }

module IO =
    open System.IO
    open System.Text
    open System.Xml
    open System.Xml.Linq

    /// <summary>Writes doc back over the file it was loaded from.</summary>
    /// <remarks>
    /// Not <c>XDocument.Save(path)</c>: that prepends an <c>&lt;?xml ?&gt;</c> declaration no project file has
    /// and encodes with a byte-order mark whether or not the original had one, so a one-line version change
    /// lands in review as a rewrite of the first line and of the file's encoding.
    /// </remarks>
    let save (projPath: string) (doc: XDocument) =
        let hasByteOrderMark =
            use stream = File.OpenRead projPath
            let head = Array.zeroCreate 3
            stream.Read(head, 0, 3) = 3 && head[0] = 0xEFuy && head[1] = 0xBBuy && head[2] = 0xBFuy

        let settings = XmlWriterSettings(OmitXmlDeclaration = true, Indent = false, Encoding = UTF8Encoding hasByteOrderMark)
        use writer = XmlWriter.Create(projPath, settings)
        doc.Save writer

    /// Sets one MSBuild property in the first `PropertyGroup`, adding the element if it is not there yet,
    /// and answers what it held before.
    let writeProperty (propertyGroup: XElement) (property: string) (map: string option -> string) =
        match propertyGroup.Element(XName.Get property) with
        | null ->
            propertyGroup.Add(XElement(XName.Get property, map None))
            None
        | element ->
            let previous = Some element.Value
            element.Value <- map previous
            previous
