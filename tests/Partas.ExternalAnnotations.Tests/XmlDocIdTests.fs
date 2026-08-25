/// <summary>
/// <c>XmlDocId</c> in isolation, over the fixture types loaded by ordinary reflection.
/// </summary>
/// <remarks>
/// These ids are the whole contract with ReSharper: a wrong one produces a file that parses, loads
/// and silently annotates nothing. The literals here are what a doc id looks like; that they are
/// also what the C# compiler emits is asserted separately, in <c>OracleTests</c>.
/// </remarks>
module Partas.ExternalAnnotationsTests.XmlDocIdTests

open System
open System.Reflection
open Expecto
open Partas.ExternalAnnotations
open Fixture.Surface

let private declared =
    BindingFlags.Public
    ||| BindingFlags.NonPublic
    ||| BindingFlags.Instance
    ||| BindingFlags.Static
    ||| BindingFlags.DeclaredOnly

/// The single method of <paramref name="ty"/> matching <paramref name="predicate"/>. Overloads mean
/// a name is not enough, and a silent pick of the wrong one would assert the wrong id.
let private methodWhere (ty: Type) (predicate: MethodInfo -> bool) =
    match ty.GetMethods declared |> Array.filter predicate with
    | [| found |] -> found :> MemberInfo
    | found -> failwith $"expected one method on {ty.Name}, found {found.Length}"

let private method' (ty: Type) (name: string) = methodWhere ty (fun m -> m.Name = name)

let private ctorWhere (ty: Type) (predicate: ConstructorInfo -> bool) =
    match ty.GetConstructors declared |> Array.filter predicate with
    | [| found |] -> found :> MemberInfo
    | found -> failwith $"expected one constructor on {ty.Name}, found {found.Length}"

let private outer = typedefof<Outer<obj>>
let private inner = outer.GetNestedType ("Inner`1", BindingFlags.Public)

[<Tests>]
let tests =
    testList "xml doc id" [
        testList "types" [
            test "a type is its namespace and name" {
                Expect.equal (XmlDocId.ofMember typeof<Basic>) "T:Fixture.Surface.Basic" "namespace and name should be joined by a dot"
            }

            test "a type outside any namespace carries no leading dot" {
                Expect.equal (XmlDocId.ofMember typeof<GlobalScope>) "T:GlobalScope" "an empty namespace should contribute nothing"
            }

            test "a generic type declares its arity" {
                Expect.equal (XmlDocId.ofMember outer) "T:Fixture.Surface.Outer`1" "the arity should replace the CLR name's suffix"
            }

            test "a nested generic counts only the parameters it adds" {
                // Inner has two generic arguments as far as the CLR is concerned; one of them is
                // Outer's, already accounted for by the `1 on Outer.
                Expect.equal
                    (XmlDocId.ofMember inner)
                    "T:Fixture.Surface.Outer`1.Inner`1"
                    "the enclosing type's parameters should not be counted twice"
            }

            test "a declaration names its parameters by arity, a reference by position" {
                Expect.equal (XmlDocId.typeDecl outer) "Fixture.Surface.Outer`1" "a declaration counts"
                Expect.equal (XmlDocId.typeRef outer) "Fixture.Surface.Outer{`0}" "a reference substitutes"
            }

            test "a generic parameter is referenced by position and owner kind" {
                let typeParameter = outer.GetGenericArguments () |> Array.exactlyOne

                let methodParameter =
                    (outer.GetMethod ("Pick", declared)).GetGenericArguments () |> Array.exactlyOne

                Expect.equal (XmlDocId.typeRef typeParameter) "`0" "a type's parameter takes one backtick"
                Expect.equal (XmlDocId.typeRef methodParameter) "``0" "a method's parameter takes two"
            }
        ]

        testList "members" [
            test "a field, an event and a property take their own prefixes" {
                Expect.equal
                    (XmlDocId.ofMember (typeof<Basic>.GetField "Field"))
                    "F:Fixture.Surface.Basic.Field"
                    "a field should be F:"

                Expect.equal
                    (XmlDocId.ofMember (typeof<Basic>.GetEvent "Changed"))
                    "E:Fixture.Surface.Basic.Changed"
                    "an event should be E:"

                Expect.equal
                    (XmlDocId.ofMember (typeof<Basic>.GetProperty "Property"))
                    "P:Fixture.Surface.Basic.Property"
                    "a property should be P:"
            }

            test "an indexer carries its index parameters" {
                Expect.equal
                    (XmlDocId.ofMember (typeof<Bag>.GetProperty "Item"))
                    "P:Fixture.Surface.Bag.Item(System.String)"
                    "a property with index parameters should list them"
            }

            test "a constructor is #ctor and a static one is #cctor" {
                Expect.equal
                    (XmlDocId.ofMember (ctorWhere typeof<Basic> (fun c -> not c.IsStatic && c.GetParameters().Length = 0)))
                    "M:Fixture.Surface.Basic.#ctor"
                    "the parameterless constructor"

                Expect.equal
                    (XmlDocId.ofMember (ctorWhere typeof<Basic> (fun c -> c.GetParameters().Length = 1)))
                    "M:Fixture.Surface.Basic.#ctor(System.String)"
                    "the overload should be distinguished by its parameters"

                Expect.equal
                    (XmlDocId.ofMember typeof<Basic>.TypeInitializer)
                    "M:Fixture.Surface.Basic.#cctor"
                    "the static constructor takes a different name entirely"
            }

            test "overloads are distinguished by their parameter lists alone" {
                let ofInt = methodWhere typeof<Signatures> (fun m -> m.Name = "Overloaded" && m.GetParameters().[0].ParameterType = typeof<int>)
                let ofString = methodWhere typeof<Signatures> (fun m -> m.Name = "Overloaded" && m.GetParameters().[0].ParameterType = typeof<string>)

                Expect.equal (XmlDocId.ofMember ofInt) "M:Fixture.Surface.Signatures.Overloaded(System.Int32)" "the int overload"
                Expect.equal (XmlDocId.ofMember ofString) "M:Fixture.Surface.Signatures.Overloaded(System.String)" "the string overload"
            }

            test "an explicit interface implementation has its dots mangled" {
                let explicitImpl = method' typeof<Thing> "Fixture.Surface.IThing.Do"

                Expect.equal
                    (XmlDocId.ofMember explicitImpl)
                    "M:Fixture.Surface.Thing.Fixture#Surface#IThing#Do"
                    "dots inside a member name should become hashes, leaving the separator unambiguous"
            }

            test "a conversion operator ends in its return type" {
                let implicitOp = method' typeof<Signatures> "op_Implicit"
                let explicitOp = method' typeof<Signatures> "op_Explicit"

                // The return type is part of the signature here and nowhere else: two conversions
                // differing only in what they convert *to* are legal.
                Expect.equal
                    (XmlDocId.ofMember implicitOp)
                    "M:Fixture.Surface.Signatures.op_Implicit(Fixture.Surface.Signatures)~System.String"
                    "op_Implicit"

                Expect.equal
                    (XmlDocId.ofMember explicitOp)
                    "M:Fixture.Surface.Signatures.op_Explicit(Fixture.Surface.Signatures)~System.Int32"
                    "op_Explicit"
            }
        ]

        testList "parameter types" [
            test "arrays are spelled by rank" {
                Expect.equal
                    (XmlDocId.ofMember (method' typeof<Signatures> "Arrays"))
                    "M:Fixture.Surface.Signatures.Arrays(System.Int32[],System.Int32[0:,0:],System.String[][])"
                    "vector, rectangular and jagged arrays each have their own spelling"
            }

            test "by-reference parameters take a suffix, whether in or out" {
                Expect.equal
                    (XmlDocId.ofMember (method' typeof<Signatures> "ByRef"))
                    "M:Fixture.Surface.Signatures.ByRef(System.Int32@,System.Int32@)"
                    "ref and out are the same thing in metadata"
            }

            test "a pointer takes a star" {
                Expect.equal
                    (XmlDocId.ofMember (method' typeof<Signatures> "Pointer"))
                    "M:Fixture.Surface.Signatures.Pointer(System.Int32*)"
                    "a pointer parameter"
            }

            test "a closed generic nests its arguments in braces" {
                Expect.equal
                    (XmlDocId.ofMember (method' typeof<Signatures> "Closed"))
                    "M:Fixture.Surface.Signatures.Closed(System.Collections.Generic.Dictionary{System.String,System.Collections.Generic.List{System.Int32}})"
                    "braces nest, and arguments are comma-separated"
            }

            test "a generic method numbers its own parameters apart from its type's" {
                Expect.equal
                    (XmlDocId.ofMember (method' outer "Pick"))
                    "M:Fixture.Surface.Outer`1.Pick``1(System.Collections.Generic.IList{``0},`0)"
                    "the method's parameter and its type's are both position 0, told apart by the backticks"
            }

            test "a nested type's method sees the whole nesting's parameters in order" {
                Expect.equal
                    (XmlDocId.ofMember (method' inner "Use"))
                    "M:Fixture.Surface.Outer`1.Inner`1.Use(`0,`1)"
                    "the enclosing type's parameter is 0 and the nested type's is 1"
            }
        ]
    ]
