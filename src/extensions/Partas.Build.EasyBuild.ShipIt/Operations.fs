/// Pure commands for the EasyBuild.ShipIt local tool. Constructing commands has no side effects.
[<RequireQualifiedAccess>]
module Partas.Build.EasyBuild.ShipIt.Operations

open System
open Partas.Build

let private nonblank name value =
    if String.IsNullOrWhiteSpace value then invalidArg name "Value must not be blank."
    value

let private optional name value command =
    command |> Cmd.argWhenSome value (fun value -> [ name; nonblank name value ])

let private release prefix (options: ReleaseOptions) =
    if options.AllowedBranches.IsEmpty then invalidArg "AllowedBranches" "At least one allowed branch is required."
    let mode = match options.Mode with Local -> "local" | PullRequest -> "pull-request" | Push -> "push"
    Cmd.ofList "dotnet" ("shipit" :: prefix)
    |> Cmd.args (options.AllowedBranches |> List.collect (fun branch -> [ "--allow-branch"; nonblank "AllowedBranches" branch ]))
    |> Cmd.args [ "--mode"; mode ]
    |> optional "--pre-release" options.PreRelease
    |> optional "--remote-hostname" options.RemoteHostname
    |> optional "--remote-owner" options.RemoteOwner
    |> optional "--remote-repo" options.RemoteRepository
    |> Cmd.argIf options.SkipInvalidCommit [ "--skip-invalid-commit" ]
    |> Cmd.argIf options.SkipMergeCommit [ "--skip-merge-commit" ]
    |> Cmd.argIf options.DryRun [ "--dry-run" ]

/// Generate changelogs and apply configured file updaters.
let generate options = release [] options

/// Explicit GitHub provider, with the supplied token masked in command labels.
let github options token =
    let command = release [ "github" ] options
    match token with
    | Some token -> command |> Cmd.secretOption "--token" (nonblank "token" token)
    | None -> command

/// Report the installed tool version.
let version () = Cmd.ofList "dotnet" [ "shipit"; "version" ]
/// Show supported conventional commit types.
let conventions () = Cmd.ofList "dotnet" [ "shipit"; "conventions" ]
/// Create a changelog. Upstream refuses to overwrite an existing file.
let initChangelog path =
    Cmd.ofList "dotnet" [ "shipit"; "init"; "changelog" ]
    |> Cmd.argWhenSome path (fun path -> [ nonblank "path" path ])
/// Scaffold upstream GitHub workflow templates, without overwriting existing files.
let initWorkflows () = Cmd.ofList "dotnet" [ "shipit"; "init"; "workflows" ]
/// Apply or preview GitHub settings, optionally including organization settings.
let initGithub organization dryRun =
    Cmd.ofList "dotnet" [ "shipit"; "init"; "github" ]
    |> Cmd.argIf organization [ "--org" ]
    |> Cmd.argIf dryRun [ "--dry-run" ]
