namespace Partas.Build

open System
open Partas.Build.Internal

/// A declaration-order address of a stage in one pipeline. Nested path elements are step indexes.
type ProducerLocation = {
    PipelineIndex: int
    IsPostStage: bool
    Path: int list
}

/// A producer's validated position in one invocation.
type ProducerPlacement = {
    Producer: ProducerRef
    /// None denotes the pipeline scope; otherwise the enclosing stage owns the value.
    Owner: ProducerLocation voption
    /// The stage before which implicit work must run, or the explicitly listed stage itself.
    Before: ProducerLocation
    IsExplicit: bool
}

/// Where one stage sits in a pipeline.
type StageAddress = {
    Location: ProducerLocation
    /// Declaration order, a stage ahead of the stages nested in it.
    Ordinal: int
    /// The stages enclosing this one, innermost first, with their addresses.
    Ancestors: (StageContext * ProducerLocation) list
}

/// The static producer graph for all pipelines a command will invoke.
type DependencyPlan = { Placements: ProducerPlacement list }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module StageAddress =
    /// <summary>The pipeline with each of its stages rebuilt from that stage's address.</summary>
    /// <remarks>
    /// <paramref name="rebuild"/> receives a stage whose own sub-stages have already been rebuilt, and answers
    /// the stages standing in its place. Addresses are allocated in declaration order, a stage ahead of the
    /// stages nested in it.
    /// <para>The one traversal that gives a stage a <see cref="T:Partas.Build.ProducerLocation"/>: validation
    /// and scheduling address stages through it.</para>
    /// </remarks>
    /// <param name="rebuild" />
    /// <param name="pipelineIndex" />
    /// <param name="pipeline" />
    let rebuildPipeline
        (rebuild: StageAddress -> StageContext -> StageContext list)
        (pipelineIndex: int)
        (pipeline: PipelineContext)
        : PipelineContext =
        let mutable allocated = -1

        let rec walk isPost ancestors path (stage: StageContext) =
            allocated <- allocated + 1
            let address = {
                Location = { PipelineIndex = pipelineIndex; IsPostStage = isPost; Path = path }
                Ordinal = allocated
                Ancestors = ancestors
            }
            let nested = (stage, address.Location) :: ancestors

            let steps =
                stage.Steps
                |> List.indexed
                |> List.collect (fun (index, step) ->
                    match step with
                    | Step.StepOfStage child -> walk isPost nested (path @ [ index ]) child |> List.map Step.StepOfStage
                    | step -> [ step ])

            rebuild address { stage with Steps = steps }

        let walkAll isPost stages =
            stages |> List.indexed |> List.collect (fun (index, stage) -> walk isPost [] [ index ] stage)

        { pipeline with
            Stages = walkAll false pipeline.Stages
            PostStages = walkAll true pipeline.PostStages }

module DependencyPlan =
    type private Entry = {
        Stage: StageContext
        Address: StageAddress
    }

    exception private InvalidDependency of string

    let private entries pipelineIndex (pipeline: PipelineContext) =
        let found = ResizeArray<Entry>()

        StageAddress.rebuildPipeline
            (fun address stage ->
                let parent =
                    match address.Ancestors with
                    | (parent, _) :: _ -> StageParent.Stage parent
                    | [] -> StageParent.Pipeline pipeline
                found.Add { Stage = { stage with ParentContext = ValueSome parent }; Address = address }
                [ stage ])
            pipelineIndex
            pipeline
        |> ignore

        // The rebuild reaches a stage after the stages nested in it; the ordinal is the declaration order the
        // placement rules read.
        found |> Seq.sortBy _.Address.Ordinal |> List.ofSeq

    let private owner (entry: Entry) =
        match entry.Address.Ancestors with
        | (_, location) :: _ -> ValueSome location
        | [] -> ValueNone

    let private unsafeScope (entry: Entry) =
        entry.Address.Ancestors
        |> List.tryFind (fun (scope, _) -> scope.ShuffleExecuteSequence || (scope.IsParallel scope).IsSome)
        |> Option.map (fun (scope, _) -> scope.Name)

    let private isWithin (owner: ProducerLocation voption) (consumer: ProducerLocation) =
        match owner with
        | ValueNone -> true
        | ValueSome owner ->
            owner.PipelineIndex = consumer.PipelineIndex
            && owner.IsPostStage = consumer.IsPostStage
            && List.length owner.Path < List.length consumer.Path
            && (consumer.Path |> List.take owner.Path.Length) = owner.Path

    /// <summary>Validates and locates producer work before any producer callback is invoked.</summary>
    /// <remarks>
    /// Each pipeline receives its own placement set; a second invocation validates afresh.
    /// <para>The supported scopes are sequential: a producer stage, whether listed explicitly or placed before
    /// its first consumer, must sit in a scope that is neither <c>parallel'</c> nor
    /// <c>shuffleExecuteSequence</c>, at any nesting depth. Both arrangements are rejected here, naming the
    /// producer and the scope. An author who needs a parallel scope to consume a producer lists that producer
    /// explicitly ahead of the scope; a consumer inside the scope then reads the value the enclosing sequential
    /// scope published.</para>
    /// </remarks>
    let validate (pipelines: PipelineContext list): Result<DependencyPlan, string> =
        try
            let placements = ResizeArray<ProducerPlacement>()

            pipelines
            |> List.iteri (fun pipelineIndex pipeline ->
                let stages = entries pipelineIndex pipeline
                let explicit =
                    stages
                    |> List.choose (fun entry ->
                        entry.Stage.Producer |> ValueOption.toOption |> Option.map (fun producer -> producer.Id, entry))
                    |> List.fold (fun found (id, entry) -> if Map.containsKey id found then found else Map.add id entry found) Map.empty
                let mutable placed = Map.empty<ProducerId, ProducerPlacement>

                let rec place (active: Set<ProducerId>) (demand: Entry) (producer: ProducerRef) =
                    if Set.contains producer.Id active then
                        raise (InvalidDependency $"Producer dependency cycle includes '%s{producer.Name}'.")
                    else
                      match Map.tryFind producer.Id placed with
                      | Some earlier ->
                        if not (isWithin earlier.Owner demand.Address.Location) && earlier.Before <> demand.Address.Location then
                            raise (InvalidDependency $"Producer '%s{producer.Name}' belongs to a scope unavailable to consumer '%s{demand.Stage.Name}'.")
                      | None ->
                        let explicitEntry = Map.tryFind producer.Id explicit
                        let position = explicitEntry |> Option.defaultValue demand
                        let isExplicit = explicitEntry.IsSome

                        if isExplicit && position.Address.Ordinal > demand.Address.Ordinal then
                            raise (InvalidDependency $"Consumer '%s{demand.Stage.Name}' precedes explicitly placed producer '%s{producer.Name}'.")

                        if isExplicit && not (isWithin (owner position) demand.Address.Location) && position.Address.Location <> demand.Address.Location then
                            raise (InvalidDependency $"Producer '%s{producer.Name}' is placed in a scope unavailable to consumer '%s{demand.Stage.Name}'.")

                        match unsafeScope position with
                        | Some scope when not isExplicit ->
                            raise (InvalidDependency $"Implicit producer '%s{producer.Name}' is first consumed inside parallel or shuffled scope '%s{scope}'. List it explicitly before that scope.")
                        | Some scope ->
                            raise (InvalidDependency $"Explicit producer '%s{producer.Name}' is inside parallel or shuffled scope '%s{scope}', where its order is ambiguous.")
                        | None -> ()

                        let active = Set.add producer.Id active
                        producer.Requires |> List.iter (place active position)

                        let placement = {
                            Producer = producer
                            Owner = owner position
                            Before = position.Address.Location
                            IsExplicit = isExplicit
                        }
                        placements.Add placement
                        placed <- Map.add producer.Id placement placed

                for entry in stages do
                    entry.Stage.Producer |> ValueOption.iter (place Set.empty entry)
                    entry.Stage.Requires |> List.iter (place Set.empty entry))

            Ok { Placements = List.ofSeq placements }
        with InvalidDependency message -> Error message
