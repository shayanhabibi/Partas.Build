/// <summary>
/// <c>AttributeFilter</c>, both as the predicate it flattens to and as the selection a real run
/// makes.
/// </summary>
/// <remarks>
/// The predicate is used twice per attribute - once over reflected <c>CustomAttributeData</c> and
/// once over the raw metadata blob - and a hit is located in the blob by its index among the
/// matches at that site. Two predicates that disagree would read a neighbouring attribute's named
/// arguments, so the flattening is worth pinning on its own.
/// </remarks>
module Partas.ExternalAnnotationsTests.AttributeFilterTests

open Expecto
open Partas.ExternalAnnotations
open Partas.ExternalAnnotationsTests.Helpers

let private jetbrains = AttributeFilter.predicate AttributeFilter.JetBrains

[<Tests>]
let tests =
    testList "attribute filter" [
        testList "predicate" [
            test "the default takes the JetBrains namespace whatever the attribute is called" {
                Expect.isTrue (jetbrains "JetBrains.Annotations" "NotNullAttribute") "a known attribute"
                Expect.isTrue (jetbrains "JetBrains.Annotations" "SomethingInventedLater") "an unknown one, which is the point of taking the namespace"
            }

            test "the default rejects the same name in another namespace" {
                Expect.isFalse (jetbrains "System.Diagnostics.CodeAnalysis" "NotNullAttribute") "the namespace decides, not the name"
                Expect.isFalse (jetbrains "JetBrains.Annotations.Internal" "NotNullAttribute") "a child namespace is a different namespace"
                Expect.isFalse (jetbrains "" "NotNullAttribute") "a namespace-less attribute"
            }

            test "Named takes the name whatever the namespace" {
                let predicate = AttributeFilter.predicate (AttributeFilter.Named [ "InjectAttribute"; "MarkAttribute" ])

                Expect.isTrue (predicate "Anything" "InjectAttribute") "the namespace is ignored"
                Expect.isTrue (predicate "" "MarkAttribute") "including no namespace at all"
                Expect.isFalse (predicate "JetBrains.Annotations" "NotNullAttribute") "a name not in the list"
                Expect.isFalse (predicate "Anything" "Inject") "the match is exact, not by suffix"
            }

            test "Named with no names selects nothing" {
                let predicate = AttributeFilter.predicate (AttributeFilter.Named [])
                Expect.isFalse (predicate "JetBrains.Annotations" "NotNullAttribute") "an empty list is empty, not absent"
            }

            test "Where is handed both halves of the name" {
                let seen = ResizeArray ()

                let predicate =
                    AttributeFilter.predicate (
                        AttributeFilter.Where (fun ns name ->
                            seen.Add (ns, name)
                            ns = "A" && name = "B")
                    )

                Expect.isTrue (predicate "A" "B") "the predicate's own answer should be used"
                Expect.isFalse (predicate "A" "C") "and so should its refusal"
                Expect.sequenceEqual seen [ "A", "B"; "A", "C" ] "namespace first, then simple name"
            }

            test "the JetBrains namespace literal is the one the default matches" {
                Expect.isTrue (jetbrains AttributeFilter.JetBrainsNamespace "Anything") "the literal should not drift from the case that uses it"
            }
        ]

        testList "selection" [
            test "the default collects the JetBrains attributes and nothing else" {
                generating AttributeFilter.JetBrains csharpAssembly (fun generated ->
                    Expect.equal generated.Result.Members 5 "five members carry a JetBrains annotation"
                    Expect.equal generated.Result.Sites 5 "one site each"
                    Expect.isEmpty generated.Result.Skipped "nothing should be skipped"

                    Expect.all
                        (Xml.memberNames generated.Document)
                        (fun name -> name.Contains "JetBrainsSurface")
                        "only the type carrying JetBrains annotations should appear")
            }

            test "a narrowed filter drops what it does not name" {
                generating (AttributeFilter.Named [ "GradeAttribute" ]) csharpAssembly (fun generated ->
                    Expect.equal
                        (Xml.memberNames generated.Document)
                        [ "M:Fixture.Surface.Basic.Graded"; "M:Fixture.Surface.Basic.GradedLow" ]
                        "only the two members carrying that attribute"

                    Expect.equal generated.Result.Sites 2 "and only their sites")
            }

            test "an attribute nothing selects is never emitted" {
                generatingFixture (fun generated ->
                    // Basic.Unselected carries [Obsolete], and nothing else.
                    Expect.isFalse
                        (Xml.memberNames generated.Document |> List.contains "M:Fixture.Surface.Basic.Unselected")
                        "a member whose only attribute is unselected should not appear at all"

                    Expect.isFalse
                        (generated.Text.Contains "Obsolete")
                        "no trace of an unselected attribute should reach the file")
            }

            test "a member carrying nothing is never emitted" {
                generatingFixture (fun generated ->
                    Expect.isFalse
                        (Xml.memberNames generated.Document |> List.contains "M:Fixture.Surface.Basic.Bare")
                        "an unannotated member has nothing to say")
            }

            test "an internal attribute is collected, because that is what a sidecar is for" {
                generating (AttributeFilter.Named [ "SecretAttribute" ]) csharpAssembly (fun generated ->
                    let memb = Xml.memberNamed "M:Fixture.Surface.Basic.Hidden" generated.Document

                    Expect.equal
                        (Xml.ctors memb)
                        [ "M:Fixture.Annotations.SecretAttribute.#ctor(System.String)" ]
                        "visibility should not affect collection: a consumer's compiler cannot see this, which is the reason to ship it"

                    Expect.equal (Xml.arguments memb) [ "hidden" ] "its argument should survive")
            }
        ]
    ]
