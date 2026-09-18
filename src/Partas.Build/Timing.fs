namespace Partas.Build

open System

/// <summary>The wall time of one stage of a run, with how the stage ended.</summary>
/// <remarks><c>Elapsed</c> covers the stage's own steps and every stage nested under them.</remarks>
[<Struct>]
type StageTiming = {
    Name: string
    /// The number of stages enclosing this one; 0 for a stage of the pipeline itself.
    Depth: int
    Elapsed: TimeSpan
    Outcome: StageOutcome
}

/// <summary>The stages a pipeline run has finished, as the tree the stages nest into.</summary>
/// <remarks>
/// Stages append concurrently under <c>parallel'</c>. <c>Start</c> supplies the ordinal that orders a stage
/// among its siblings and that its own sub-stages record as their parent; a stage of the pipeline records
/// <c>0L</c>.
/// </remarks>
[<ReferenceEquality>]
type StageTimings = private {
    entries: System.Collections.Concurrent.ConcurrentBag<struct (int64 * int64 * StageTiming)>
    mutable started: int64
}

module StageTimings =
    let create() = {
        entries = System.Collections.Concurrent.ConcurrentBag()
        started = 0L
    }
    /// The ordinal of the stage starting now.
    let start (stageTimings: StageTimings) = System.Threading.Interlocked.Increment &stageTimings.started
    let add (parent: int64) (order: int64) (stageTiming: StageTiming) (stageTimings: StageTimings) =
        stageTimings.entries.Add(struct(parent, order, stageTiming))
    /// Discards every recorded stage. A second run of the same pipeline value reports itself alone.
    let clear (stageTimings: StageTimings) =
        let mutable stageTiming = Unchecked.defaultof<struct(int64 * int64 * StageTiming)>
        while stageTimings.entries.TryTake &stageTiming do ()

    /// <summary>Every recorded stage in pre-order, each sub-stage under the stage containing it.</summary>
    /// <remarks>Siblings read in start order, which <c>parallel'</c> makes nondeterministic.</remarks>
    let ordered (stageTimings: StageTimings) =
        let children =
            stageTimings.entries
            |> List.ofSeq
            |> List.groupBy (fun struct (parent, _, _) -> parent)
            |> List.map (fun (parent, siblings) -> parent, siblings |> List.sortBy (fun struct (_, order, _) -> order))
            |> Map.ofList

        let rec walk parent = [
            match Map.tryFind parent children with
            | None -> ()
            | Some siblings ->
                for struct (_, order, timing) in siblings do
                    timing
                    yield! walk order
        ]

        walk 0L

