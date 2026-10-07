/// Reusable inputs registered only by the stages that read them.
[<RequireQualifiedAccess>]
module Partas.Build.EasyBuild.ShipIt.Inputs

open System
open Partas.Build

let private optional name description =
    Input.option<string option> name |> Input.arity Arity.ExactlyOne |> Input.description description
    |> Input.customParser (fun result -> result.Tokens |> Seq.tryLast |> Option.map _.Value)
    |> Input.validate (function Some value when String.IsNullOrWhiteSpace value -> Error "Value must not be blank." | _ -> Ok ())

let allowedBranches =
    Input.option<string list> "--allow-branch" |> Input.arity Arity.OneOrMore |> Input.allowMultipleArgumentsPerToken
    |> Input.def [ "main" ] |> Input.description "Branches allowed to generate releases"
    |> Input.validate (fun branches -> if branches.IsEmpty || List.exists String.IsNullOrWhiteSpace branches then Error "Provide nonblank branches." else Ok ())
let mode =
    Input.option<ReleaseMode> "--mode"
    |> Input.mapFromAmong [ "local", Local; "pull-request", PullRequest; "push", Push ]
    |> Input.def PullRequest |> Input.description "Apply locally, create a release PR, or push"
let preRelease = optional "--pre-release" "Prerelease prefix; supply beta for the upstream default prefix"
let remoteHostname = optional "--remote-hostname" "Git remote hostname"
let remoteOwner = optional "--remote-owner" "Git remote owner"
let remoteRepository = optional "--remote-repo" "Git remote repository"
let skipInvalidCommit = Input.option<bool> "--skip-invalid-commit" |> Input.description "Skip invalid conventional commits"
let skipMergeCommit = Input.option<bool> "--skip-merge-commit" |> Input.description "Skip merge commits"
let dryRun = Input.option<bool> "--dry-run" |> Input.description "Preview without applying changes"
let organization = Input.option<bool> "--org" |> Input.description "Also configure organization GitHub settings"
let changelogPath = optional "--changelog" "Changelog path; defaults to CHANGELOG.md"
let projects =
    Input.option<string list> "--project" |> Input.arity Arity.OneOrMore |> Input.allowMultipleArgumentsPerToken
    |> Input.description "Projects whose Version should follow this changelog"
    |> Input.validate (fun paths -> if paths.IsEmpty then Error "Supply at least one --project." else Ok ())
let toolVersion =
    Input.option<string> "--tool-version" |> Input.def "3.1.0" |> Input.description "Version installed only if ShipIt is missing"
    |> Input.validate (fun v -> if String.IsNullOrWhiteSpace v then Error "Supply a tool version." else Ok ())
    |> InputSpec.ofInput
let token =
    optional "--token" "GitHub token; defaults to GITHUB_TOKEN, otherwise upstream gh authentication"
    |> Input.sensitive
    |> Input.defaultValueFactory (fun _ ->
        match Environment.GetEnvironmentVariable "GITHUB_TOKEN" with
        | value when String.IsNullOrWhiteSpace value -> None
        | value -> Some value)
    |> InputSpec.ofInput

let private options (modeSpec: InputSpec<ReleaseMode>) = input {
    let! branches = allowedBranches
    and! mode = modeSpec
    and! prerelease = preRelease
    and! hostname = remoteHostname
    and! owner = remoteOwner
    and! repository = remoteRepository
    and! invalid = skipInvalidCommit
    and! merge = skipMergeCommit
    and! preview = dryRun
    return {
        AllowedBranches = branches; Mode = mode; PreRelease = prerelease
        RemoteHostname = hostname; RemoteOwner = owner; RemoteRepository = repository
        SkipInvalidCommit = invalid; SkipMergeCommit = merge; DryRun = preview
    }
}
let release = options (InputSpec.ofInput mode)
/// Local-only options; no --mode is registered.
let bump = options (InputSpec.ret Local)
