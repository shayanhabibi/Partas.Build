namespace Partas.Build

open System.CommandLine
open Partas.Build.Internal

/// <summary>Producer execution, as the stages and steps that carry it.</summary>
/// <remarks>
/// The state itself is <see cref="T:Partas.Build.ExecutionState"/>, declared in <c>Types.fs</c> because a
/// <c>PipelineContext</c> carries one. This module is what puts work into it: it compiles after
/// <c>Operation</c>, <c>ProducerExecution.prepare</c> and <c>DependencyPlan</c>, which the scheduling needs and
/// the runner does not.
/// </remarks>
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ExecutionState =
    /// <summary>The step that runs <paramref name="producer"/> and publishes the value it answers.</summary>
    /// <remarks>The value reaches the scope once the operation completes; an operation that raises, times out or
    /// is cancelled leaves the scope's values as they were. A prerequisite unavailable at this point fails the
    /// step, naming it.</remarks>
    let private production (parseResult: ParseResult) (state: ExecutionState) (producer: ProducerRef) =
        let publishing = {
            Execute = fun context -> async {
                match ProducerExecution.prepare producer parseResult state.Values with
                | Error unavailable -> return Operation.fail (FailureCause.Reported unavailable)
                | Ok operation ->
                    let! value = operation.Execute context
                    state.Publish(producer, value)
            }
        }

        Step.Operation(ValueSome $"produce %s{producer.Name}", Operation.toStepOutcome publishing)

    /// <summary>The stage, skipped while one of <paramref name="required"/> has published no value, with that
    /// producer named as the reason.</summary>
    let private blockedWhileUnpublished (state: ExecutionState) (required: ProducerRef list) (stage: StageContext) =
        required
        |> List.fold
            (fun stage (required: ProducerRef) ->
                stage
                |> StageContext.addPredicateBecause
                    (ValueSome $"requires '%s{required.Name}', which published no value")
                    (fun _ -> state.Contains required.Id))
            stage

    /// The stage an implicit placement runs as: the producer's work, under the producer's own name.
    let private producerStage parseResult state (producer: ProducerRef) =
        { StageContext.create producer.Name with
            Producer = ValueSome producer
            Requires = producer.Requires
            DeclaredInputs = producer.Inputs
            Steps = [ production parseResult state producer ] }
        |> blockedWhileUnpublished state producer.Requires

    /// <summary>The pipelines with the producer work of <paramref name="plan"/> in place.</summary>
    /// <remarks>
    /// Run this on a plan <c>DependencyPlan.validate</c> accepted, and run the pipelines it answers rather than
    /// the ones it was given. An explicit placement becomes one more step of the stage that lists the producer;
    /// an implicit placement becomes a stage of its own, immediately before the consumer that demanded it, after
    /// the stages of that producer's own prerequisites. One placement per producer is one execution per
    /// invocation, however many consumers require it and however often it is listed.
    /// <para>A stage requiring a producer is skipped while that producer has published nothing, so a skipped or
    /// failed producer leaves its consumers skipped rather than failed, with the producer named as the reason.</para>
    /// <para>Parallel scopes take consumers alone: a consumer under <c>parallel'</c> or
    /// <c>shuffleExecuteSequence</c> reads a value published before its scope began. Placing a producer under
    /// such a scope is an arrangement <c>DependencyPlan.validate</c> rejects, naming the producer and the scope,
    /// so nothing is scheduled inside one. Concurrent execution and deduplication of producers are a later
    /// question.</para>
    /// <para>The pipelines answered are copies. They publish into the <c>ExecutionState</c> the originals carry,
    /// which the run empties before its first stage.</para>
    /// </remarks>
    let schedule (parseResult: ParseResult) (plan: DependencyPlan) (pipelines: PipelineContext list) =
        pipelines
        |> List.mapi (fun pipelineIndex pipeline ->
            let state = pipeline.Producers

            let placedAt isPost path =
                plan.Placements
                |> List.filter (fun placement ->
                    placement.Before = { PipelineIndex = pipelineIndex; IsPostStage = isPost; Path = path })

            let rec scheduleStage isPost path (stage: StageContext) =
                let steps =
                    stage.Steps
                    |> List.indexed
                    |> List.collect (fun (index, step) ->
                        match step with
                        | Step.StepOfStage child -> scheduleStage isPost (path @ [ index ]) child |> List.map Step.StepOfStage
                        | step -> [ step ])

                let listed, unlisted = placedAt isPost path |> List.partition _.IsExplicit

                let consumer = { stage with Steps = steps } |> blockedWhileUnpublished state stage.Requires

                let scheduled =
                    listed
                    |> List.fold
                        (fun stage (placement: ProducerPlacement) ->
                            stage
                            |> StageContext.addStep (production parseResult state placement.Producer)
                            |> blockedWhileUnpublished state placement.Producer.Requires)
                        consumer

                [ for placement in unlisted do
                      yield producerStage parseResult state placement.Producer
                  yield scheduled ]

            let scheduleAll isPost stages =
                stages |> List.indexed |> List.collect (fun (index, stage) -> scheduleStage isPost [ index ] stage)

            { pipeline with
                Stages = scheduleAll false pipeline.Stages
                PostStages = scheduleAll true pipeline.PostStages })
