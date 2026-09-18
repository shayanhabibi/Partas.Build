namespace Partas.Build

open System.Collections.Generic
open System.CommandLine
open Partas.Build.Internal

/// <summary>Producer execution, as the stages and steps that carry it.</summary>
/// <remarks>
/// The state itself is <see cref="T:Partas.Build.ExecutionState"/>, declared in <c>Types.fs</c> because a
/// <c>PipelineContext</c> carries one. This module puts work into it: it compiles after <c>Operation</c>,
/// <c>ProducerExecution.prepare</c> and <c>DependencyPlan</c>, which the scheduling reads and the runner does
/// not.
/// </remarks>
module ExecutionSchedule =
    /// <summary>The step that runs <paramref name="producer"/> and publishes the value it answers.</summary>
    /// <remarks>The value reaches the scope once the operation completes; an operation that raises, times out or
    /// is cancelled leaves the scope's values as they were. A prerequisite unavailable at this point fails the
    /// step, naming it.</remarks>
    /// <param name="parseResult" />
    /// <param name="state" />
    /// <param name="producer" />
    let private production (parseResult: ParseResult) (state: ExecutionState) (producer: ProducerRef) =
        let publishing = {
            Execute = fun context -> async {
                match
                    ExecutionState.values state
                    |> ProducerExecution.prepare producer parseResult
                with
                | Error unavailable -> return Operation.fail (FailureCause.Reported unavailable)
                | Ok operation ->
                    let! value = operation.Execute context
                    ExecutionState.publish producer value state
            }
        }

        Step.Operation(ValueSome $"produce %s{producer.Name}", Operation.toStepOutcome publishing)

    /// <summary>The stage, skipped while one of <paramref name="required"/> has published no value, with that
    /// producer named as the reason.</summary>
    /// <param name="required" />
    /// <param name="state" />
    /// <param name="stage" />
    let private blockedWhileUnpublished (state: ExecutionState) (required: ProducerRef list) (stage: StageContext) =
        required
        |> List.fold
            (fun stage (required: ProducerRef) ->
                stage
                |> StageContext.addPredicateBecause
                    (ValueSome $"requires '%s{required.Name}', which published no value")
                    (fun _ -> ExecutionState.contains required.Id state))
            stage

    /// <summary>The stages of <paramref name="pipeline"/> as declared, each carrying the parents the runner
    /// gives it.</summary>
    /// <remarks>
    /// Each stage carries the parent chain the runner gives it: a condition evaluated here answers what it
    /// answers in the run.
    /// <para>Addressing is <c>StageAddress.rebuildPipeline</c>'s alone: these stages carry their declarations,
    /// not their positions.</para>
    /// </remarks>
    /// <param name="pipeline" />
    let private declaredStages (pipeline: PipelineContext) =
        let rec walk parent (stage: StageContext) = [
            let stage = { stage with ParentContext = ValueSome parent }
            yield stage

            for step in stage.Steps do
                match step with
                | Step.StepOfStage child -> yield! walk (StageParent.Stage stage) child
                | _ -> ()
        ]

        [
            for stage in pipeline.Stages @ pipeline.PostStages do
                yield! walk (StageParent.Pipeline pipeline) stage
        ]

    /// <summary>Whether a stage requiring the given producer is active, through any chain of producers.</summary>
    /// <remarks>
    /// Reads the conditions the author declared, on the stages as they were declared: the condition scheduling
    /// conjoins onto a consumer asks whether the value is published, which is what this decides. A stage
    /// declaring no condition is active, and the producers it requires run.
    /// <para>A consumer's conditions are evaluated here and again when the consumer is reached, the way
    /// <c>--explain</c> evaluates them.</para>
    /// </remarks>
    let private demandedBy (declared: StageContext list) (placements: ProducerPlacement list) =
        let requires (id: ProducerId) (required: ProducerRef list) = required |> List.exists (fun other -> other.Id = id)

        let rec demanded (seen: Set<ProducerId>) (id: ProducerId) =
            let byStage (stage: StageContext) =
                let listing =
                    match stage.Producer with
                    | ValueSome listed -> requires id listed.Requires
                    | ValueNone -> false

                (requires id stage.Requires || listing) && stage.IsActive stage

            let throughProducer (placement: ProducerPlacement) =
                not placement.IsExplicit
                && not (Set.contains placement.Producer.Id seen)
                && requires id placement.Producer.Requires
                && demanded (Set.add placement.Producer.Id seen) placement.Producer.Id

            List.exists byStage declared || List.exists throughProducer placements

        fun (id: ProducerId) -> demanded (Set.singleton id) id

    /// The stage an implicit placement runs as: the producer's work, under the producer's own name.
    let private producerStage parseResult state (demanded: ProducerId -> bool) (producer: ProducerRef) =
        { StageContext.create producer.Name with
            Producer = ValueSome producer
            Requires = producer.Requires
            DeclaredInputs = producer.Inputs
            Steps = [ production parseResult state producer ] }
        |> blockedWhileUnpublished state producer.Requires
        |> StageContext.addPredicateBecause
            (ValueSome $"every stage requiring '%s{producer.Name}' is inactive")
            (fun _ -> demanded producer.Id)

    /// The address as a diagnostic names it.
    let private describe (location: ProducerLocation) =
        let path = location.Path |> List.map string |> String.concat "."
        if location.IsPostStage then $"post stage %s{path}" else $"stage %s{path}"

    /// <summary>The pipelines with the producer work of <paramref name="plan"/> in place.</summary>
    /// <remarks>
    /// Run this on a plan <c>DependencyPlan.validate</c> accepted, and run the pipelines it answers rather than
    /// the ones it was given. An explicit placement becomes one more step of the stage that lists the producer;
    /// an implicit placement becomes a stage of its own, immediately before the consumer that demanded it, after
    /// the stages of that producer's own prerequisites. One placement per producer is one execution per
    /// invocation, however many consumers require it and however often it is listed. Every placement is located
    /// through <c>StageAddress.rebuildPipeline</c>, the traversal that addressed it during validation; a
    /// placement reaching no stage fails the invocation, naming the producer and the address.
    /// <para>A stage requiring a producer is skipped while that producer has published nothing, and names it as
    /// the reason, which leaves the consumers of a skipped or failed producer skipped. An implicitly placed
    /// producer runs where a stage requiring it is active and is skipped where every such stage is inactive; an
    /// explicitly listed producer runs where the author's own conditions put it.</para>
    /// <para>Parallel scopes take consumers alone: a consumer under <c>parallel'</c> or
    /// <c>shuffleExecuteSequence</c> reads a value published before its scope began. Placing a producer under
    /// such a scope is an arrangement <c>DependencyPlan.validate</c> rejects, naming the producer and the
    /// scope.</para>
    /// <para>The pipelines answered are copies. They publish into the <c>ExecutionState</c> the originals carry,
    /// which the run empties before its first stage.</para>
    /// </remarks>
    /// <param name="parseResult" />
    /// <param name="plan" />
    /// <param name="pipelines" />
    let schedule (parseResult: ParseResult) (plan: DependencyPlan) (pipelines: PipelineContext list) =
        pipelines
        |> List.mapi (fun pipelineIndex pipeline ->
            let state = pipeline.Producers
            let placements = plan.Placements |> List.filter (fun placement -> placement.Before.PipelineIndex = pipelineIndex)
            let demanded = demandedBy (declaredStages pipeline) placements
            let located = HashSet<ProducerId>()

            let rebuild (address: StageAddress) (stage: StageContext) =
                let here = placements |> List.filter (fun placement -> placement.Before = address.Location)
                let listed, unlisted = here |> List.partition _.IsExplicit

                for placement in here do
                    located.Add placement.Producer.Id |> ignore

                let consumer = stage |> blockedWhileUnpublished state stage.Requires

                let scheduled =
                    listed
                    |> List.fold
                        (fun stage (placement: ProducerPlacement) ->
                            stage
                            |> StageContext.addStep (production parseResult state placement.Producer)
                            |> blockedWhileUnpublished state placement.Producer.Requires)
                        consumer

                [ for placement in unlisted do
                      yield producerStage parseResult state demanded placement.Producer
                  yield scheduled ]

            let scheduled = StageAddress.rebuildPipeline rebuild pipelineIndex pipeline

            for placement in placements do
                if not (located.Contains placement.Producer.Id) then
                    let message =
                        $"Producer '%s{placement.Producer.Name}' is placed at %s{describe placement.Before} of pipeline "
                        + $"'%s{pipeline.Name}', which holds no stage."

                    PipelineContext.printError pipeline message
                    raise (PipelineFailedException message)

            scheduled)
