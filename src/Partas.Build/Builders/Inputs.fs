[<AutoOpen>]
module Partas.Build.InputsBuilder

open System.ComponentModel
open Partas.Build.Internal
open Partas.Build

/// <summary>
/// Applicative computation expression collecting the CLI inputs a value depends on.
/// </summary>
/// <remarks>
/// <c>Bind</c> is deliberately absent: a sequential <c>let!</c> would let the second source depend on the
/// first's value, which cannot be known before parsing, so the input set would not be statically readable.
/// Omitting it makes that a compile error (<c>FS0708</c>) instead of a silently incomplete option set.
/// Bind every source in one <c>let!</c>/<c>and!</c> group.
/// </remarks>
type InputsBuilder() =
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Source(input: ActionInput<'T>): InputSpec<'T> = InputSpec.ofInput input

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Source(spec: InputSpec<'T>): InputSpec<'T> = spec

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.MergeSources(a: InputSpec<'A>, b: InputSpec<'B>): InputSpec<'A * 'B> = {
        Inputs = InputSpec.union [ a.Inputs; b.Inputs ]
        Read = fun pr -> a.Read pr, b.Read pr
    }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.MergeSources3(a: InputSpec<'A>, b: InputSpec<'B>, c: InputSpec<'C>): InputSpec<'A * 'B * 'C> = {
        Inputs = InputSpec.union [ a.Inputs; b.Inputs; c.Inputs ]
        Read = fun pr -> a.Read pr, b.Read pr, c.Read pr
    }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.MergeSources4(a: InputSpec<'A>, b: InputSpec<'B>, c: InputSpec<'C>, d: InputSpec<'D>): InputSpec<'A * 'B * 'C * 'D> = {
        Inputs = InputSpec.union [ a.Inputs; b.Inputs; c.Inputs; d.Inputs ]
        Read = fun pr -> a.Read pr, b.Read pr, c.Read pr, d.Read pr
    }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.MergeSources5(a: InputSpec<'A>, b: InputSpec<'B>, c: InputSpec<'C>, d: InputSpec<'D>, e: InputSpec<'E>)
        : InputSpec<'A * 'B * 'C * 'D * 'E> = {
        Inputs = InputSpec.union [ a.Inputs; b.Inputs; c.Inputs; d.Inputs; e.Inputs ]
        Read = fun pr -> a.Read pr, b.Read pr, c.Read pr, d.Read pr, e.Read pr
    }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.BindReturn(spec: InputSpec<'A>, [<InlineIfLambda>] fn: 'A -> 'B): InputSpec<'B> = {
        Inputs = spec.Inputs
        Read = fun pr -> fn (spec.Read pr)
    }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Return(value: 'T): InputSpec<'T> = InputSpec.ret value

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.ReturnFrom(spec: InputSpec<'T>): InputSpec<'T> = spec

/// <summary>Binds CLI inputs to a value — usually a stage or a pipeline — whose inputs a command registers.</summary>
/// <remarks>
/// Every source is bound in one <c>let!</c>/<c>and!</c> group. The result is an <c>InputSpec</c>: yielded into a
/// <c>stage</c>, <c>pipeline</c> or <c>command</c>, its options appear in that command's <c>--help</c>.
/// </remarks>
/// <example>
/// <code lang="fsharp">
/// module Options =
///     let configuration = Input.option&lt;string&gt; "--configuration" |> Input.alias "-c" |> Input.def "Release"
///     let quick = Input.option&lt;bool&gt; "--quick" |> Input.alias "-q"
///
/// let build = input {
///     let! configuration = Options.configuration
///     and! quick = Options.quick
///     return stage "build" {
///         when' (not quick) "--quick is set"
///         run (cmd $"dotnet build -c {configuration}")
///     }
/// }
/// </code>
/// </example>
let input = InputsBuilder()

[<System.Obsolete("Use `input` instead.")>]
let inputs = InputsBuilder()
