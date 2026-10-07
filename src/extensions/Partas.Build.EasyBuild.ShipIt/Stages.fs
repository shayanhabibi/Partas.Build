/// Composable ShipIt stages. Setup effects occur only at execution time.
[<RequireQualifiedAccess>]
module Partas.Build.EasyBuild.ShipIt.Stages

open System.IO
open System.Threading
open Partas.Build
open Partas.Build.Internal

let private commandStage name (command: Cmd) =
        let stage = stage name { run command }
        // Boundary inspection is deferred until execution, so discovery is pure.
        let steps = stage.Steps |> List.map (function
            | Step.StepFn(label, execute) -> Step.StepFn(label, fun ctx index -> async {
                ToolSetup.validateBoundary (StageContext.getWorkingDir ctx |> ValueOption.defaultWith Directory.GetCurrentDirectory) |> ignore
                return! execute ctx index
              })
            | other -> other)
        { stage with Steps = steps }

let private operation name (build: 'T -> Cmd) (inputs: InputSpec<'T>) =
    inputs |> InputSpec.map (build >> commandStage name)

let generateWith options = operation "shipit generate" Operations.generate options
let generate = generateWith Inputs.release
let githubWith (options: InputSpec<ReleaseOptions>) (token: InputSpec<string option>) = input {
    let! options = options
    and! token = token
    return commandStage "shipit github" (Operations.github options token)
}
let github = githubWith Inputs.release Inputs.token
/// Calculate and apply versions locally, even if the consumer supplies another mode.
let bumpWith options =
    options |> InputSpec.map (fun options -> { options with Mode = Local })
    |> operation "shipit bump" Operations.generate
let bump = bumpWith Inputs.bump
let version = operation "shipit version" Operations.version (InputSpec.ret ())
let conventions = operation "shipit conventions" Operations.conventions (InputSpec.ret ())
let initChangelogWith path = operation "shipit init changelog" Operations.initChangelog path
let initChangelog = initChangelogWith (InputSpec.ofInput Inputs.changelogPath)
let initWorkflows = operation "shipit init workflows" Operations.initWorkflows (InputSpec.ret ())
let initGithubWith (organization: InputSpec<bool>) (dryRun: InputSpec<bool>) = input {
    let! organization = organization
    and! dryRun = dryRun
    return commandStage "shipit init github" (Operations.initGithub organization dryRun)
}
let initGithub = initGithubWith (InputSpec.ofInput Inputs.organization) (InputSpec.ofInput Inputs.dryRun)

let private directory ctx = StageContext.getWorkingDir ctx |> ValueOption.defaultWith Directory.GetCurrentDirectory

let setupWith (version: InputSpec<string>) = input {
    let! version = version
    return stage "shipit setup" {
        run (fun (ctx: StageContext) -> async {
            let root = ToolSetup.validateBoundary (directory ctx)
            let executionContext = StageContext.setWorkingDir (ValueSome root) ctx
            let execute command = async {
                match! CmdRunner.run executionContext 0<stepIndex> CancellationToken.None command with
                | Ok () -> return 0
                | Error error -> return failwith error
            }
            do! ToolSetup.runWith execute root version
            return Ok ()
        })
    }
}
let setup = setupWith Inputs.toolVersion

/// Explicit project adoption. Creates a changelog only when absent, then registers XML updaters.
let configureProjectsWith (changelog: InputSpec<string>) (projects: InputSpec<string list>) = input {
    let! changelog = changelog
    and! projects = projects
    return stage "shipit init project" {
        run (fun (ctx: StageContext) -> async {
            let root = directory ctx
            ToolSetup.validateBoundary root |> ignore
            let path = Path.GetFullPath(Path.Combine(root, changelog))
            let projects = projects |> List.map (fun project -> Path.GetFullPath(Path.Combine(root, project)))
            ProjectSetup.validateProjects projects
            if not (File.Exists path) then
                let! result = CmdRunner.run ctx 0<stepIndex> CancellationToken.None (Operations.initChangelog (Some path))
                match result with
                | Error error -> failwith error
                | Ok () -> ()
            ProjectSetup.configure path projects
            return Ok ()
        })
    }
}
let configureProjects changelog projects = configureProjectsWith (InputSpec.ret changelog) (InputSpec.ret projects)
