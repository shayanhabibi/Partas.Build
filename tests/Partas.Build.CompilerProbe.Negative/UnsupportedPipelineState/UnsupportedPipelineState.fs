module Partas.Build.CompilerProbe.Negative.UnsupportedPipelineState

open Partas.Build
open Partas.Build.Internal

// Must not compile: the shared mapping supports BuildPipeline and InputSpec<BuildPipeline>.
// Any other state fails to resolve, and the overload list the compiler answers with is what `CompilerTests` pins.
let mapped: InputSpec<int> =
    PipelineMap.mapPipeline (fun ctx -> { ctx with Timeout = ValueSome(System.TimeSpan.FromSeconds 1.0) }) (InputSpec.ret 1)

// The same failure reached through the custom operation rather than the mapping helper.
let operated: InputSpec<int> = PipelineBuilder.PipelineBuilder("p").timeout(InputSpec.ret 1, 1)
