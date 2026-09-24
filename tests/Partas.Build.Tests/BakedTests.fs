/// <summary>The ready-made stages of <c>Partas.Build.Baked</c>, run through a command the way a consumer runs them.</summary>
module Partas.Build.Tests.BakedTests

open System.IO
open Expecto
open Partas.Build

[<Tests>]
let tests =
    testList "baked" [
        test "bump reports each project through the stage's output" {
            let directory = Directory.CreateTempSubdirectory "partas-build-bump"

            try
                let project = Path.Combine(directory.FullName, "Bumped.fsproj")
                File.WriteAllText(project, """<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>""")
                let lines = ResizeArray<string>()

                let built =
                    command "bump" {
                        pipeline "bump" {
                            quiet
                            stage "outer" {
                                outputTo (StageOutput.Redirect(fun _ line -> lines.Add line))
                                Baked.SemVer.Stages.bumpArgument (InputSpec.ret [ project ])
                            }
                        }
                    }

                Expect.equal (built.Parse("minor --ci false").Invoke()) 0 "the bump should succeed"
                Expect.sequenceEqual lines [ $"{project}: 1.2.3 -> 1.3.0" ] "the report should reach the stage's redirect"
                Expect.stringContains (File.ReadAllText project) "<Version>1.3.0</Version>" "the project file should carry the new version"
            finally
                directory.Delete true
        }
    ]
