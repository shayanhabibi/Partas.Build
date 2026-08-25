/// <summary>
/// What a generation run finds, and how it lays a site out in the file.
/// </summary>
/// <remarks>
/// The element shape is not cosmetic: ReSharper reads <c>parameter</c>, <c>return</c> and
/// <c>typeparameter</c> as different sites, so a member-level element where a parameter one was
/// meant annotates the wrong thing while remaining a valid file.
/// </remarks>
module Partas.ExternalAnnotationsTests.GeneratorTests

open System
open Expecto
open Partas.ExternalAnnotations
open Partas.ExternalAnnotationsTests.Helpers

[<Tests>]
let tests =
    testList "generator" [
        testList "counts" [
            test "reports what it scanned, found and emitted" {
                generatingFixture (fun generated ->
                    // These are the fixture's, and change only when someone edits it deliberately.
                    Expect.equal generated.Result.Types 13 "types scanned"
                    Expect.equal generated.Result.Sites 41 "annotated sites found"
                    Expect.equal generated.Result.Members 33 "members emitted"
                    Expect.isEmpty generated.Result.Skipped "a skip means annotations are silently absent"

                    Expect.equal
                        (Xml.members generated.Document).Length
                        generated.Result.Members
                        "the reported member count should be the number of elements written")
            }

            test "more sites than members, because a member can carry several" {
                generatingFixture (fun generated ->
                    Expect.isGreaterThan
                        generated.Result.Sites
                        generated.Result.Members
                        "the fixture has members annotated at more than one site")
            }

            test "the same counts are on the generator itself" {
                use generator = new ExternalAnnotationGenerator (csharpAssembly, fixtureAttributes)

                Expect.equal generator.TypeScanCount 13 "types"
                Expect.equal generator.SiteCount 41 "sites"
                Expect.equal generator.MemberCount 33 "members"
                Expect.isEmpty generator.Skipped "skips"
            }

            test "a filter matching nothing produces an empty but well-formed file" {
                generating (AttributeFilter.Named [ "NothingIsCalledThis" ]) csharpAssembly (fun generated ->
                    Expect.equal generated.Result.Members 0 "no members"
                    Expect.equal generated.Result.Sites 0 "no sites"
                    Expect.isGreaterThan generated.Result.Types 0 "the assembly was still scanned"
                    Expect.isEmpty (Xml.members generated.Document) "no member elements"

                    Expect.equal
                        (Xml.assemblyName generated.Document)
                        "AnnotationsFixture.Cs"
                        "an empty file still has to name its assembly, or it annotates nothing at all")
            }
        ]

        testList "sites" [
            test "an attribute on the member is a direct child" {
                generatingFixture (fun generated ->
                    let memb = Xml.memberNamed "T:Fixture.Surface.Basic" generated.Document

                    Expect.equal (Xml.siteKinds memb) [ "attribute" ] "a member-level attribute sits directly under the member"
                    Expect.equal (Xml.arguments memb) [ "type" ] "with its argument")
            }

            test "two attributes on one parameter share a single parameter element" {
                generatingFixture (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.Basic.Two(System.String)" generated.Document
                    let parameter = Xml.parameter "x" memb

                    // Grouped rather than one element each: what all but a fraction of ReSharper's
                    // own shipped files do.
                    Expect.equal (Xml.arguments parameter).Length 2 "both attributes"
                    Expect.equal (Xml.arguments parameter) [ "a"; "b" ] "in source order")
            }

            test "member, parameter and return are separate sites of one member" {
                generatingFixture (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.Basic.Both(System.String)" generated.Document

                    Expect.equal
                        (Xml.siteKinds memb)
                        [ "attribute"; "parameter"; "return" ]
                        "member attributes come first, then the parameter, then the return"

                    // Each read is of one site: a parameter's attribute must not be visible as the
                    // member's, or an annotation meant for an argument would be read as the whole
                    // method's.
                    Expect.equal (Xml.arguments memb) [ "member" ] "the member's own"
                    Expect.equal (Xml.arguments (Xml.parameter "x" memb)) [ "parameter" ] "the parameter's"
                    Expect.equal (Xml.arguments (Xml.returns memb)) [ "return" ] "the return's")
            }

            test "a return attribute alone still produces a return element" {
                generatingFixture (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.Basic.Returns" generated.Document

                    Expect.equal (Xml.siteKinds memb) [ "return" ] "no member-level element should be invented"
                    Expect.equal (Xml.arguments (Xml.returns memb)) [ "return" ] "the return's attribute")
            }

            test "a generic parameter is a site of its owner" {
                generatingFixture (fun generated ->
                    let ofType = Xml.memberNamed "T:Fixture.Surface.Outer`1" generated.Document
                    let ofMethod = Xml.memberNamed "M:Fixture.Surface.Outer`1.Pick``1(System.Collections.Generic.IList{``0},`0)" generated.Document

                    Expect.equal (Xml.arguments (Xml.typeParameter "TOuter" ofType)) [ "TOuter" ] "a type's parameter"
                    Expect.equal (Xml.arguments (Xml.typeParameter "TMethod" ofMethod)) [ "TMethod" ] "a method's parameter")
            }

            test "a nested type does not repeat the parameters it inherited" {
                generatingFixture (fun generated ->
                    let inner = Xml.memberNamed "T:Fixture.Surface.Outer`1.Inner`1" generated.Document

                    // The CLR gives Inner both parameters, with TOuter's attribute copied onto its
                    // redeclaration. Emitting it here would repeat the outer type's annotation on
                    // every nested type, which is not what the source said.
                    Expect.equal
                        (Xml.siteKinds inner)
                        [ "attribute"; "typeparameter" ]
                        "one type parameter, not two"

                    Expect.equal
                        (Xml.arguments (Xml.typeParameter "TInner" inner))
                        [ "TInner" ]
                        "and it is the one the nested type actually declares")
            }

            test "an indexer's index parameter is annotated on the property and on its accessor" {
                generatingFixture (fun generated ->
                    // Not a quirk of ours: the C# compiler copies a parameter attribute onto the
                    // generated accessor, so both members genuinely carry it.
                    let property = Xml.memberNamed "P:Fixture.Surface.Bag.Item(System.String)" generated.Document
                    let accessor = Xml.memberNamed "M:Fixture.Surface.Bag.get_Item(System.String)" generated.Document

                    Expect.equal (Xml.arguments (Xml.parameter "key" property)) [ "key" ] "on the property"
                    Expect.equal (Xml.arguments (Xml.parameter "key" accessor)) [ "key" ] "and on the accessor"
                    Expect.equal (Xml.siteKinds property) [ "attribute"; "parameter" ] "the property also carries a member-level one")
            }
        ]

        testList "values" [
            test "the constructor overload used is named in full" {
                generatingFixture (fun generated ->
                    let one = Xml.memberNamed "M:Fixture.Surface.Basic.Named" generated.Document
                    let two = Xml.memberNamed "M:Fixture.Surface.Basic.TwoArguments" generated.Document

                    Expect.equal
                        (Xml.ctors one)
                        [ "M:Fixture.Annotations.MarkAttribute.#ctor(System.String)" ]
                        "the one-argument overload"

                    Expect.equal
                        (Xml.ctors two)
                        [ "M:Fixture.Annotations.MarkAttribute.#ctor(System.String,System.Int32)" ]
                        "the two-argument overload, which only the parameter list distinguishes"

                    Expect.equal (Xml.arguments two) [ "two"; "3" ] "both arguments, in order")
            }

            test "named arguments become properties, keeping their names" {
                generatingFixture (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.Basic.Named" generated.Document

                    Expect.equal (Xml.arguments memb) [ "named" ] "the positional argument"
                    Expect.equal (Xml.properties memb) [ "Note", "a note" ] "and the named one")
            }

            test "a collection-valued argument is flattened" {
                generatingFixture (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.Basic.ArrayArgument" generated.Document
                    Expect.equal (Xml.properties memb) [ "Tags", "x,y" ] "elements joined by commas")
            }

            test "an enum argument is written by name, including when it is the default" {
                generatingFixture (fun generated ->
                    let high = Xml.memberNamed "M:Fixture.Surface.Basic.Graded" generated.Document
                    let low = Xml.memberNamed "M:Fixture.Surface.Basic.GradedLow" generated.Document

                    Expect.equal (Xml.arguments high) [ "High" ] "a non-default value"
                    // Zero is both Level.Low and the CLR default, which is exactly when a numeric
                    // fallback would go unnoticed.
                    Expect.equal (Xml.arguments low) [ "Low" ] "and the zero value, by name rather than as 0")
            }

            test "a Type-valued argument is written as a doc id" {
                generatingFixture (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.Basic.Graded" generated.Document

                    Expect.equal
                        (Xml.properties memb)
                        [ "Fallback", "System.Collections.Generic.List`1" ]
                        "a Type argument is named the way the rest of the file names types")
            }

            test "an attribute with no arguments is a bare element" {
                generating AttributeFilter.JetBrains csharpAssembly (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.JetBrainsSurface.Clean(System.Int32)" generated.Document

                    Expect.equal (Xml.ctors memb) [ "M:JetBrains.Annotations.PureAttribute.#ctor" ] "the parameterless constructor"
                    Expect.isEmpty (Xml.arguments memb) "no arguments"
                    Expect.isEmpty (Xml.properties memb) "no properties")
            }

            test "two named arguments on one attribute both survive" {
                generating AttributeFilter.JetBrains csharpAssembly (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.JetBrainsSurface.Inject(System.String)" generated.Document
                    let parameter = Xml.parameter "html" memb

                    Expect.equal (Xml.arguments parameter) [ "html" ] "the language"

                    // Prefix and Suffix are what make an injected fragment parse; losing one leaves
                    // an annotation that looks present and analyses wrongly.
                    Expect.equal
                        (Xml.properties parameter)
                        [ "Prefix", "<div>"; "Suffix", "</div>" ]
                        "both named arguments, unescaped once the file is parsed")
            }

            test "markup in an argument is escaped in the file and round-trips" {
                generating AttributeFilter.JetBrains csharpAssembly (fun generated ->
                    Expect.stringContains generated.Text "&lt;div&gt;" "the raw file should escape it"

                    let memb = Xml.memberNamed "M:Fixture.Surface.JetBrainsSurface.Check(System.String)" generated.Document
                    Expect.equal (Xml.arguments memb) [ "null => false" ] "and parsing should give it back")
            }
        ]

        testList "reference resolution" [
            // A copy of the fixture away from its dependencies: the one reliable way to make
            // resolution fail on purpose, and so the only way to see what a skip looks like.
            let stranded (body: string -> unit) =
                let copy = IO.Path.Combine (IO.Path.GetTempPath (), $"stranded-{Guid.NewGuid():N}.dll")
                IO.File.Copy (csharpAssembly, copy)

                try
                    body copy
                finally
                    try
                        IO.File.Delete copy
                    with _ ->
                        ()

            yield
                testCase "a type whose attributes cannot be resolved is reported, not dropped in silence" (fun () ->
                    stranded (fun copy ->
                        let skipped, members =
                            use generator = new ExternalAnnotationGenerator (copy, AttributeFilter.JetBrains)
                            generator.Skipped, generator.MemberCount

                        // Silence here is the failure this reporting exists to prevent: a green run
                        // and a sidecar quietly missing the annotations of a whole type.
                        Expect.isNonEmpty skipped "the unresolvable type should be reported"

                        Expect.isTrue
                            (skipped |> List.exists (fun (name, _) -> name = "Fixture.Surface.JetBrainsSurface"))
                            "by name"

                        Expect.isTrue
                            (skipped |> List.exists (fun (_, reason) -> reason.Contains "JetBrains.Annotations"))
                            "and with the assembly it could not find"

                        Expect.equal members 0 "and its annotations are indeed absent"))

            yield
                testCase "an explicit probe directory resolves what deps.json cannot" (fun () ->
                    stranded (fun copy ->
                        let skipped, members =
                            use generator =
                                new ExternalAnnotationGenerator (
                                    copy,
                                    AttributeFilter.JetBrains,
                                    [ IO.Path.GetDirectoryName csharpAssembly ]
                                )

                            generator.Skipped, generator.MemberCount

                        Expect.isEmpty skipped "the extra directory should make the reference resolvable"
                        Expect.equal members 5 "and the annotations reappear"))
        ]

        testList "the generator object" [
            test "disposing releases the assembly, so the file can be replaced" {
                let copy = IO.Path.Combine (IO.Path.GetTempPath (), $"annotations-fixture-{Guid.NewGuid():N}.dll")
                IO.File.Copy (csharpAssembly, copy)

                // A generator that holds its PE stream open would make the delete below throw, and a
                // build that regenerates into a project it just built would fail on the next run.
                let read () =
                    // Probing the fixture's own directory: away from its dependencies a copied
                    // assembly loses the types whose attributes cannot be resolved, and this test
                    // is about the file handle, not about that.
                    use generator =
                        new ExternalAnnotationGenerator (copy, fixtureAttributes, [ IO.Path.GetDirectoryName csharpAssembly ])

                    generator.MemberCount

                try
                    Expect.isGreaterThan (read ()) 0 "it should have read something first"
                    IO.File.Delete copy
                    Expect.isFalse (IO.File.Exists copy) "the assembly should be deletable once the generator is disposed"
                finally
                    try
                        IO.File.Delete copy
                    with _ ->
                        ()
            }

            test "generate is generateWith over the whole JetBrains namespace" {
                let directory = IO.Path.Combine (IO.Path.GetTempPath (), $"annotations-{Guid.NewGuid():N}")

                try
                    let viaDefault = generate csharpAssembly (IO.Path.Combine (directory, "default.xml"))
                    let viaFilter = generateWith AttributeFilter.JetBrains [] csharpAssembly (IO.Path.Combine (directory, "explicit.xml"))

                    Expect.equal viaDefault viaFilter "the convenience overload should not differ from the filter it stands for"

                    Expect.equal
                        (IO.File.ReadAllBytes (IO.Path.Combine (directory, "default.xml")))
                        (IO.File.ReadAllBytes (IO.Path.Combine (directory, "explicit.xml")))
                        "byte for byte"
                finally
                    try
                        IO.Directory.Delete (directory, true)
                    with _ ->
                        ()
            }
        ]
    ]
