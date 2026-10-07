module Partas.Build.Baked.Fable

open System
open Partas.Build

let extension =
    Input.option<string> "--extension"
    |> Input.alias "-e"
    |> Input.description "File extension for Fable generated files"
    |> Input.def ".fs.js"
    |> Input.helpName "EXT"

let language =
    Input.option<string> "--language"
    |> Input.alias "--lang"
    |> Input.acceptOnlyFromAmong [
        "js"
        "ts"
        "py"
        "rs"
        "php"
        "dart"
        "beam"
        "erlang"
    ]

let output =
    Input.optionMaybe<string> "--output"
    |> Input.alias "-o"
    |> Input.description "Output directory"
    |> Input.helpName "OUT_DIR"

module Stages =
    let cleanWith (ext: InputSpec<string>) (dir: string) = input {
        let! extension = ext
        return stage "fable-clean" {
            run (cmd $"dotnet fable clean --cwd {dir} --yes -e {extension}")
        }
    }
    let clean dir = cleanWith (InputSpec.ofInput extension) dir
