namespace Partas.Build.EasyBuild.ShipIt

/// Where ShipIt applies a calculated release.
[<Struct>]
type ReleaseMode = Local | PullRequest | Push

/// Upstream release options; changelog front matter owns file updaters.
type ReleaseOptions = {
    AllowedBranches: string list
    Mode: ReleaseMode
    PreRelease: string option
    RemoteHostname: string option
    RemoteOwner: string option
    RemoteRepository: string option
    SkipInvalidCommit: bool
    SkipMergeCommit: bool
    DryRun: bool
}

[<RequireQualifiedAccess>]
module ReleaseOptions =
    /// Upstream defaults, with no prerelease requested.
    let defaults = {
        AllowedBranches = [ "main" ]; Mode = PullRequest; PreRelease = None
        RemoteHostname = None; RemoteOwner = None; RemoteRepository = None
        SkipInvalidCommit = false; SkipMergeCommit = false; DryRun = false
    }
