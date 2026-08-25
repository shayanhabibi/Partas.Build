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

#nowarn 3391

[<AutoOpen>]
module CliApiManagement =
    module Options =
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

        module GitHub =
            let key =
                Input.optionMaybe<string> "--github-key"
                |> Input.alias "--github"
                |> Input.arity Arity.ExactlyOne
                |> Input.desc "GitHub API key"
                |> Input.helpName "APIKEY"

        module Project =
            /// <summary>Every packable project: what <c>bump</c> versions and what <c>pack</c> packs. Add a project here to ship it.</summary>
            /// <remarks>
            /// Keyed off the file name rather than <c>PackageId</c>/<c>AssemblyName</c>: those are MSBuild
            /// properties, which the provider evaluates by shelling out to <c>dotnet msbuild -getProperty</c>.
            /// The accepted values are needed to *construct* the option, so paying for that would put an
            /// MSBuild evaluation per project in front of every command, <c>--help</c> included. <c>Path</c>
            /// is a compile-time constant.
            /// </remarks>
            let versioned =
                [ Repo.Project.``Partas.Build``.Path
                  Repo.Project.``Partas.ExternalAnnotations``.Path
                  Repo.Project.``Partas.ExternalAnnotations.Tool``.Path
                  Repo.Project.``Partas.Build.ExternalAnnotations``.Path ]

            let private name (path: string) = System.IO.Path.GetFileNameWithoutExtension path

            let projMap =
                versioned
                |> List.map (fun path -> (name path).ToLowerInvariant(), path)
                |> Map.ofList

            /// Resolves a `--project` value to the project file it names, case-insensitively.
            let getProj (key: string) = Map.tryFind (key.ToLowerInvariant()) projMap

            /// `acceptOnlyFromAmong` compares verbatim, so this is the exact spelling `--project` accepts.
            let targets =
                versioned
                |> List.collect (fun path -> [ name path ])
                |> List.distinct

            /// Requires at least one project: bumping every package because a flag was forgotten is not a default worth having.
            let target =
                Baked.Input.Project.target targets
                |> Input.required
    // No per-command option lists: a command registers whatever the stages of its pipelines declare, so
    // `--configuration` appears under `build` because the build stage binds `Options.config`.

[<AutoOpen>]
module FakeInitializationAndUtilities =
    let private root = Root.ToString()

    // Credit SAFE STACK
    let initializeContext () =
        let execContext = FakeExecutionContext.Create false "build.fsx" []
        setExecutionContext (RuntimeContext.Fake execContext)
