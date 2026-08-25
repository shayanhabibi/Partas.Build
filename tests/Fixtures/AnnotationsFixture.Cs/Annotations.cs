using System;

namespace Fixture.Annotations
{
    /// <summary>
    /// The workhorse fixture attribute: two constructor overloads, a plain named argument, and an
    /// array-valued one, so a test can tell an <c>argument</c> from a <c>property</c> and see how
    /// a collection value is flattened.
    /// </summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class MarkAttribute : Attribute
    {
        /// <summary>Marks a site with <paramref name="tag"/>.</summary>
        public MarkAttribute(string tag)
        {
            Tag = tag;
        }

        /// <summary>The second overload exists so the emitted <c>ctor</c> id has to disambiguate.</summary>
        public MarkAttribute(string tag, int order)
        {
            Tag = tag;
            Order = order;
        }

        /// <summary>The positional argument.</summary>
        public string Tag { get; }

        /// <summary>Zero unless the two-argument constructor was used.</summary>
        public int Order { get; }

        /// <summary>A named argument.</summary>
        public string Note { get; set; } = "";

        /// <summary>A named argument whose value is a collection.</summary>
        public string[] Tags { get; set; } = Array.Empty<string>();
    }

    /// <summary>Values for <see cref="GradeAttribute"/>; deliberately not contiguous.</summary>
    public enum Level
    {
        /// <summary>The zero value, which is also the CLR default.</summary>
        Low = 0,

        /// <summary>A middle value.</summary>
        Medium = 1,

        /// <summary>A value that is not the default, so a test can tell it from one.</summary>
        High = 2
    }

    /// <summary>Carries the two argument kinds the value formatter special-cases: an enum and a <see cref="Type"/>.</summary>
    [AttributeUsage(AttributeTargets.All)]
    public sealed class GradeAttribute : Attribute
    {
        /// <summary>Grades a site at <paramref name="level"/>.</summary>
        public GradeAttribute(Level level)
        {
            Level = level;
        }

        /// <summary>The enum-valued positional argument.</summary>
        public Level Level { get; }

        /// <summary>A <see cref="Type"/>-valued named argument.</summary>
        public Type Fallback { get; set; }
    }

    /// <summary>
    /// Declared <c>internal</c>, the way Partas.Solid declares its <c>LanguageInjection</c> alias.
    /// A sidecar has to carry it anyway: invisibility to a consumer's compiler is the reason the
    /// sidecar exists.
    /// </summary>
    [AttributeUsage(AttributeTargets.All)]
    internal sealed class SecretAttribute : Attribute
    {
        /// <summary>Marks a site with <paramref name="note"/>.</summary>
        public SecretAttribute(string note)
        {
            Note = note;
        }

        /// <summary>The positional argument.</summary>
        public string Note { get; }
    }
}
