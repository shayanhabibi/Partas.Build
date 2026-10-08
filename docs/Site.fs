module Docs.Site

open Feliz.ViewEngine
open System.IO
open Nacara.Core
open Nacara.Plugins
open Partas.Nacara.Theme

let theme =
    Theme.defaults
    |> Theme.favIcon "favicon.svg"
    |> Theme.lightTokens (fun t -> { t with Primary = "#165c50"; ContentWidth = "48rem"; Radius = "0.6rem" })
    |> Theme.darkTokens (fun t -> { t with Primary = "#7de0bd"; ContentWidth = "48rem"; Radius = "0.6rem" })
    |> Theme.layerAfter "responsive" "partas" (File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "theme.css")))
    |> Theme.navbar [
        NavbarSection("Guide", "build", "/build/")
        NavbarSection("Extensions", "extensions", "/extensions/")
        NavbarSection("External Annotations", "external-annotations", "/external-annotations/")
        NavbarSection("Reference", "reference", "/reference/")
        NavbarDivider
        NavbarSection("Blog", "blog", "/blog/05092026-fantomas")
    ]
    |> Theme.navbarEnd
        [
            NavbarDynamicWidget Search.trigger
            NavbarIcon("GitHub", "https://github.com/shayanhabibi/Partas.Build", Icons.github)
        ]
    |> Theme.editUrl "https://github.com/shayanhabibi/Partas.Build/edit/master/docs"
    |> Theme.footer (Html.p [ Html.text "Partas.Build · Build workflows in F# · Built with Nacara" ])
    |> Theme.menu "build" [
        Menu.section "Start here" [
            Menu.page "Build/index.md"
            Menu.page "Build/installation.md"
            Menu.page "Build/getting-started.md"
        ]
        Menu.section "Build workflows" [
            Menu.page "Build/inputs.md"
            Menu.page "Build/steps.md"
            Menu.page "Build/composition.md"
            Menu.page "Build/execution.md"
            Menu.page "Build/baked.md"
        ]
        Menu.section "Integrate and inspect" [
            Menu.page "Build/agents.md"
            Menu.page "Build/hosting.md"
            Menu.page "Build/troubleshooting.md"
        ]
        Menu.section "Reference" [
            Menu.page "Build/CAPABILITIES.md"
            Menu.page "Build/computation-expression-operations.md"
            Menu.page "Build/workflow-reference.md"
            Menu.page "Build/composition-reference.md"
        ]
    ]
    |> Theme.menu "extensions" [
        Menu.section "Extensions" [ Menu.page "extensions/index.md"; Menu.page "extensions/shipit.md" ]
    ]
    |> Theme.menu "external-annotations" [
        Menu.section "External annotations" [
            Menu.page "external-annotations/index.md"
            Menu.page "external-annotations/external-annotations-recipes.md"
            Menu.page "external-annotations/external-annotations-api.md"
        ]
    ]
    |> Theme.menu "blog" [
        Menu.section "2026" [
            Menu.section "September" [
                Menu.page "blog/05092026-fantomas"
            ]
        ]
    ]

let apiOptions = {
        FSharpApi.defaults with
            Root = "reference"
            Sources = [
                FSharpApiSource.create "../src/Partas.Build/bin/Release/net10.0/Partas.Build.dll"
                FSharpApiSource.create "../src/extensions/Partas.Build.EasyBuild.ShipIt/bin/Release/net10.0/Partas.Build.EasyBuild.ShipIt.dll"
            ]
            Exclude = [ "Partas.Build.Internal" ]
    }

let reference =
    FSharpApi.collection "reference" DocFrontMatter.decoder apiOptions
    |> Collection.title _.Title
    |> Collection.layout (Theme.layout theme)

let blog =
    Collection.create "blog" DocFrontMatter.decoder
    |> Collection.title _.Title
    |> Collection.layout (Theme.layout theme)

let content =
    Theme.docs theme "content"

let plugins =
    Markdown.register
    >> FSharpApi.register apiOptions
    >> Literate.register
    >> TreeSitter.register
    >> Docs.Directives.register
    >> Sitemap.register
    >> AgentFriendly.registerWith (
        AgentFriendly.summary "Composable build workflows and command-line tools in F#."
        >> AgentFriendly.details "Start with https://shayanhabibi.github.io/Partas.Build/build/getting-started.md. Commands derive their inputs and help from the stages they run. Inspect a consumer build with --help, --schema --json, or --explain --json before executing it. See https://shayanhabibi.github.io/Partas.Build/build/agents.md for JSON output and exit codes, and https://shayanhabibi.github.io/Partas.Build/AGENTS-snippet.md for repository instructions."
    )
    // >> OgImage.registerWith (
    //     OgImage.defaultImage (OgImage.image "/img/sun-ztu.jpeg" |> OgImage.withAlt "Partas.Build")
    // )
    >> LightningCss.register
    >> Esbuild.register
    >> Nuglify.minifyHtml
    >> Search.register
    // Validate after AgentFriendly writes Markdown and llms-full.txt. Canonical URLs point to
    // this build, not the previously deployed site; local links are still validated normally.
    >> LinkValidator.registerWith (LinkValidator.ignoring [ @"^https://shayanhabibi\.github\.io/Partas\.Build/" ])

let collections =
    Site.collection content
    >> Site.collection reference
    >> Site.collection blog

let site =
    Site.create "Partas.Build"
    |> Site.origin "https://shayanhabibi.github.io"
    |> Site.baseUrl "/Partas.Build/"
    |> Site.output "../output"
    |> Site.staticFiles "static"
    |> Theme.register theme
    |> plugins
    |> collections

[<EntryPoint>]
let main argv = Nacara.run site argv
