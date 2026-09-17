namespace Partas.Build

open System.CommandLine
open System.Threading
open Partas.Build.Internal

/// <summary>What an operation can read about the stage executing it.</summary>
/// <remarks>The cancellation token is the ambient one of the running step, reachable through
/// <c>Async.CancellationToken</c>.</remarks>
[<Struct>]
type RuntimeContext = {
    Stage: StageContext
    StepIndex: StepIndex
}

/// <summary>Work deferred until a stage executes it.</summary>
/// <remarks>Constructing an operation starts nothing: <c>Execute</c> runs when the step it belongs to runs.</remarks>
[<Struct>]
type Operation<'T> = { Execute: RuntimeContext -> Async<'T> }

/// <summary>The identity of one producer declaration.</summary>
/// <remarks>Allocated by <c>Producer.define</c> and carried by every copy of the handle, so identity survives the
/// runner's stage copies. Two declarations are distinct even when their names and arguments match.</remarks>
[<Struct>]
type ProducerId = ProducerId of id: int64

[<Sealed>]
type private ProducerIdSource() =
    static let mutable allocated = 0L
    static member Next() = ProducerId(Interlocked.Increment &allocated)

/// <summary>A producer declaration without its result type.</summary>
type ProducerRef = {
    Id: ProducerId
    Name: string
    /// <summary>The producers required before this one runs, in declaration order.</summary>
    Requires: ProducerRef list
}

/// <summary>The producer results published within one attempt scope.</summary>
/// <remarks>Storage is heterogeneous; <c>TryGet</c> answers a value only when both the identity and the result
/// type agree.</remarks>
[<Sealed>]
type ProducerValues private (values: Map<ProducerId, obj>) =
    /// <summary>A scope in which no producer has published yet.</summary>
    static member Empty = ProducerValues Map.empty

    /// <summary>The same values plus <paramref name="value"/> published under <paramref name="id"/>.</summary>
    member _.Add(id: ProducerId, value: 'T) = ProducerValues(Map.add id (box value) values)

    /// <summary>The value published under <paramref name="id"/>, when one of type <c>'T</c> is available.</summary>
    member _.TryGet<'T>(id: ProducerId): 'T voption =
        match Map.tryFind id values with
        | Some value ->
            match value with
            | :? 'T as typed -> ValueSome typed
            | _ -> ValueNone
        | None -> ValueNone

/// <summary>A typed producer handle.</summary>
/// <remarks>Declaring a producer registers its identity, its CLI inputs and its prerequisites; the callback it
/// carries runs when a consumer schedules it.</remarks>
type Producer<'T> = {
    Id: ProducerId
    Name: string
    /// <summary>The CLI inputs this producer declares, including those its prerequisites declare.</summary>
    Inputs: ActionInput list
    /// <summary>The producers required before this one runs, in declaration order.</summary>
    Requires: ProducerRef list
    /// <summary>Answers the operation yielding the value. Calling it starts no work.</summary>
    Prepare: ParseResult -> ProducerValues -> Operation<'T>
}
with
    /// <summary>This declaration seen without its result type.</summary>
    member this.Ref: ProducerRef = { Id = this.Id; Name = this.Name; Requires = this.Requires }

/// <summary>The prerequisites of one piece of work, and the typed value their results read as.</summary>
/// <remarks>Composition is applicative: <c>Read</c> receives the values a scope has published and never schedules
/// a producer.</remarks>
type DependencySpec<'T> = {
    /// <summary>The producers required before the dependent work runs, in declaration order.</summary>
    Requires: ProducerRef list
    /// <summary>The CLI inputs the required producers declare.</summary>
    Inputs: ActionInput list
    Read: ProducerValues -> 'T
}

module Operation =
    /// <summary>An operation that executes nothing and answers <paramref name="value"/>.</summary>
    let ret (value: 'T) = { Execute = fun _ -> async.Return value }

    /// <summary>An operation executing <paramref name="work"/> under the stage's runtime context.</summary>
    let ofAsync (work: Async<'T>) = { Execute = fun _ -> work }

module DependencySpec =
    /// Concatenates prerequisite lists, keeping the first occurrence of each identity.
    let private union (requires': ProducerRef list list) =
        requires'
        |> List.concat
        |> List.fold
            (fun kept (required: ProducerRef) ->
                if kept |> List.exists (fun (other: ProducerRef) -> other.Id = required.Id) then kept else kept @ [ required ])
            []

    let private read (producer: ProducerRef) (values: ProducerValues): 'T =
        match values.TryGet<'T> producer.Id with
        | ValueSome value -> value
        | ValueNone -> invalidOp $"The producer '%s{producer.Name}' has published no value of type %s{typeof<'T>.Name}."

    /// <summary>A specification requiring nothing.</summary>
    let empty: DependencySpec<unit> = { Requires = []; Inputs = []; Read = fun _ -> () }

    /// <summary>A specification requiring <paramref name="producer"/> and reading its result.</summary>
    let require (producer: Producer<'T>): DependencySpec<'T> = {
        Requires = [ producer.Ref ]
        Inputs = producer.Inputs
        Read = read producer.Ref
    }

    /// <summary>The same prerequisites, read as <paramref name="fn"/> applied to their value.</summary>
    let map (fn: 'T -> 'U) (spec: DependencySpec<'T>): DependencySpec<'U> = {
        Requires = spec.Requires
        Inputs = spec.Inputs
        Read = spec.Read >> fn
    }

    /// <summary>The prerequisites and inputs of both specifications, read as <paramref name="fn"/> applied to both values.</summary>
    let map2 (fn: 'T -> 'U -> 'V) (first: DependencySpec<'T>) (second: DependencySpec<'U>): DependencySpec<'V> = {
        Requires = union [ first.Requires; second.Requires ]
        Inputs = InputSpec.union [ first.Inputs; second.Inputs ]
        Read = fun values -> fn (first.Read values) (second.Read values)
    }

    /// <summary>A specification requiring both producers and reading their results as a pair.</summary>
    let zip (first: Producer<'T>) (second: Producer<'U>): DependencySpec<'T * 'U> =
        map2 (fun left right -> left, right) (require first) (require second)

module Producer =
    /// <summary>Declares a producer of <c>'T</c> from its CLI inputs, its prerequisites, and the work that
    /// computes the value.</summary>
    /// <remarks>Declaration allocates an identity and harvests inputs; <paramref name="execute"/> is called when
    /// a consumer schedules the producer, and the operation it answers runs after that.</remarks>
    let define
        (name: string)
        (inputs: InputSpec<'I>)
        (dependencies: DependencySpec<'D>)
        (execute: 'I -> 'D -> Operation<'T>)
        : Producer<'T> = {
        Id = ProducerIdSource.Next()
        Name = name
        Inputs = InputSpec.union [ inputs.Inputs; dependencies.Inputs ]
        Requires = dependencies.Requires
        Prepare = fun parseResult values -> execute (inputs.Read parseResult) (dependencies.Read values)
    }

/// <summary>Stages defined from what they consume.</summary>
module Stage =
    /// <summary>The label <c>--explain</c> renders for a consumer's step.</summary>
    let private label (requires': ProducerRef list) =
        match requires' with
        | [] -> "needs nothing"
        | required -> required |> List.map _.Name |> String.concat ", " |> sprintf "needs %s"

    /// <summary>The step running a consumer's operation over the values its scope has published.</summary>
    /// <remarks>
    /// Producer scheduling publishes nothing yet, so a consumer declaring prerequisites fails when it runs,
    /// naming the producer whose result is unavailable. A consumer declaring none executes its operation.
    /// </remarks>
    let private consumer (name: string) (dependencies: DependencySpec<'D>) (execute: 'D -> Operation<unit>): StageContext =
        let resolve () =
            try Ok(dependencies.Read ProducerValues.Empty)
            with :? System.InvalidOperationException as unavailable -> Error unavailable.Message

        let step: BuildStep = fun stage index -> async {
            match resolve () with
            | Error unavailable -> return Error unavailable
            | Ok values ->
                do! (execute values).Execute { Stage = stage; StepIndex = index }
                return Ok ()
        }

        StageContext.create name |> StageContext.addLabelledStepFn (label dependencies.Requires) step

    /// <summary>A stage that consumes producer results and produces none of its own.</summary>
    let consuming (name: string) (dependencies: DependencySpec<'D>) (execute: 'D -> Operation<unit>): StageContext =
        consumer name dependencies execute

    /// <summary>A stage that consumes CLI inputs of its own alongside producer results.</summary>
    let consumingWith
        (name: string)
        (inputs: InputSpec<'I>)
        (dependencies: DependencySpec<'D>)
        (execute: 'I -> 'D -> Operation<unit>)
        : InputSpec<StageContext> =
        {
            Inputs = InputSpec.union [ inputs.Inputs; dependencies.Inputs ]
            Read = fun parseResult -> consumer name dependencies (execute (inputs.Read parseResult))
        }
