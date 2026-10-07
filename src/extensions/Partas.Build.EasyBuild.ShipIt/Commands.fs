/// Ready-made commands, composable into any Partas.Build root command.
[<RequireQualifiedAccess>]
module Partas.Build.EasyBuild.ShipIt.Commands

open Partas.Build

let private operation name descriptionText (spec: InputSpec<StageContext>) = command name {
    description descriptionText
    Command.pipeline { spec }
}

let shipit = command "shipit" {
    description "ShipIt release workflows and explicit setup"
    addCommands [
        operation "generate" "Generate a release using the configured mode" Stages.generate
        operation "github" "Generate a GitHub release" Stages.github
        operation "bump" "Calculate and apply versions locally using ShipIt" Stages.bump
        operation "version" "Show installed ShipIt version" Stages.version
        operation "conventions" "Show conventional commit types" Stages.conventions
        operation "setup" "Install missing local tool registration and restore" Stages.setup
        command "init" {
            description "Explicitly initialize project files or GitHub settings"
            addCommands [
                operation "changelog" "Create a changelog without overwriting" Stages.initChangelog
                operation "workflows" "Create upstream workflow templates" Stages.initWorkflows
                operation "github" "Apply or preview GitHub settings" Stages.initGithub
                operation "project" "Register project Version updaters" (
                    Stages.configureProjectsWith
                        (InputSpec.ofInput Inputs.changelogPath |> InputSpec.map (Option.defaultValue "CHANGELOG.md"))
                        (InputSpec.ofInput Inputs.projects))
            ]
        }
    ]
}
