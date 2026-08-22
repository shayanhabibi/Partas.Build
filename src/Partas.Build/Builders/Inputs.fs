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

let inputs = InputsBuilder()
