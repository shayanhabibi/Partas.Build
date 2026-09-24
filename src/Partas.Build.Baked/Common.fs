module Partas.Build.Baked.Common

open Partas.Build


let isCI =
    Input.option<bool> "--ci"
    |> Input.description "Indicates that the build is running in a CI environment; defaults to true if environment variables indicate so"
    |> Input.def (
        let vars = System.Environment.GetEnvironmentVariables()
        vars.Contains "CI"
        || vars.Contains "TRAVIS"
        || vars.Contains "CIRCLECI"
        || vars.Contains "BUILD_ID"
        || vars.Contains "GITLAB_CI"
        || vars.Contains "GITHUB_ACTIONS"
        )
