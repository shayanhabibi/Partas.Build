module Partas.Build.Baked.NuGet
open Partas.Build


/// NuGet key from an arg/opt, defaults to the environment variable NUGET_API_KEY
let apiKey =
    BuildOptionInput.create "nuget-key"
    |> BuildOptionInput.withAlias "k"
    |> BuildOption.createMaybe<string>
    |> BuildOption.map (
        Input.desc "NuGet API key"
        >> Input.helpName "APIKEY"
        >> Input.arity Arity.ExactlyOne
        >> Input.def (
                try
                let result = System.Environment.GetEnvironmentVariable "NUGET_API_KEY"
                if System.String.IsNullOrEmpty result then None else Some result
                with _ -> None
            )
        )
