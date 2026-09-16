module Partas.Build.Baked.Dotnet

open System
open Partas.Build

let config =
    BuildOptionInput.createWith "configuration" [ "-c" ]
    |> BuildOption.createMaybe<string>
    |> BuildOption.map (
        Input.arity Arity.ExactlyOne
        >> Input.mapFromAmongWith StringComparer.OrdinalIgnoreCase [
            "release", Some "Release"
            "r", Some "Release"
            "debug", Some "Debug"
            "d", Some "Debug"
        ])
