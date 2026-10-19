namespace Partas.Build

open System.ComponentModel

/// <summary>Where an exception raised while a command builds its pipelines came from.</summary>
/// <remarks>
/// The stage builder and the input readers record their names on the exception's <c>Data</c> and rethrow it
/// unchanged: its type, message and stack trace remain those of the original exception.
/// </remarks>
[<RequireQualifiedAccess>]
module Construction =
    [<Literal>]
    let private StagesKey = "Partas.Build.Construction.Stages"

    [<Literal>]
    let private InputKey = "Partas.Build.Construction.Input"

    let private tryRead (key: string) (error: exn) =
        try error.Data[key] with _ -> null

    let private tryWrite (key: string) (value: obj) (error: exn) =
        try error.Data[key] <- value with _ -> ()

    /// <summary>The names of the stages being built when the exception was raised, outermost first.</summary>
    /// <remarks>Empty for an exception raised outside every stage body.</remarks>
    let stagePath (error: exn) : string list =
        match tryRead StagesKey error with
        | :? (string array) as names -> List.ofArray names
        | _ -> []

    /// <summary>The name of the input being read when the exception was raised.</summary>
    let inputName (error: exn) : string voption =
        match tryRead InputKey error with
        | :? string as name -> ValueSome name
        | _ -> ValueNone

    /// <summary>Runs a stage body, recording the stage's name on any exception the body raises.</summary>
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    let inStage (name: string) (build: unit -> 'T) : 'T =
        try
            build ()
        with error ->
            tryWrite StagesKey (Array.ofList (name :: stagePath error)) error
            reraise ()

    /// <summary>Reads an input's value, recording the input's name on any exception the read raises.</summary>
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    let readingInput (name: string) (read: unit -> 'T) : 'T =
        try
            read ()
        with error ->
            if (inputName error).IsNone then tryWrite InputKey name error
            reraise ()
