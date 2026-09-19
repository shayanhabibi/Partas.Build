[<AutoOpen>]
module Partas.Build.DependenciesBuilder

open System.ComponentModel
open Partas.Build.Internal
open Partas.Build

/// <summary>
/// Applicative computation expression collecting the CLI inputs and producers a value depends on.
/// </summary>
/// <remarks>
/// <c>Bind</c> is deliberately absent: a sequential <c>let!</c> would let the second source depend on the
/// first's value, which cannot be known before parsing, so the input set would not be statically readable.
/// Omitting it makes that a compile error (<c>FS0708</c>) instead of a silently incomplete option set.
/// Bind every source in one <c>let!</c>/<c>and!</c> group.
/// </remarks>
type DependenciesBuilder() =
    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Source(producer: Producer<'T>): DependencySpec<'T> =
        DependencySpec.require producer

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Source(spec: DependencySpec<'T>): DependencySpec<'T> = spec

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.MergeSources(a: DependencySpec<'A>, b: DependencySpec<'B>): DependencySpec<'A * 'B> = DependencySpec.map2 (fun a b -> a,b) a b

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member _.MergeSources3(a: DependencySpec<'A>, b: DependencySpec<'B>, c: DependencySpec<'C>): DependencySpec<'A * 'B * 'C> = {
        Inputs = InputSpec.union [ a.Inputs; b.Inputs; c.Inputs ]
        Requires = ProducerRef.union [ a.Requires; b.Requires; c.Requires ]
        Read = fun pr ->
            match a.Read pr, b.Read pr, c.Read pr with
            | Ok a, Ok b, Ok c -> Ok(a,b,c)
            | Error unavailable, _, _
            | _, Error unavailable, _
            | _, _, Error unavailable -> Error unavailable
    }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member _.MergeSources4(a: DependencySpec<'A>, b: DependencySpec<'B>, c: DependencySpec<'C>, d: DependencySpec<'D>): DependencySpec<'A * 'B * 'C * 'D> = {
        Inputs = InputSpec.union [ a.Inputs; b.Inputs; c.Inputs; d.Inputs ]
        Requires = ProducerRef.union [ a.Requires; b.Requires; c.Requires; d.Requires ]
        Read = fun pr ->
            match a.Read pr, b.Read pr, c.Read pr, d.Read pr with
            | Ok a, Ok b, Ok c, Ok d -> Ok(a,b,c,d)
            | Error unavailable, _, _, _
            | _, Error unavailable, _, _
            | _, _, Error unavailable, _
            | _, _, _, Error unavailable -> Error unavailable
    }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member _.MergeSources5(a: DependencySpec<'A>, b: DependencySpec<'B>, c: DependencySpec<'C>, d: DependencySpec<'D>, e: DependencySpec<'E>): DependencySpec<'A * 'B * 'C * 'D * 'E> = {
        Inputs = InputSpec.union [ a.Inputs; b.Inputs; c.Inputs; d.Inputs; e.Inputs ]
        Requires = ProducerRef.union [ a.Requires; b.Requires; c.Requires; d.Requires; e.Requires ]
        Read = fun pr ->
            match a.Read pr, b.Read pr, c.Read pr, d.Read pr, e.Read pr with
            | Ok a, Ok b, Ok c, Ok d, Ok e -> Ok(a,b,c,d,e)
            | Error unavailable, _, _, _, _
            | _, Error unavailable, _, _, _
            | _, _, Error unavailable, _, _
            | _, _, _, Error unavailable, _
            | _, _, _, _, Error unavailable -> Error unavailable
    }

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.BindReturn(spec: DependencySpec<'A>, [<InlineIfLambda>] fn: 'A -> 'B): DependencySpec<'B> = DependencySpec.map fn spec

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.Return(value: 'T): DependencySpec<'T> = DependencySpec.empty |> DependencySpec.map (fun _ -> value)

    [<EditorBrowsable(EditorBrowsableState.Never)>]
    member inline _.ReturnFrom(spec: DependencySpec<'T>): DependencySpec<'T> = spec

let dependency = DependenciesBuilder()
