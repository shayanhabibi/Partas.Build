module Docs.Directives

open Feliz.ViewEngine
open Nacara.Core
open Nacara.Plugins

let private cards =
    Directive.create "cards" Decode.node
    |> Directive.render (fun _ _ contents ->
        Html.div [ prop.className "pb-cards"; prop.children contents ])

let private card =
    Directive.create "card" (Decode.object (fun get ->
        {| Title = get.Required.Field "title" Decode.string
           Href = get.Required.Field "href" Decode.string |}))
    |> Directive.render (fun _ args contents ->
        Html.a [
            prop.className "pb-card"
            prop.href args.Href
            prop.children [ Html.strong args.Title; contents ]
        ])

let register = Directives.register [ cards; card ]
