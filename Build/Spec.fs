/// <summary>
/// Everything the build commands are written against: typed paths into the
/// repository and the CLI option set.
///
/// Program.fs holds the stages and the commands; this file holds the nouns.
/// </summary>
module Spec

open Partas.TypeProvider.BuildHelper
open Partas.Build
open Fake.Core
open Fake.Core.Context

[<Literal>]
let __REPOSITORY_DIRECTORY__ =
    __SOURCE_DIRECTORY__
  + "/.."
type Repo =
    BuildHelperProvider<__REPOSITORY_DIRECTORY__,
                        capabilityFullOverride = true,
                        virtualPathConfig = """
bin/
tmp/
""">

/// <summary>
/// Typed view of the repository on disk. Every path below is checked at
/// compile time, so renaming a project without updating the build breaks the
/// build project rather than failing halfway through a release.
/// </summary>
type Root = Repo.FileSystem

/// Virtual paths that don't exist at design time (output artifacts).
type VRoot = Repo.VirtualFileSystem

let inline funApply value fn = fn value

[<AutoOpen>]
module DirectoryManagement =
    open Fake.IO.Globbing.Operators

    /// Source files considered by formatting and linting.
    let sourceFiles =
        !! "**/*.fs"
     -- "**/obj/**/*.*"
     -- "**/AssemblyInfo.fs"

[<AutoOpen>]
module GitManagement =
    [<Literal>]
    let githubUsername = "GitHub Action"

    [<Literal>]
    let githubEmail = "41898282+github-actions[bot]@users.noreply.github.com"

    [<Literal>]
    let gitCiPrefix =
        "-c user.name=\""
      + githubUsername
      + "\" -c user.email=\""
      + githubEmail
      + "\""

    [<Literal>]
    let gitCiCommand =
        "git "
      + gitCiPrefix

    let gitCiArgs =
        [ "-c"
          $"user.name=\"{githubUsername}\""
          "-c"
          $"user.email=\"{githubEmail}\"" ]

#nowarn 3391

[<AutoOpen>]
module CliApiManagement =
    module Options =
        let config =
            Input.option<string> "--configuration"
            |> Input.alias "-c"
            |> Input.desc "Build/pack configuration"
            |> Input.def "Release"
            |> Input.arity Arity.ExactlyOne
            |> Input.helpName "Debug|Release"
            |> Input.acceptOnlyFromAmong [ "Debug"; "Release" ]

        let quick =
            Input.option<bool> "--quick"
            |> Input.alias "-q"
            |> Input.desc "Skips installations, linting, and other checks"

        let skipTests =
            Input.option<bool> "--skip-tests"
            |> Input.desc "Skips running tests"

        let watch =
            Input.option<bool> "--watch"
            |> Input.desc "Runs the operation in watch mode."

        let interactive =
            Input.option<bool> "--interactive"
            |> Input.alias "-i"
            |> Input.desc "Runs the operation in interactive mode."

        module NuGet =
            let environmentKey =
                try
                let result = System.Environment.GetEnvironmentVariable "NUGET_API_KEY"
                if System.String.IsNullOrEmpty result then None else Some result
                with _ -> None
            let key =
                Input.optionMaybe<string> "--nuget-key"
                |> Input.alias "--nuget"
                |> Input.arity Arity.ExactlyOne
                |> Input.desc "NuGet API key"
                |> Input.helpName "APIKEY"
                |> Input.def environmentKey

        module GitHub =
            let key =
                Input.optionMaybe<string> "--github-key"
                |> Input.alias "--github"
                |> Input.arity Arity.ExactlyOne
                |> Input.desc "GitHub API key"
                |> Input.helpName "APIKEY"

    // No per-command option lists: a command registers whatever the stages of its pipelines declare, so
    // `--configuration` appears under `build` because the build stage binds `Options.config`.

[<AutoOpen>]
module FakeInitializationAndUtilities =
    let private root = Root.ToString()

    // Credit SAFE STACK
    let initializeContext () =
        let execContext = FakeExecutionContext.Create false "build.fsx" []
        setExecutionContext (RuntimeContext.Fake execContext)

    module Git =
        open Fake.Tools.Git

        let inline private run command =
            CommandHelper.directRunGitCommandAndFail root command

        let pushTags pass =
            run $"{gitCiPrefix} push --tags origin"
            pass

        let pushBranch branchName pass =
            run $"{gitCiPrefix} push origin {branchName}"
            pass

        let pushBranchAndTags branchName pass =
            pushBranch branchName pass
            |> pushTags

        let branchName () =
            Information.getBranchName root

        let pushCurrentBranch pass =
            branchName ()
            |> pushBranch
            |> funApply pass

        let pushCurrentBranchAndTags pass =
            branchName ()
            |> pushBranchAndTags
            |> funApply pass

        let commitFiles msg files =
            files
            |> List.iter (
                   Staging.stageFile root
                >> ignore
                   )

            Commit.exec root msg

        let tagBranch tag =
            Branches.tag root tag
