module Partas.Build.CompilerProbe.Negative.NestedInputSpec

open Partas.Build
open Partas.Build.Internal

// Must not compile: yielding an input-aware value inside a stage that is itself returned from `input` would need
// a flattening of InputSpec<InputSpec<_>> that the builders deliberately do not offer.
let nested: InputSpec<StageContext> =
    input {
        return stage "outer" {
            InputSpec.ret (stage "inner" { retry 1 })
        }
    }
