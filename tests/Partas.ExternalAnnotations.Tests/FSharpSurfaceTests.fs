/// <summary>
/// The F#-shaped assembly, which is the case this generator exists for.
/// </summary>
/// <remarks>
/// An F# optional type extension compiles to a static method with a mangled name on a nested module
/// type, bearing no resemblance to what was written. ReSharper honours a <b>member</b>-level
/// annotation on it and ignores a parameter-level one, so the ids here are load-bearing: this is
/// the shape Partas.Solid has 596 of.
/// </remarks>
module Partas.ExternalAnnotationsTests.FSharpSurfaceTests

open Expecto
open Partas.ExternalAnnotations
open Partas.ExternalAnnotationsTests.Helpers

let private extensionSetter =
    "M:Fixture.FSharpSurface.Extensions.Element#set_style(Fixture.FSharpSurface.Element,System.String)"

[<Tests>]
let tests =
    testList "f# surface" [
        test "finds every JetBrains annotation the assembly carries" {
            generating AttributeFilter.JetBrains fsharpAssembly (fun generated ->
                Expect.equal generated.Result.Sites 9 "annotated sites"
                Expect.equal generated.Result.Members 7 "members emitted"
                Expect.isEmpty generated.Result.Skipped "an F#-compiled member must not defeat the doc-id writer"

                Expect.equal (Xml.assemblyName generated.Document) "AnnotationsFixture.Fs" "the assembly name")
        }

        test "a module-level function is a static method of its module" {
            generating AttributeFilter.JetBrains fsharpAssembly (fun generated ->
                let memb = Xml.memberNamed "M:Fixture.FSharpSurface.check(System.String)" generated.Document

                Expect.equal
                    (Xml.ctors memb)
                    [ "M:JetBrains.Annotations.ContractAnnotationAttribute.#ctor(System.String)" ]
                    "the attribute on a let-bound function reaches the compiled method"

                Expect.equal (Xml.arguments memb) [ "null => false" ] "with its argument intact")
        }

        test "several annotated parameters of one method are separate sites" {
            generating AttributeFilter.JetBrains fsharpAssembly (fun generated ->
                let memb =
                    Xml.memberNamed "M:Fixture.FSharpSurface.Html.Render(System.String,System.String,System.String)" generated.Document

                Expect.equal
                    (Xml.siteKinds memb)
                    [ "parameter"; "parameter"; "parameter" ]
                    "three parameters, three elements, in declaration order"

                Expect.equal (Xml.arguments (Xml.parameter "markup" memb)) [ "html" ] "the first"
                Expect.equal (Xml.properties (Xml.parameter "style" memb)) [ "Prefix", "div{"; "Suffix", "}" ] "the second, with its named arguments"

                Expect.equal
                    (Xml.ctors (Xml.parameter "pattern" memb))
                    [ "M:JetBrains.Annotations.NotNullAttribute.#ctor" ]
                    "the third, a different attribute entirely")
        }

        test "an overload of the same method is a member of its own" {
            generating AttributeFilter.JetBrains fsharpAssembly (fun generated ->
                let names = Xml.memberNames generated.Document

                Expect.contains names "M:Fixture.FSharpSurface.Html.Render(System.String,System.Int32)" "the two-parameter overload"

                Expect.contains
                    names
                    "M:Fixture.FSharpSurface.Html.Render(System.String,System.String,System.String)"
                    "and the three-parameter one, which must not collapse into it")
        }

        test "a constructor parameter is a site on #ctor, not on the type" {
            generating AttributeFilter.JetBrains fsharpAssembly (fun generated ->
                let memb = Xml.memberNamed "M:Fixture.FSharpSurface.Script.#ctor(System.String)" generated.Document

                Expect.equal (Xml.arguments (Xml.parameter "source" memb)) [ "js" ] "the constructor's parameter"

                Expect.isFalse
                    (Xml.memberNames generated.Document |> List.contains "T:Fixture.FSharpSurface.Script")
                    "the type itself carries nothing")
        }

        test "an F# indexer is annotated on the property and on its accessor" {
            generating AttributeFilter.JetBrains fsharpAssembly (fun generated ->
                let property = Xml.memberNamed "P:Fixture.FSharpSurface.Bag.Item(System.String)" generated.Document
                let accessor = Xml.memberNamed "M:Fixture.FSharpSurface.Bag.get_Item(System.String)" generated.Document

                Expect.equal (Xml.arguments (Xml.parameter "key" property)) [ "json" ] "on the property"
                Expect.equal (Xml.arguments (Xml.parameter "key" accessor)) [ "json" ] "and on the accessor the compiler generated")
        }

        test "a mangled extension setter is reached at parameter level" {
            generating AttributeFilter.JetBrains fsharpAssembly (fun generated ->
                let memb = Xml.memberNamed extensionSetter generated.Document

                // The compiled member is a static method taking the extended type as its first
                // argument, under the enclosing module, with the extended type's name folded into
                // the method name. None of that is visible in the source.
                Expect.equal (Xml.arguments (Xml.parameter "value" memb)) [ "css" ] "the injected language"
                Expect.equal (Xml.properties (Xml.parameter "value" memb)) [ "Prefix", "div{"; "Suffix", "}" ] "and its named arguments")
        }

        test "a mangled extension setter is reached at member level, which is the site that works" {
            // ReSharper injects from a member-level annotation on one of these and ignores a
            // parameter-level one, so this is the case the whole exercise turns on.
            generating (AttributeFilter.Named [ "InjectAttribute" ]) fsharpAssembly (fun generated ->
                Expect.equal generated.Result.Members 1 "exactly the one member carrying it"

                let memb =
                    Xml.memberNamed
                        "M:Fixture.FSharpSurface.Extensions.Element#set_innerHtml(Fixture.FSharpSurface.Element,System.String)"
                        generated.Document

                Expect.equal (Xml.siteKinds memb) [ "attribute" ] "member level, not parameter level"

                Expect.equal
                    (Xml.ctors memb)
                    [ "M:Fixture.FSharpSurface.InjectAttribute.#ctor(System.String)" ]
                    "an attribute declared internal in the assembly being scanned"

                Expect.equal (Xml.arguments memb) [ "html" ] "the language"
                Expect.equal (Xml.properties memb) [ "Prefix", "<div>"; "Suffix", "</div>" ] "and both named arguments")
        }

        test "an attribute outside the JetBrains namespace is left alone" {
            generating AttributeFilter.JetBrains fsharpAssembly (fun generated ->
                Expect.isFalse
                    (Xml.memberNames generated.Document |> List.contains "M:Fixture.FSharpSurface.ignored")
                    "the [<Obsolete>] function should not appear")
        }
    ]
