module Partas.Build.CompilerProbe.Negative.MonadicNeeds

open Partas.Build
open Partas.Build.Internal

/// The sugar a consumer might reach for. The dependency itself composes; binding its value inside `stage` is what
/// must not.
let needs (producer: Producer<'T>) = DependencySpec.require producer

let release = Producer.define "release" (InputSpec.ret ()) DependencySpec.empty (fun () () -> Operation.ret "v1")

// Must not compile: `StageBuilder` has no `Bind`, so a dependency value cannot be bound inside a stage. Binding it
// would re-parent the stage and hide the dependency from input discovery.
let publish: StageContext =
    stage "publish" {
        let! resolved = needs release
        echo resolved
    }
