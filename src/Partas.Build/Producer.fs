namespace Partas.Build

open System

/// A key allocated for one producer declaration and retained by every copy of its handle.
[<Struct>]
type ProducerId = ProducerId of id: int64

/// <summary>Values published during one invocation or attempt scope.</summary>
/// <remarks>
/// A value is held alongside the type it was produced as. A published <c>None</c> or <c>ValueNone</c> reads
/// back as <c>ValueSome None</c> or <c>ValueSome ValueNone</c>: intentional absence is a result a consumer
/// handles.
/// </remarks>
[<Struct>]
type ProducerValues = private ProducerValues of Map<ProducerId, struct(Type * obj)>

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ProducerValues =
    let private create = ProducerValues
    let inline private itemise<^T> (value: ^T) = struct (typeof<^T>, box value)
    let empty = create Map.empty
    let add<'T> (id: ProducerId) (value: 'T) (ProducerValues map) =
        map |> Map.add id (itemise value) |> create
    let addBoxed (id: ProducerId) (produced: Type) (value: obj) (ProducerValues map) =
        map |> Map.add id struct (produced, value) |> create
    let contains (id: ProducerId) (ProducerValues map) = Map.containsKey id map
    let tryGet<'T>(id: ProducerId) (ProducerValues map): 'T voption =
        match Map.tryFind id map with
        | Some(struct (produced, value)) when typeof<'T>.IsAssignableFrom produced -> ValueSome(unbox<'T> value)
        | _ -> ValueNone

/// The executable-erased declaration shared by a typed handle and every stage using it.
/// Prepare returns a boxed Operation<obj>; ProducerExecution.prepare exposes its typed form.
type ProducerRef = {
    Id: ProducerId
    Name: string
    Requires: ProducerRef list
    Inputs: ActionInput list
    /// The type of the value this producer publishes.
    ResultType: Type
    Prepare: CommandLine.ParseResult -> ProducerValues -> Result<obj, string>
}

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module internal ProducerRef =
    /// Concatenates prerequisite lists, keeping the first occurrence of each identity.
    let union (refs: ProducerRef list list) =
        refs
        |> List.concat
        |> List.fold (fun kept (required: ProducerRef) ->
            if
                kept
                |> List.exists (fun (other: ProducerRef) ->
                    other.Id = required.Id)
            then kept
            else kept @ [ required ])
            []
    let read (producer: ProducerRef) (values: ProducerValues): Result<'T, string> =
        ProducerValues.tryGet<'T> producer.Id values
        |> ValueOption.map Ok
        |> ValueOption.defaultWith (fun () -> Error $"The producer '%s{producer.Name}' has published no value of type %s{typeof<'T>.Name}")

/// <summary>What one pipeline invocation has published, and the boundary at which a scope discards it.</summary>
/// <remarks>
/// Invocation-local: a run empties the state before its first stage, and a second invocation of the same
/// pipeline value executes its producers again.
/// <para>Publication is sequential, within the scopes <c>DependencyPlan.validate</c> admits: a producer stage
/// sits outside every <c>parallel'</c> and <c>shuffleExecuteSequence</c> scope. Consumers running in parallel
/// read values completed before their scope began.</para>
/// </remarks>
type ExecutionState = private {
    sync: obj
    mutable values: ProducerValues
}

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ExecutionState =
    let create() = { sync = obj(); values = ProducerValues.empty }
    let inline private withSync (fn: ExecutionState -> 'T) (executionState: ExecutionState) =
        lock executionState.sync (fun () -> fn executionState)
    /// The values available to the work running now.
    let values state = withSync _.values state
    let contains id state = withSync (fun state -> ProducerValues.contains id state.values) state
    /// <summary>Publishes value as the result of producer.</summary>
    let publish (producerRef: ProducerRef) (value: obj) =
        withSync (fun executionState -> executionState.values <- ProducerValues.addBoxed producerRef.Id producerRef.ResultType value executionState.values)
    /// <summary>Restores the values as snapshot held them.</summary>
    /// <remarks>An attempt boundary: a scope that snapshots before its first attempt discards, on every further
    /// attempt, what the previous attempt published.</remarks>
    let resetTo (snapshot: ProducerValues) =
        withSync (fun values -> values.values <- snapshot)
    /// Discards every published value. An invocation starts from here.
    let clear = resetTo ProducerValues.empty
