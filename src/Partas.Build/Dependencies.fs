namespace Partas.Build

open System.CommandLine
open System.Threading
open Partas.Build.Internal

/// <summary>The identity of one producer declaration.</summary>
/// <remarks>Identity survives the runner's stage copies: every copy of a handle carries the one its declaration
/// allocated. Two declarations are distinct even when their names and arguments match.</remarks>
[<Sealed>]
type private ProducerIdSource() =
    static let mutable allocated = 0L
    static member Next() = ProducerId(Interlocked.Increment &allocated)

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
    /// <summary>Answers the operation yielding the value, once every prerequisite has published its own.</summary>
    /// <remarks>Calling it applies the producer's callback; the operation it answers runs later.</remarks>
    Prepare: ParseResult -> ProducerValues -> Result<Operation<'T>, string>
}
with
    /// <summary>This declaration with its result type erased from the signature and carried as a value.</summary>
    member this.Ref: ProducerRef = {
        Id = this.Id
        Name = this.Name
        Requires = this.Requires
        Inputs = this.Inputs
        ResultType = typeof<'T>
        Prepare = fun parseResult values ->
            this.Prepare parseResult values
            |> Result.map (Operation.map box >> box)
    }

module ProducerExecution =
    /// Materialises a declared producer's deferred work after its prerequisites have published values.
    let prepare (producer: ProducerRef) parseResult values: Result<Operation<obj>, string> =
        producer.Prepare parseResult values |> Result.map unbox<Operation<obj>>

/// <summary>The prerequisites of one piece of work, and the typed value their results read as.</summary>
/// <remarks>
/// Composition is applicative: <c>Read</c> receives the values a scope has published.
/// <para><c>Read</c> answers <c>Error</c> naming the first prerequisite whose published value is unavailable or
/// of another type. An exception out of <c>Read</c> comes from a function the caller supplied.</para>
/// </remarks>
type DependencySpec<'T> = {
    /// <summary>The producers required before the dependent work runs, in declaration order.</summary>
    Requires: ProducerRef list
    /// <summary>The CLI inputs the required producers declare.</summary>
    Inputs: ActionInput list
    Read: ProducerValues -> Result<'T, string>
}

module DependencySpec =
    /// Concatenates prerequisite lists, keeping the first occurrence of each identity.
    let private union (requires': ProducerRef list list) =
        requires'
        |> List.concat
        |> List.fold
            (fun kept (required: ProducerRef) ->
                if kept |> List.exists (fun (other: ProducerRef) -> other.Id = required.Id) then kept else kept @ [ required ])
            []

    let private read (producer: ProducerRef) (values: ProducerValues): Result<'T, string> =
        match ProducerValues.tryGet<'T> producer.Id values with
        | ValueSome value -> Ok value
        | ValueNone -> Error $"The producer '%s{producer.Name}' has published no value of type %s{typeof<'T>.Name}."

    let empty: DependencySpec<unit> = { Requires = []; Inputs = []; Read = fun _ -> Ok () }

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
        Read = spec.Read >> Result.map fn
    }

    /// <summary>The prerequisites and inputs of both specifications, read as <paramref name="fn"/> applied to both values.</summary>
    /// <remarks>The first unavailable prerequisite, in declaration order, is the one reported.</remarks>
    let map2 (fn: 'T -> 'U -> 'V) (first: DependencySpec<'T>) (second: DependencySpec<'U>): DependencySpec<'V> = {
        Requires = union [ first.Requires; second.Requires ]
        Inputs = InputSpec.union [ first.Inputs; second.Inputs ]
        Read = fun values ->
            match first.Read values, second.Read values with
            | Ok left, Ok right -> Ok(fn left right)
            | Error unavailable, _ -> Error unavailable
            | _, Error unavailable -> Error unavailable
    }

    /// <summary>A specification requiring both producers and reading their results as a pair.</summary>
    let zip (first: Producer<'T>) (second: Producer<'U>): DependencySpec<'T * 'U> =
        map2 (fun left right -> left, right) (require first) (require second)

module Producer =
    /// Lists a producer at this exact point in a pipeline or parent stage.
    let stage (producer: Producer<'T>): StageContext =
        { StageContext.create producer.Name with
            Producer = ValueSome producer.Ref
            DeclaredInputs = producer.Inputs }

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
        Prepare = fun parseResult values -> dependencies.Read values |> Result.map (execute (inputs.Read parseResult))
    }

/// <summary>Stages defined from what they consume.</summary>
module Stage =
    /// <summary>The label <c>--explain</c> renders for a consumer's step.</summary>
    let private label (requires': ProducerRef list) =
        match requires' with
        | [] -> "operation"
        | required -> required |> List.map _.Name |> String.concat ", " |> sprintf "needs %s"

    /// <summary>The stage with <paramref name="dependencies"/> added to what it requires, and
    /// <paramref name="execute"/> added as one more step over the values its scope has published.</summary>
    /// <remarks>
    /// The step runs the operation over the values the invocation has published, or fails naming the first
    /// unavailable prerequisite.
    /// <para>Every setting already on the stage is kept, so a consumer written through the <c>consumes</c>
    /// operation of a stage builder carries that builder's <c>retry</c> and conditions.</para>
    /// </remarks>
    let consumes (dependencies: DependencySpec<'D>) (execute: 'D -> Operation<unit>) (stage: StageContext): StageContext =
        let step (context: RuntimeContext) = async {
            match dependencies.Read (StageContext.publishedValues context.Stage) with
            | Error unavailable -> return StepOutcome.Failed (FailureCause.Reported unavailable)
            | Ok values -> return! Operation.toStepOutcome (execute values) context
        }

        { stage with
            DeclaredInputs = InputSpec.union [ stage.DeclaredInputs; dependencies.Inputs ]
            Requires = stage.Requires @ dependencies.Requires }
        |> StageContext.addOperation (ValueSome(label dependencies.Requires)) step

    let private consumer (name: string) (dependencies: DependencySpec<'D>) (execute: 'D -> Operation<unit>): StageContext =
        StageContext.create name |> consumes dependencies execute

    /// <summary>A stage whose work consumes producer results and returns unit.</summary>
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
