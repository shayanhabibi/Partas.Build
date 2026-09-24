namespace Partas.Build

open System
open System.Collections
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.CommandLine
open System.CommandLine.Completions
open System.CommandLine.Invocation

/// <summary>The options selecting machine-readable output, and the JSON description of a command tree.</summary>
/// <remarks>
/// Every command registers <c>--json</c> and <c>--schema</c>; a command that runs pipelines also registers
/// <c>--report</c>. A command that already declares one of these names, or an alias of one, keeps its own option
/// and goes without the library's.
/// <para>
/// Under <c>--json</c>, <c>--explain</c> writes its tree as one JSON document, evaluated statically, and a run
/// writes its <c>RunResult</c> as one line of compact JSON, the last line the run writes to the invocation's
/// output, in place of the timing table. Step output is unaffected, and still reaches the console unless the
/// stage captures or silences it: a consumer reading stdout reads the last line, or reads <c>--report</c>.
/// </para>
/// </remarks>
module MachineOutput =
    /// <summary>The version of every JSON document the library writes.</summary>
    /// <remarks>Incremented on a change that renames or removes a property, or changes a property's type.</remarks>
    [<Literal>]
    let FormatVersion = 1

    /// <summary>A JSON document written by <paramref name="write"/>, as text.</summary>
    let document (indented: bool) (write: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()

        do
            let options = JsonWriterOptions(Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
            use writer = new Utf8JsonWriter(stream, options)
            write writer
            writer.Flush()

        Encoding.UTF8.GetString(stream.ToArray())

    /// Writes <paramref name="value"/> as a JSON string, or <c>null</c> for <c>ValueNone</c>.
    let writeOptionalString (writer: Utf8JsonWriter) (name: string) (value: string voption) =
        match value with
        | ValueSome value -> writer.WriteString(name, value)
        | ValueNone -> writer.WriteNull name

    /// Writes <paramref name="values"/> as a JSON array of strings.
    let writeStrings (writer: Utf8JsonWriter) (name: string) (values: string seq) =
        writer.WriteStartArray name
        for value in values do
            writer.WriteStringValue value
        writer.WriteEndArray()

    let private optionalText (text: string) = if String.IsNullOrWhiteSpace text then ValueNone else ValueSome text

    /// The JSON type name of <paramref name="valueType"/>, with an F# option read as the type it wraps.
    let rec private typeName (valueType: Type) =
        if valueType.IsGenericType && valueType.GetGenericTypeDefinition() = typedefof<option<_>> then
            typeName (valueType.GetGenericArguments()[0])
        elif valueType.IsGenericType && valueType.GetGenericTypeDefinition() = typedefof<voption<_>> then
            typeName (valueType.GetGenericArguments()[0])
        elif valueType = typeof<bool> then "boolean"
        elif valueType = typeof<string> then "string"
        elif valueType = typeof<char> then "string"
        elif valueType.IsEnum then "enum"
        elif
            [ typeof<int>; typeof<int64>; typeof<int16>; typeof<byte>; typeof<uint32>; typeof<uint64> ]
            |> List.contains valueType
        then "integer"
        elif [ typeof<float>; typeof<float32>; typeof<decimal> ] |> List.contains valueType then "number"
        elif valueType = typeof<FileInfo> then "file"
        elif valueType = typeof<DirectoryInfo> then "directory"
        elif valueType = typeof<FileSystemInfo> then "path"
        elif valueType.IsArray then typeName (valueType.GetElementType()) + "[]"
        elif valueType <> typeof<string> && typeof<IEnumerable>.IsAssignableFrom valueType && valueType.IsGenericType then
            typeName (valueType.GetGenericArguments()[0]) + "[]"
        else valueType.Name

    /// Writes a default value: an F# option as its content or <c>null</c>, a sequence as an array, and anything
    /// without a JSON counterpart as its <c>ToString</c>.
    let rec private writeValue (writer: Utf8JsonWriter) (value: obj) =
        match value with
        | null -> writer.WriteNullValue()
        | :? bool as value -> writer.WriteBooleanValue value
        | :? string as value -> writer.WriteStringValue value
        | :? int as value -> writer.WriteNumberValue value
        | :? int64 as value -> writer.WriteNumberValue value
        | :? float as value -> writer.WriteNumberValue value
        | :? decimal as value -> writer.WriteNumberValue value
        | :? FileSystemInfo as value -> writer.WriteStringValue(value.ToString())
        | :? Enum as value -> writer.WriteStringValue(value.ToString())
        | _ ->
            let valueType = value.GetType()

            if valueType.IsGenericType && valueType.GetGenericTypeDefinition() = typedefof<option<_>> then
                writeValue writer (valueType.GetProperty("Value").GetValue value)
            elif valueType.IsGenericType && valueType.GetGenericTypeDefinition() = typedefof<voption<_>> then
                match valueType.GetProperty("IsSome").GetValue value with
                | :? bool as isSome when isSome -> writeValue writer (valueType.GetProperty("Value").GetValue value)
                | _ -> writer.WriteNullValue()
            else
                match value with
                | :? IEnumerable as values ->
                    writer.WriteStartArray()
                    for value in values do
                        writeValue writer value
                    writer.WriteEndArray()
                | _ -> writer.WriteStringValue(value.ToString())

    /// The default of a symbol, evaluated without a parse: a factory that reads the parse it is given fails, and
    /// its default is reported absent.
    let private tryDefault (hasDefault: bool) (getDefault: unit -> obj) =
        if not hasDefault then ValueNone
        else
            try ValueSome(getDefault ()) with _ -> ValueNone

    /// The values a symbol lists for completion, which include those <c>acceptOnlyFromAmong</c> and
    /// <c>mapFromAmong</c> restrict it to. A boolean lists none.
    let private choices (valueType: Type) (complete: unit -> CompletionItem seq) =
        if valueType = typeof<bool> || valueType = typeof<bool option> then []
        else
            try complete () |> Seq.map _.Label |> Seq.distinct |> List.ofSeq with _ -> []

    let private writeArity (writer: Utf8JsonWriter) (arity: ArgumentArity) =
        writer.WriteStartObject "arity"
        writer.WriteNumber("min", arity.MinimumNumberOfValues)

        // System.CommandLine's "unbounded" is a large finite number.
        if arity.MaximumNumberOfValues >= ArgumentArity.ZeroOrMore.MaximumNumberOfValues then writer.WriteNull "max"
        else writer.WriteNumber("max", arity.MaximumNumberOfValues)

        writer.WriteEndObject()

    /// Whether <paramref name="value"/> is <c>null</c>, <c>None</c> or <c>ValueNone</c>.
    let private isAbsent (value: obj) =
        match value with
        | null -> true
        | _ ->
            let valueType = value.GetType()

            valueType.IsGenericType
            && valueType.GetGenericTypeDefinition() = typedefof<voption<_>>
            && not (valueType.GetProperty("IsSome").GetValue value :?> bool)

    /// Writes the default, with a present default of a sensitive symbol written as <c>"***"</c>.
    let private writeDefault (writer: Utf8JsonWriter) (sensitive: bool) (value: obj voption) =
        writer.WriteBoolean("sensitive", sensitive)

        match value with
        | ValueSome value ->
            writer.WriteBoolean("hasDefault", true)
            writer.WritePropertyName "default"
            if sensitive && not (isAbsent value) then writer.WriteStringValue "***" else writeValue writer value
        | ValueNone ->
            writer.WriteBoolean("hasDefault", false)
            writer.WriteNull "default"

    let private writeOption (writer: Utf8JsonWriter) (option: Option) =
        writer.WriteStartObject()
        writer.WriteString("name", option.Name)
        writeStrings writer "aliases" option.Aliases
        writeOptionalString writer "description" (optionalText option.Description)
        writer.WriteString("type", typeName option.ValueType)
        writer.WriteString("clrType", option.ValueType.FullName)
        writer.WriteBoolean("required", option.Required)
        writer.WriteBoolean("hidden", option.Hidden)
        writer.WriteBoolean("recursive", option.Recursive)
        writeArity writer option.Arity
        writeDefault writer (Input.isSensitive option) (tryDefault option.HasDefaultValue option.GetDefaultValue)
        writeStrings writer "choices" (choices option.ValueType (fun () -> option.GetCompletions CompletionContext.Empty))
        writer.WriteEndObject()

    let private writeArgument (writer: Utf8JsonWriter) (argument: Argument) =
        writer.WriteStartObject()
        writer.WriteString("name", argument.Name)
        writeOptionalString writer "description" (optionalText argument.Description)
        writer.WriteString("type", typeName argument.ValueType)
        writer.WriteString("clrType", argument.ValueType.FullName)
        writer.WriteBoolean("hidden", argument.Hidden)
        writeArity writer argument.Arity
        writeDefault writer (Input.isSensitive argument) (tryDefault argument.HasDefaultValue argument.GetDefaultValue)
        writeStrings writer "choices" (choices argument.ValueType (fun () -> argument.GetCompletions CompletionContext.Empty))
        writer.WriteEndObject()

    let rec private writeCommand (writer: Utf8JsonWriter) (command: Command) =
        writer.WriteStartObject()
        writer.WriteString("name", command.Name)
        writeStrings writer "aliases" command.Aliases
        writeOptionalString writer "description" (optionalText command.Description)
        writer.WriteBoolean("hidden", command.Hidden)
        writer.WriteBoolean("runsPipelines", not (isNull command.Action))

        writer.WriteStartArray "options"
        for option in command.Options do
            writeOption writer option
        writer.WriteEndArray()

        writer.WriteStartArray "arguments"
        for argument in command.Arguments do
            writeArgument writer argument
        writer.WriteEndArray()

        writer.WriteStartArray "subcommands"
        for subCommand in command.Subcommands do
            writeCommand writer subCommand
        writer.WriteEndArray()

        writer.WriteEndObject()

    /// <summary><paramref name="command"/> and every command under it, with each option and argument it takes,
    /// as an indented JSON document.</summary>
    /// <remarks>
    /// Each option lists its name, aliases, description, type, whether it is required, hidden or recursive, its
    /// arity, its default and the values it is restricted to. The default of a symbol marked by
    /// <c>Input.sensitive</c> is written as <c>"***"</c>. <c>runsPipelines</c> is <c>false</c> for a command
    /// that only dispatches to its subcommands.
    /// </remarks>
    let schema (command: Command) =
        document true (fun writer ->
            writer.WriteStartObject()
            writer.WriteNumber("formatVersion", FormatVersion)
            writer.WritePropertyName "command"
            writeCommand writer command
            writer.WriteEndObject())

    /// <summary>The <c>--json</c> flag.</summary>
    let json: ActionInput<bool> =
        Input.option<bool> "--json"
        |> Input.description "Write JSON instead of text: the --explain tree, or the run result as the last line of output"
        |> Input.def false

    /// <summary>The <c>--report</c> option: a file the run result is written to as JSON.</summary>
    /// <remarks>Written once the command's pipelines finish, whether they succeeded or not, and never under <c>--explain</c>.</remarks>
    let report: ActionInput<string option> =
        Input.optionMaybe<string> "--report"
        |> Input.description "Write the run result as JSON to this file"
        |> Input.helpName "path"

    /// <summary>The <c>--schema</c> flag, which prints <c>schema</c> for the command it is given to, and exits.</summary>
    let schemaOption: ActionInput<bool> =
        Input.option<bool> "--schema"
        |> Input.description "Print this command, its options and its subcommands as JSON, and exit"
        |> Input.def false
        |> Input.editOption (fun option ->
            option.Action <-
                { new SynchronousCommandLineAction() with
                    member _.Invoke(parseResult: ParseResult) =
                        parseResult.InvocationConfiguration.Output.WriteLine(schema parseResult.CommandResult.Command)
                        0 })

    /// <summary>Whether <paramref name="input"/> is registered on the parsed command and given on the command line
    /// or by default as <c>true</c>.</summary>
    let isSet (input: ActionInput<bool>) (parseResult: ParseResult) =
        match input.Source with
        | ParsedOption option ->
            match parseResult.GetResult option with
            | null -> false
            | _ -> input.GetValue parseResult
        | _ -> false

    /// <summary>The value of <paramref name="input"/> when it is registered on the parsed command.</summary>
    let tryValue (input: ActionInput<'T option>) (parseResult: ParseResult) =
        match input.Source with
        | ParsedOption option ->
            match parseResult.GetResult option with
            | null -> None
            | _ -> input.GetValue parseResult
        | _ -> None

    /// <summary>Whether <paramref name="command"/> already declares the name or an alias of <paramref name="input"/>.</summary>
    let isTaken (command: Command) (input: ActionInput) =
        match input.Source with
        | ParsedOption option ->
            let names = Seq.append [ option.Name ] option.Aliases |> Set.ofSeq
            command.Options
            |> Seq.exists (fun existing -> names.Contains existing.Name || existing.Aliases |> Seq.exists names.Contains)
        | _ -> false
