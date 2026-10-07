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

/// `--quick`/`-q`: skips restores, installations, cleaning and formatting.
let quick =
    Input.option<bool> "--quick"
    |> Input.alias "-q"
    |> Input.description "Skips restores, installations, cleaning and formatting"

/// `--skip-tests`: skips building and running the test suites.
let skipTests =
    Input.option<bool> "--skip-tests"
    |> Input.description "Skips building and running the test suites"

/// `--watch`: runs the operation in watch mode.
let watch =
    Input.option<bool> "--watch"
    |> Input.description "Runs the operation in watch mode"
