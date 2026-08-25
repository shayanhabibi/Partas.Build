/// <summary>
/// Locates the fixture assemblies and reduces a generated annotations file to something a test can
/// assert on.
/// </summary>
/// <remarks>
/// Generation always goes to a real file in a throwaway directory, never to an in-memory document:
/// the encoding, the byte-order mark and the directory creation are all part of what
/// <c>generateWith</c> promises, and none of them are observable from an <c>XDocument</c>.
/// </remarks>
module Partas.ExternalAnnotationsTests.Helpers

open System
open System.IO
open System.Xml.Linq
open Partas.ExternalAnnotations

/// <summary>The C# fixture, which covers every construct the doc-id writer has to name.</summary>
let csharpAssembly = typeof<Fixture.Surface.Basic>.Assembly.Location

/// <summary>The F# fixture, which covers the shapes Partas.Solid actually has.</summary>
let fsharpAssembly = typeof<Fixture.FSharpSurface.Element>.Assembly.Location

/// <summary>
/// The attributes the C# fixture declares itself, as opposed to the JetBrains ones it also carries.
/// </summary>
let fixtureAttributes = AttributeFilter.Where (fun ns _ -> ns = "Fixture.Annotations")

module Xml =
    let private named (name: string) (element: XElement) = element.Elements (XName.Get name)
    let private attr (name: string) (element: XElement) = element.Attribute(XName.Get name).Value

    /// <summary>The assembly the file names, which is what pairs the sidecar with a DLL.</summary>
    let assemblyName (doc: XDocument) = attr "name" doc.Root

    /// <summary>Every <c>member</c> element, in document order, paired with its doc id.</summary>
    let members (doc: XDocument) = [ for element in named "member" doc.Root -> attr "name" element, element ]

    /// <summary>The doc ids the file annotates, in document order.</summary>
    let memberNames (doc: XDocument) = members doc |> List.map fst

    /// <summary>
    /// The one <c>member</c> element for <paramref name="docId"/>. Failing loudly beats an empty
    /// sequence: a test that silently asserts nothing about a missing member is worse than no test.
    /// </summary>
    let memberNamed (docId: string) (doc: XDocument) =
        match members doc |> List.filter (fst >> (=) docId) with
        | [ _, element ] -> element
        | [] -> failwith $"no member '{docId}' in the generated file; it has {(members doc).Length} members"
        | duplicates -> failwith $"{duplicates.Length} members named '{docId}'; doc ids must be unique"

    /// <summary>The <c>ctor</c> of every attribute directly on a site, in order.</summary>
    let ctors (site: XElement) = [ for element in named "attribute" site -> attr "ctor" element ]

    /// <summary>The positional arguments of a site's attributes, flattened in order.</summary>
    let arguments (site: XElement) = [
        for attribute in named "attribute" site do
            for element in named "argument" attribute -> element.Value
    ]

    /// <summary>The named arguments of a site's attributes, flattened in order.</summary>
    let properties (site: XElement) = [
        for attribute in named "attribute" site do
            for element in named "property" attribute -> attr "name" element, element.Value
    ]

    /// <summary>The <c>parameter</c> element for <paramref name="name"/>.</summary>
    let parameter (name: string) (memb: XElement) =
        match named "parameter" memb |> Seq.filter (attr "name" >> (=) name) |> List.ofSeq with
        | [ element ] -> element
        | found -> failwith $"expected one parameter '{name}', found {found.Length}"

    /// <summary>The <c>typeparameter</c> element for <paramref name="name"/>.</summary>
    let typeParameter (name: string) (memb: XElement) =
        match named "typeparameter" memb |> Seq.filter (attr "name" >> (=) name) |> List.ofSeq with
        | [ element ] -> element
        | found -> failwith $"expected one typeparameter '{name}', found {found.Length}"

    /// <summary>The <c>return</c> element.</summary>
    let returns (memb: XElement) =
        match named "return" memb |> List.ofSeq with
        | [ element ] -> element
        | found -> failwith $"expected one return element, found {found.Length}"

    /// <summary>The element names directly under a member, which is how site order is asserted.</summary>
    let siteKinds (memb: XElement) = [ for element in memb.Elements () -> element.Name.LocalName ]

/// <summary>What one generation run produced: the counts, the parsed file, and the file itself.</summary>
type Generated =
    { Result: GenerateResult
      Document: XDocument
      Path: string }

    member this.Bytes = File.ReadAllBytes this.Path
    member this.Text = File.ReadAllText this.Path

/// <summary>
/// Runs a real generation into a fresh directory and hands the result to
/// <paramref name="body"/>, cleaning up afterwards.
/// </summary>
/// <remarks>
/// The directory is created only by <c>generateWith</c>, never here, because creating the output's
/// parent is one of the things being tested.
/// </remarks>
let generating (filter: AttributeFilter) (assembly: string) (body: Generated -> 'a) : 'a =
    let root = Path.Combine (Path.GetTempPath (), "partas-annotations-tests", Guid.NewGuid().ToString "N")

    try
        // Two levels below an absent root, so the directory creation has something to do.
        let output = Path.Combine (root, "nested", "annotations.xml")
        let result = generateWith filter [] assembly output

        body
            { Result = result
              Document = XDocument.Load output
              Path = output }
    finally
        try
            Directory.Delete (root, true)
        with _ ->
            ()

/// <summary>The same, for the common case of the C# fixture's own attributes.</summary>
let generatingFixture body = generating fixtureAttributes csharpAssembly body
