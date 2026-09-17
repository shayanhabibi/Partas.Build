module Partas.Build.CompilerProbe.Negative.UnsupportedSettingState

open Partas.Build
open Partas.Build.Internal

// Must not compile: the shared mapping supports BuildStage, InputSpec<BuildStage> and InputSpec<StageContext>.
// Any other state is a constraint failure, and the diagnostic it produces is what `CompilerTests` pins.
let mapped: InputSpec<int> =
    StageMap.mapStage (fun ctx -> { ctx with Retry = 1 }) (InputSpec.ret 1)

// The same failure reached through the custom operation rather than the mapping helper.
let operated: InputSpec<int> = StageBuilder.StageBuilder("stage").retry(InputSpec.ret 1, 1)
