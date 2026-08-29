/// <summary>
/// Doc ids checked against something other than a string a test author typed.
/// </summary>
/// <remarks>
/// <c>XmlDocId</c> reimplements the ID string format of ECMA-334 Annex E. The C# compiler already
/// implements it, and was asked to emit its answers into the fixture's documentation file, so
/// every id here is compared with Roslyn's rather than with a literal. This catches a whole class
/// of plausible-looking mistake that hand-written expectations do not.
/// </remarks>
module Partas.ExternalAnnotationsTests.OracleTests

open System
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Xml.Linq
open Expecto
open Partas.ExternalAnnotations
open Partas.ExternalAnnotationsTests.Helpers

/// Every id the C# compiler wrote for the fixture.
let private roslynIds =
    let path = Path.ChangeExtension (csharpAssembly, ".xml")

    if not (File.Exists path) then
        failwith $"the C# fixture's documentation file is missing at {path}; GenerateDocumentationFile must stay on for it"

    XDocument.Load(path).Descendants (XName.Get "member")
    |> Seq.map _.Attribute(XName.Get "name").Value
    // Roslyn writes a <typeparam> entry named after the parameter alone; those are not doc ids.
    |> Seq.filter (fun name -> name.Length > 1 && name[1] = ':')
    |> Set.ofSeq

let private declared =
    BindingFlags.Public
    ||| BindingFlags.NonPublic
    ||| BindingFlags.Instance
    ||| BindingFlags.Static
    ||| BindingFlags.DeclaredOnly

let private isCompilerGenerated (memb: MemberInfo) =
    memb.Name.Contains '<'
    || not (isNull (memb.GetCustomAttribute typeof<CompilerGeneratedAttribute>))

/// Accessors are real members and carry real annotations, but the compiler documents the property
/// or event instead, so they have no counterpart to compare against.
let private isAccessor (memb: MemberInfo) =
    match memb with
    | :? MethodBase as methodBase ->
        methodBase.IsSpecialName
        && [ "get_"; "set_"; "add_"; "remove_" ] |> List.exists methodBase.Name.StartsWith
    | _ -> false

/// Every member of the fixture, with the ids this library gives them.
let private ours =
    Set.ofList [
        for ty in typeof<Fixture.Surface.Basic>.Assembly.GetTypes () do
            if not (isCompilerGenerated ty) then
                yield XmlDocId.ofMember ty

                for memb in ty.GetMembers declared do
                    if not (memb :? Type) && not (isCompilerGenerated memb) && not (isAccessor memb) then
                        yield XmlDocId.ofMember memb
    ]

[<Tests>]
let tests =
    testList "oracle" [
        test "every doc id the C# compiler wrote is one this library also produces" {
            // Compared in this direction because the compiler documents only what the source
            // declared, while reflection also reports what the compiler supplied - an implicit
            // default constructor, an enum's value__ field - which have correct ids and no
            // counterpart to compare them against. Every id Roslyn did emit must be reproduced
            // exactly, which is the claim that matters.
            let missing = Set.difference roslynIds ours

            Expect.isEmpty missing $"""ids the compiler emitted that this library does not produce:{"
"}{String.Join ("
", missing)}"""
        }

        test "the comparison covers the whole fixture, not an accidentally empty set" {
            // Without this, anything that emptied either set would turn the test above green.
            Expect.isGreaterThan roslynIds.Count 50 "the ids the compiler emitted"
            Expect.isGreaterThan ours.Count roslynIds.Count "and the wider set this library produces for the same assembly"
        }

        test "every emitted member id is one the compiler recognises" {
            generatingFixture (fun generated ->
                let unknown =
                    Xml.memberNames generated.Document
                    // Accessors are the one thing we name and the compiler does not document.
                    |> List.filter (fun name -> not (name.Contains ".get_" || name.Contains ".set_"))
                    |> List.filter (roslynIds.Contains >> not)

                Expect.isEmpty unknown "an id ReSharper cannot resolve annotates nothing, silently")
        }

        testList "Partas.Solid" [
            // The known-good run this design was settled against. Present only on a machine that
            // has Partas.Solid built beside this repository, so it is skipped rather than failed
            // elsewhere; where it does run it is the strongest evidence available that a change is
            // safe.
            let repositoryRoot =
                let rec search (dir: DirectoryInfo) =
                    if isNull dir then None
                    elif File.Exists (Path.Combine (dir.FullName, "Partas.Build.slnx")) then Some dir.FullName
                    else search dir.Parent

                search (DirectoryInfo AppContext.BaseDirectory)

            let beside (parts: string list) =
                match repositoryRoot with
                | None -> ""
                | Some root -> Path.Combine (DirectoryInfo(root).Parent.FullName :: parts |> Array.ofList)

            let assembly = beside [ "Partas.Solid"; "Partas.Solid"; "bin"; "Release"; "net6.0"; "Partas.Solid.dll" ]
            let expected = beside [ "Partas.Solid"; "ExternalAnnotationsTest"; "lib"; "Partas.Solid.ExternalAnnotations.xml" ]

            yield
                testCase "reproduces the known-good counts" (fun () ->
                    if not (File.Exists assembly) then
                        skiptest $"no Partas.Solid release build at {assembly}"

                    generating AttributeFilter.JetBrains assembly (fun generated ->
                        // The type count is deliberately not asserted: it counts what Partas.Solid
                        // happens to declare, so it moves whenever that repository does, while the
                        // number of annotations found is what this library decides.
                        Expect.equal generated.Result.Sites 598 "annotated sites"
                        Expect.equal generated.Result.Members 598 "members emitted"
                        Expect.isEmpty generated.Result.Skipped "the known-good run skips nothing"))

            yield
                testCase "reproduces the known-good file" (fun () ->
                    if not (File.Exists assembly && File.Exists expected) then
                        skiptest $"no Partas.Solid oracle at {expected}"

                    let normalise (element: XElement) = element.ToString SaveOptions.None

                    let oracle =
                        XDocument.Load(expected).Root.Elements (XName.Get "member")
                        |> Seq.map normalise
                        |> Set.ofSeq

                    generating AttributeFilter.JetBrains assembly (fun generated ->
                        // A subset rather than an equality: the oracle carries one hand-added probe
                        // entry that the generator does not and should not emit. Every member it
                        // does emit has to match that file exactly, element for element.
                        let differing =
                            generated.Document.Root.Elements (XName.Get "member")
                            |> Seq.map normalise
                            |> Seq.filter (oracle.Contains >> not)
                            |> List.ofSeq

                        Expect.isEmpty
                            differing
                            $"""members that differ from the file proven to work in Rider:{"
"}{String.Join ("
", List.truncate 3 differing)}"""))
        ]
    ]
