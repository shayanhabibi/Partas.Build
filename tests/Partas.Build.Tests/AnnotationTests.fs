module Partas.Build.Tests.AnnotationTests

open System
open System.IO
open Expecto
open Spectre.Console
open Partas.Build
open Partas.Build.Internal
open Partas.Build.Tests.Helpers

[<Tests>]
let tests = testList "annotations" [
    test "stage failures use inherited GitHub detection and escape title properties" {
        let parent = pipeline "ci" { envVars [ "GITHUB_ACTIONS", "true" ] }
        let ctx = { stage "compile,100%:source\r\nnext" { () } with ParentContext = ValueSome(StageParent.Pipeline parent) }
        let _, text = capturingOut (fun () -> StageContext.printError ctx "first 100%\r\nsecond")
        Expect.stringContains text
            "::error title=[STAGE] compile%2C100%25%3Asource%0D%0Anext::first 100%25%0D%0Asecond"
            "inherited CI settings and both escaping rules reach the runner"
    }

    test "pipeline failures respect explicit local detection even when GITHUB_ENV exists" {
        let ctx = pipeline "local" { envVars [ "GITHUB_ACTIONS", "false"; "GITHUB_ENV", "unused" ] }
        let _, text = capturingOut (fun () -> PipelineContext.printError ctx "literal [bold]message[/]")
        Expect.isFalse (text.Contains "::error") "an environment-file path is not the CI detection flag"
        Expect.stringContains text "literal [bold]message[/]" "local diagnostics keep literal markup"
    }

    test "pipeline failures escape properties without losing title characters" {
        let ctx = pipeline "build:one,two\nnext" { envVars [ "GITHUB_ACTIONS", "true" ] }
        let _, text = capturingOut (fun () -> PipelineContext.printError ctx "reason")
        Expect.stringContains text "::error title=[PIPELINE] build%3Aone%2Ctwo%0Anext::reason"
            "titles preserve commas, colons and newlines without command injection"
    }

    test "annotation operations are deferred and reporting an error does not fail a stage" {
        let capture = OutputCapture.create()
        let built = command "ci" {
            pipeline "ci" {
                quiet
                envVars [ "GITHUB_ACTIONS", "true" ]
                stage "diagnostics" {
                    captureOutput capture
                    runOperation (annotate (Annotation.notice "information"))
                    runOperation (annotate (Annotation.warning "caution"))
                    runOperation (annotate (Annotation.error "reported error"))
                    echo "step output"
                }
            }
        }
        let _, explanation = capturingOut (fun () -> built.Parse("--explain").Invoke())
        Expect.isFalse (explanation.Contains "::notice::") "explaining a pipeline emits no annotations"
        let result, text = capturingOut (fun () -> built.Parse("").Invoke())
        Expect.equal result 0 "an error annotation reports a diagnostic without failing execution"
        for expected in [ "::notice::information"; "::warning::caution"; "::error::reported error" ] do
            Expect.stringContains text expected "each level reaches the runner even in quiet captured stages"
        Expect.equal (OutputCapture.lines capture) [ "step output" ] "annotations bypass the step output sink"
    }

    test "source locations and titles escape protocol properties separately from messages" {
        let diagnostic = {
            Annotation.warning "100%\r\nmessage:with,commas" with
                Title = ValueSome "title:100%,one\r\ntwo"
                File = ValueSome "src/one,two:three.fs"
                Line = ValueSome 12
                EndLine = ValueSome 12
                Column = ValueSome 3
                EndColumn = ValueSome 8
        }
        let built = pipeline "ci" {
            envVars [ "GITHUB_ACTIONS", "true" ]
            stage "diagnostic" { runOperation (annotate diagnostic) }
        }
        let _, text = capturingOut (fun () -> PipelineContext.run built)
        Expect.stringContains text
            "::warning title=title%3A100%25%2Cone%0D%0Atwo,file=src/one%2Ctwo%3Athree.fs,line=12,endLine=12,col=3,endColumn=8::100%25%0D%0Amessage:with,commas"
            "all location fields are emitted and no property can inject a new field or command"
    }

    test "a stage can override inherited GitHub detection and keep local diagnostic metadata" {
        let diagnostic = {
            Annotation.notice "literal [bold]text[/]" with
                Title = ValueSome "title"
                File = ValueSome "src/Build.fs"
                Line = ValueSome 12
                Column = ValueSome 3
        }
        let built = pipeline "ci" {
            envVars [ "GITHUB_ACTIONS", "true" ]
            stage "local" {
                envVars [ "GITHUB_ACTIONS", "false" ]
                runOperation (annotate diagnostic)
            }
        }
        let _, text = capturingOut (fun () -> PipelineContext.run built)
        Expect.isFalse (text.Contains "::notice") "the nearer environment declaration wins"
        for expected in [ "Notice:"; "title"; "src/Build.fs"; "line 12"; "col 3"; "literal [bold]text[/]" ] do
            Expect.stringContains text expected "local output preserves metadata and literal text"
    }

    test "annotation I/O errors do not fail otherwise successful work" {
        let built = command "ci" {
            pipeline "ci" {
                quiet
                envVars [ "GITHUB_ACTIONS", "true" ]
                stage "work" { runOperation (annotate (Annotation.warning "message")) }
            }
        }
        let result, _ = capturingOut (fun () ->
            use writer =
                { new StringWriter() with
                    override _.Write(text: string) =
                        if not (isNull text) && text.Contains "::warning" then raise (IOException "unavailable")
                    override _.WriteLine(text: string) =
                        if not (isNull text) && text.Contains "::warning" then raise (IOException "unavailable") }
            Console.SetOut writer
            AnsiConsole.Console <- AnsiConsoleSettings (Out = AnsiConsoleOutput writer) |> AnsiConsole.Create
            built.Parse("").Invoke())
        Expect.equal result 0 "protocol I/O failures are best effort"
    }

    test "invalid source positions fail with an actionable configuration error" {
        for diagnostic in [
            { Annotation.warning "message" with Line = ValueSome 0 }
            { Annotation.warning "message" with Line = ValueSome 5; EndLine = ValueSome 3 }
            { Annotation.warning "message" with Line = ValueSome 1; Column = ValueSome 6; EndColumn = ValueSome 4 }
            { Annotation.warning "message" with Line = ValueSome 5; EndLine = ValueSome 6; Column = ValueSome 2 }
            { Annotation.warning "message" with EndLine = ValueSome 6 }
            { Annotation.warning "message" with Column = ValueSome 2 }
            { Annotation.warning "message" with Line = ValueSome 1; EndColumn = ValueSome 6 }
        ] do
            let built = stage "invalid annotation" {
                envVars [ "GITHUB_ACTIONS", "true" ]
                runOperation (annotate diagnostic)
            }
            let report = quietly (fun () -> reportStage built)
            Expect.isTrue (ScopeReport.failed report) "invalid metadata must not silently produce a rejected runner command"
            Expect.isTrue (report.Failures |> List.exists (fun failure ->
                match failure.Cause with
                | FailureCause.Raised (:? ArgumentException) -> true
                | _ -> false)) "the report retains the configuration error"
    }

    test "multiline source ranges retain both lines without columns" {
        let diagnostic = {
            Annotation.error "invalid block" with
                File = ValueSome "src/Build.fs"
                Line = ValueSome 5
                EndLine = ValueSome 8
        }
        let built = pipeline "ci" {
            envVars [ "GITHUB_ACTIONS", "true" ]
            stage "check" { runOperation (annotate diagnostic) }
        }
        let _, text = capturingOut (fun () -> PipelineContext.run built)
        Expect.stringContains text "::error file=src/Build.fs,line=5,endLine=8::invalid block"
            "multiline ranges reach GitHub with both lines and no discarded column properties"
    }
]
