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

/// The static producer graph for all pipelines a command will invoke.
type DependencyPlan = { Placements: ProducerPlacement list }

module DependencyPlan =
    type private Entry = {
        Stage: StageContext
        Location: ProducerLocation
        Ordinal: int
        Ancestors: (StageContext * ProducerLocation) list
    }

    exception private InvalidDependency of string

    let private location pipelineIndex isPost path = {
        PipelineIndex = pipelineIndex
        IsPostStage = isPost
        Path = path
    }

    let private entries pipelineIndex (pipeline: PipelineContext) =
        let found = ResizeArray<Entry>()

        let rec walk isPost ancestors path (stage: StageContext) =
            let parent =
                match ancestors with
                | (parent, _) :: _ -> StageParent.Stage parent
                | [] -> StageParent.Pipeline pipeline
            let stage = { stage with ParentContext = ValueSome parent }
            let here = location pipelineIndex isPost path
            found.Add {
                Stage = stage
                Location = here
                Ordinal = found.Count
                Ancestors = ancestors
            }

            stage.Steps
            |> List.iteri (fun index step ->
                match step with
                | Step.StepOfStage child -> walk isPost ((stage, here) :: ancestors) (path @ [ index ]) child
                | _ -> ())

        pipeline.Stages |> List.iteri (fun index stage -> walk false [] [ index ] stage)
        pipeline.PostStages |> List.iteri (fun index stage -> walk true [] [ index ] stage)
        List.ofSeq found

    let private owner (entry: Entry) =
        match entry.Ancestors with
        | (_, location) :: _ -> ValueSome location
        | [] -> ValueNone

    let private unsafeScope (entry: Entry) =
        entry.Ancestors
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

    /// Validates and locates producer work before any producer callback is invoked.
    /// Each pipeline receives its own placement set; a second invocation validates afresh.
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
                        if not (isWithin earlier.Owner demand.Location) && earlier.Before <> demand.Location then
                            raise (InvalidDependency $"Producer '%s{producer.Name}' belongs to a scope unavailable to consumer '%s{demand.Stage.Name}'.")
                      | None ->
                        let explicitEntry = Map.tryFind producer.Id explicit
                        let position = explicitEntry |> Option.defaultValue demand
                        let isExplicit = explicitEntry.IsSome

                        if isExplicit && position.Ordinal > demand.Ordinal then
                            raise (InvalidDependency $"Consumer '%s{demand.Stage.Name}' precedes explicitly placed producer '%s{producer.Name}'.")

                        if isExplicit && not (isWithin (owner position) demand.Location) && position.Location <> demand.Location then
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
                            Before = position.Location
                            IsExplicit = isExplicit
                        }
                        placements.Add placement
                        placed <- Map.add producer.Id placement placed

                for entry in stages do
                    entry.Stage.Producer |> ValueOption.iter (place Set.empty entry)
                    entry.Stage.Requires |> List.iter (place Set.empty entry))

            Ok { Placements = List.ofSeq placements }
        with InvalidDependency message -> Error message
