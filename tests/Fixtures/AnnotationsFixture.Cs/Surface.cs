using System;
using System.Collections.Generic;
using Fixture.Annotations;
using JetBrains.Annotations;

/// <summary>A type outside any namespace, so <c>typeDecl</c> has to cope with a null namespace.</summary>
[Mark("global")]
public class GlobalScope
{
    /// <summary>A member of a namespace-less type.</summary>
    [Mark("global member")]
    public void M()
    {
    }
}

namespace Fixture.Surface
{
    /// <summary>Member-level and parameter-level attributes on ordinary, non-generic signatures.</summary>
    [Mark("type")]
    public class Basic
    {
        /// <summary>A field.</summary>
        [Mark("field")]
        public int Field;

        /// <summary>An event.</summary>
        [Mark("event")]
        public event EventHandler Changed;

        /// <summary>A property.</summary>
        [Mark("property")]
        public string Property { get; set; }

        /// <summary>An instance constructor.</summary>
        [Mark("ctor")]
        public Basic()
        {
        }

        /// <summary>A constructor overload, so the emitted id has to carry its parameters.</summary>
        [Mark("ctor overload")]
        public Basic(string name)
        {
            Property = name;
        }

        /// <summary>A static constructor, which is named <c>#cctor</c> rather than <c>#ctor</c>.</summary>
        [Mark("cctor")]
        static Basic()
        {
        }

        /// <summary>One parameter carrying two attributes, which share a single emitted element.</summary>
        [Mark("two on one parameter")]
        public void Two([Mark("a"), Mark("b")] string x)
        {
            _ = x;
        }

        /// <summary>An attribute on the return, which is a site of its own.</summary>
        [return: Mark("return")]
        public string Returns()
        {
            return "";
        }

        /// <summary>Attributes on both the return and a parameter of the same method.</summary>
        [Mark("member")]
        [return: Mark("return")]
        public string Both([Mark("parameter")] string x)
        {
            return x;
        }

        /// <summary>The second constructor overload of the fixture attribute.</summary>
        [Mark("two", 3)]
        public void TwoArguments()
        {
        }

        /// <summary>A plain named argument.</summary>
        [Mark("named", Note = "a note")]
        public void Named()
        {
        }

        /// <summary>A named argument whose value is a collection.</summary>
        [Mark("array", Tags = new[] { "x", "y" })]
        public void ArrayArgument()
        {
        }

        /// <summary>An enum-valued argument and a <see cref="Type"/>-valued named one.</summary>
        [Grade(Level.High, Fallback = typeof(List<int>))]
        public void Graded()
        {
        }

        /// <summary>An enum argument that happens to be the CLR default, which must still name itself.</summary>
        [Grade(Level.Low)]
        public void GradedLow()
        {
        }

        /// <summary>An attribute that is invisible to a consumer's compiler.</summary>
        [Secret("hidden")]
        public void Hidden()
        {
        }

        /// <summary>Carries an attribute no filter here selects, so it must never be emitted.</summary>
        [Obsolete("not a fixture attribute")]
        public void Unselected()
        {
        }

        /// <summary>Carries nothing at all.</summary>
        public void Bare()
        {
        }
    }

    /// <summary>Signatures whose parameter types stress <c>typeRef</c>.</summary>
    public class Signatures
    {
        /// <summary>Vector, rectangular and jagged arrays, which are spelled three different ways.</summary>
        [Mark("arrays")]
        public void Arrays(int[] vector, int[,] rectangular, string[][] jagged)
        {
            _ = (vector, rectangular, jagged);
        }

        /// <summary>By-reference parameters, in and out, which share one spelling.</summary>
        [Mark("byref")]
        public void ByRef(ref int taken, out int given)
        {
            given = taken;
        }

        /// <summary>A pointer parameter.</summary>
        [Mark("pointer")]
        public unsafe void Pointer(int* p)
        {
            _ = p;
        }

        /// <summary>A closed generic parameter type, which nests braces inside the id.</summary>
        [Mark("closed generic")]
        public void Closed(Dictionary<string, List<int>> map)
        {
            _ = map;
        }

        /// <summary>An overload distinguished only by its parameter list.</summary>
        [Mark("overload one")]
        public void Overloaded(int x)
        {
            _ = x;
        }

        /// <summary>The other overload.</summary>
        [Mark("overload two")]
        public void Overloaded(string x)
        {
            _ = x;
        }

        /// <summary>A conversion operator, whose id ends in its return type.</summary>
        [Mark("implicit")]
        public static implicit operator string(Signatures value)
        {
            _ = value;
            return "";
        }

        /// <summary>The explicit counterpart.</summary>
        [Mark("explicit")]
        public static explicit operator int(Signatures value)
        {
            _ = value;
            return 0;
        }
    }

    /// <summary>An indexer, whose id carries its index parameters.</summary>
    public class Bag
    {
        /// <summary>Indexes the bag by <paramref name="key"/>.</summary>
        [Mark("indexer")]
        public string this[[Mark("key")] string key] => key;
    }

    /// <summary>Implemented explicitly by <see cref="Thing"/>, whose member name then contains dots.</summary>
    public interface IThing
    {
        /// <summary>Does the thing.</summary>
        void Do();
    }

    /// <summary>An explicit implementation, whose name is mangled in the emitted id.</summary>
    public class Thing : IThing
    {
        /// <summary>Does the thing.</summary>
        [Mark("explicit implementation")]
        void IThing.Do()
        {
        }
    }

    /// <summary>Generic parameters, which are a site in their own right.</summary>
    /// <typeparam name="TOuter">The enclosing type's parameter.</typeparam>
    [Mark("outer")]
    public class Outer<[Mark("TOuter")] TOuter>
    {
        /// <summary>
        /// A nested generic, which redeclares <typeparamref name="TOuter"/> as its own parameter 0
        /// with the outer attribute copied onto it. Only <typeparamref name="TInner"/> is this
        /// type's own.
        /// </summary>
        /// <typeparam name="TInner">This type's own parameter.</typeparam>
        [Mark("inner")]
        public class Inner<[Mark("TInner")] TInner>
        {
            /// <summary>Takes one of each, so the id has to number them across the nesting.</summary>
            [Mark("nested method")]
            public void Use(TOuter outer, TInner inner)
            {
                _ = (outer, inner);
            }
        }

        /// <summary>A generic method, whose own parameters are numbered separately from its type's.</summary>
        /// <typeparam name="TMethod">The method's parameter.</typeparam>
        [Mark("generic method")]
        public TMethod Pick<[Mark("TMethod")] TMethod>(IList<TMethod> items, TOuter fallback)
        {
            _ = fallback;
            return items[0];
        }
    }

    /// <summary>Real JetBrains annotations, which the default filter selects on namespace alone.</summary>
    [PublicAPI]
    public class JetBrainsSurface
    {
        /// <summary>A member-level annotation.</summary>
        [NotNull]
        public string Name => "x";

        /// <summary>An annotation with two named arguments, both of which must survive.</summary>
        public string Inject([LanguageInjection("html", Prefix = "<div>", Suffix = "</div>")] string html)
        {
            return html;
        }

        /// <summary>A member-level annotation taking no arguments at all.</summary>
        [Pure]
        public string Clean(int x)
        {
            return x.ToString();
        }

        /// <summary>An annotation whose single argument is a string with syntax of its own.</summary>
        [ContractAnnotation("null => false")]
        public bool Check(string s)
        {
            return s != null;
        }
    }
}
