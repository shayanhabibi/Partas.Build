/// <summary>
/// The F# side of the fixture. Every shape here is one the generator meets in Partas.Solid: an
/// internally declared attribute alias, ordinary parameter-level injection, and the mangled
/// optional-type-extension setter that neither ReSharper nor the F# compiler treats like a normal
/// member.
/// </summary>
module Fixture.FSharpSurface

open System
open JetBrains.Annotations

/// <summary>
/// Declared here rather than taken from the package, mirroring Partas.Solid's own alias. It is
/// <c>internal</c>: invisible to a consumer's compiler, which is precisely why the annotation has
/// to travel in a sidecar instead.
/// </summary>
[<AttributeUsage(AttributeTargets.All, AllowMultiple = true)>]
type internal InjectAttribute (language: string) =
    inherit Attribute ()

    /// <summary>The language identifier.</summary>
    member _.Language = language

    /// <summary>Text spliced before the value when the fragment is analysed.</summary>
    member val Prefix = "" with get, set

    /// <summary>Text spliced after the value when the fragment is analysed.</summary>
    member val Suffix = "" with get, set

/// <summary>The type the extension members below attach to.</summary>
type Element () =
    /// <summary>The value an extension setter writes.</summary>
    member val Value = "" with get, set

/// <summary>Ordinary static methods, annotated at parameter level.</summary>
type Html =
    /// <summary>Three annotated parameters on one method, each a site of its own.</summary>
    static member Render
        (
            [<LanguageInjection("html")>] markup: string,
            [<LanguageInjection("css", Prefix = "div{", Suffix = "}")>] style: string,
            [<NotNull>] pattern: string
        ) =
        markup + style + pattern

    /// <summary>An overload, so the two must not collapse into one emitted member.</summary>
    static member Render ([<LanguageInjection("js")>] source: string, count: int) = source + string count

/// <summary>A constructor parameter, which is a site on <c>#ctor</c> rather than on the type.</summary>
type Script ([<LanguageInjection("js")>] source: string) =
    /// <summary>The source the constructor was given.</summary>
    member _.Source = source

/// <summary>An indexer parameter, whose owning id carries the index type.</summary>
type Bag () =
    /// <summary>Looks a value up by an annotated key.</summary>
    member _.Item
        with get ([<LanguageInjection("json")>] key: string) = key

/// <summary>Annotated at the module level, which compiles to a static method on a static class.</summary>
[<ContractAnnotation("null => false")>]
let check (s: string) = not (isNull s)

/// <summary>Carries an attribute outside the JetBrains namespace, so the default filter must skip it.</summary>
[<Obsolete("not a JetBrains annotation")>]
let ignored () = ()

/// <summary>
/// The case the whole exercise is for. An F# optional type extension compiles to a setter whose
/// name is mangled, and ReSharper honours a member-level annotation on it while ignoring a
/// parameter-level one - so the generator has to reach it at all, whatever it ends up named.
/// </summary>
[<AutoOpen>]
module Extensions =
    type Element with
        /// <summary>An extension setter annotated on its parameter.</summary>
        member this.style
            with set ([<LanguageInjection("css", Prefix = "div{", Suffix = "}")>] value: string) = this.Value <- value

        /// <summary>An extension setter annotated on the member itself, via the internal alias.</summary>
        [<Inject("html", Prefix = "<div>", Suffix = "</div>")>]
        member this.innerHtml
            with set (value: string) = this.Value <- value
