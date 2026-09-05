module Docs.Site

open Feliz.ViewEngine
open Nacara.Core
open Nacara.Plugins
open Nacara.Theme

let theme =
    Theme.defaults
    |> Theme.navbar [
        NavbarSection("Build", "build", "/build/")
        NavbarSection("External Annotations", "external-annotations", "/external-annotations/")
        NavbarSection("Reference", "reference", "/reference/")
        NavbarDivider
        NavbarSection("Blog", "blog", "/blog/05092026-fantomas")
    ]
    |> Theme.navbarEnd
        [
            // NavbarDynamicWidget Search.trigger
            NavbarIcon("GitHub", "https://github.com/shayanhabibi/Partas.Build", Icons.github)
        ]
    |> Theme.editUrl "https://github.com/shayanhabibi/Partas.Build/edit/master/docs"
    |> Theme.footer (Html.p [ Html.text "Built with Nacara" ])
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
            Sources = [ FSharpApiSource.create "../src/Partas.Build/bin/Release/net10.0/Partas.Build.dll" ]
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
    >> Sitemap.register
    >> LightningCss.register
    >> Nuglify.minifyHtml
    >> Nuglify.minifyJs

let collections =
    Site.collection content
    >> Site.collection reference
    >> Site.collection blog

let site =
    Site.create "Partas.Build"
    |> Site.origin "https://shayanhabibi.github.io/Partas.Build"
    |> Site.baseUrl "/"
    |> Site.output "../output"
    |> Site.staticFiles "static"
    |> Theme.register theme
    |> plugins
    |> collections

[<EntryPoint>]
let main argv = Nacara.run site argv
