namespace Partas.Build.Internal

open System.ComponentModel
open Partas.Build

/// <summary>Applies a state-preserving update to whichever representation a stage builder state currently holds.</summary>
/// <remarks>
/// The supported states are <c>BuildStage</c>, <c>InputSpec&lt;BuildStage></c> and <c>InputSpec&lt;StageContext></c>.
/// Any other state fails to resolve, and the compiler lists the three supported ones.
/// <para>Public: the inline members dispatching through it resolve in consuming assemblies.</para>
/// </remarks>
[<EditorBrowsable(EditorBrowsableState.Never)>]
type StageMap =
    /// <summary>Composes the update after the state's own build function.</summary>
    static member Map(build: BuildStage, update: StageContext -> StageContext): BuildStage =
        build >> update

    /// <summary>Composes the update after the build function the specification yields, deferring the read.</summary>
    static member Map(spec: InputSpec<BuildStage>, update: StageContext -> StageContext): InputSpec<BuildStage> =
        InputSpec.map (fun (build: BuildStage) -> build >> update) spec

    /// <summary>Applies the update to the stage the specification yields, deferring the read.</summary>
    static member Map(spec: InputSpec<StageContext>, update: StageContext -> StageContext): InputSpec<StageContext> =
        InputSpec.map update spec

    /// <summary>Dispatches <paramref name="update"/> to the <c>Map</c> overload of the state's representation.</summary>
    static member inline Apply(state: ^State, update: StageContext -> StageContext): ^State =
        ((^State or StageMap): (static member Map: ^State * (StageContext -> StageContext) -> ^State) (state, update))

/// <summary>Stage updates written once for every supported builder state.</summary>
[<EditorBrowsable(EditorBrowsableState.Never); CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module StageMap =
    /// <summary>Applies a state-preserving stage update to <paramref name="state"/>, keeping its representation.</summary>
    let inline mapStage ([<InlineIfLambda>] update: StageContext -> StageContext) (state: ^State): ^State =
        StageMap.Apply(state, update)

/// <summary>The stage settings whose implementation is shared by every builder state a stage CE can be in.</summary>
/// <remarks>
/// Each operation here replaces a pair of same-named members — one over <c>BuildStage</c>, one over its
/// <c>InputSpec</c> mirror — with a single member generic in the state. Representation-changing members
/// (<c>Yield</c>, <c>Delay</c>, <c>Combine</c>, <c>For</c>, <c>Run</c>) stay explicit in the builder that
/// inherits this one.
/// </remarks>
[<EditorBrowsable(EditorBrowsableState.Advanced)>]
type StageSettingsBuilder() =
    /// <summary>Sets how many further attempts the stage's steps get after a failing one.</summary>
    /// <include file="../xmldoc/stage.xml" path="/stage/retry/*"/>
    [<CustomOperation("retry")>]
    member inline _.retry(state: ^State, count: int): ^State =
        StageMap.mapStage (fun ctx -> { ctx with Retry = max 0 count }) state

    /// <summary>Adds a step that runs <paramref name="execute"/> over the results of
    /// <paramref name="dependencies"/>, and declares those producers as the stage's prerequisites.</summary>
    /// <remarks>
    /// The stage is the consumer, so the settings written beside this operation — <c>retry</c>, conditions,
    /// timeouts — govern the consuming work itself rather than a wrapper around it.
    /// <para>The producers' CLI inputs join the stage's declared inputs, and reach the command before it
    /// parses.</para>
    /// </remarks>
    [<CustomOperation("consumes")>]
    member inline _.consumes(state: ^State, dependencies: DependencySpec<'D>, execute: 'D -> Operation<unit>): ^State =
        StageMap.mapStage (Stage.consumes dependencies execute) state
