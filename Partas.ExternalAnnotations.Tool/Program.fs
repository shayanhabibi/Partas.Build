/// <summary>
/// The <c>partas-annotations</c> command line tool.
///
/// Presentation only. Every command is a pipeline defined in
/// <c>Partas.Build.ExternalAnnotations</c>, so a build CLI that references that library gets the
/// identical behaviour without going through this executable — and the tool cannot drift from it.
///
/// It exists because the MSBuild targets shell out to *something* during pack, and a dotnet tool
/// is the one form of "something" a consumer can restore from their manifest.
/// </summary>
module Partas.ExternalAnnotations.Tool

open Partas.Build
open Partas.Build.ExternalAnnotations

let mainBuilder argsv =
    rootCommand argsv {
        description "Generates and ships ReSharper external annotations with a NuGet package"

        addCommands [ generateCommand; verifyCommand; initCommand ]
    }

[<EntryPoint>]
let main argsv = mainBuilder argsv
