namespace Partas.Build

type EnvArg =
    {
        Name: string
        Values: string list
        Description: string option
        IsOptional: bool
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module EnvArg =
    let create name = {
        Name = name
        Values = []
        Description = None
        IsOptional = false
    }
    let withName name envArg = { envArg with Name = name }
    let withValues values envArg = { envArg with Values = values }
    let withIsOptional isOptional envArg = { envArg with IsOptional = isOptional }
    let withDescription description envArg = { envArg with Description = Some description }

