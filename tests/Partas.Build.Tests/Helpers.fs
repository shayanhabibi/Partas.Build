module Partas.Build.Tests.Helpers

open System.CommandLine
open System.Threading
open Partas.Build
open Partas.Build.Internal

/// Registers a spec's inputs on a throwaway command and parses <c>commandLine</c> against it.
/// Options are registered here and nowhere else — a test that reads a value it never declared
/// gets the CLR default rather than a parse error, so assertions read real parsed values only
/// when the input reached this function through <c>InputSpec.Inputs</c>.
let parse (inputs: ActionInput list) (commandLine: string) =
    let root = RootCommand "test"

    for input in inputs do
        match input.Source with
        | ParsedOption option -> root.Options.Add option
        | ParsedArgument argument -> root.Arguments.Add argument
        | _ -> ()

    root.Parse commandLine

/// Runs a stage as the first stage of no pipeline and reports its outcome. <c>Error</c> carries the
/// exceptions the stage's steps raised, so a step that returned <c>Error</c> rather than raising gives
/// an empty list.
let runStage (stage: StageContext) : Result<unit, exn list> =
    match StageContext.run stage (StageIndex.Stage 0) CancellationToken.None with
    | true, _ -> Ok ()
    | false, exns -> Error (List.ofSeq exns)

/// The option names a spec declares, in declaration order.
let inputNames (inputs: ActionInput list) = [
    for input in inputs do
        match input.Source with
        | ParsedOption option -> option.Name
        | ParsedArgument argument -> argument.Name
        | _ -> "?"
]
