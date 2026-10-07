module Partas.Build.Tests.AiEnvironmentTests

open System
open System.IO
open System.Text.Json
open Expecto
open Partas.Build

let private withEnvironment values action =
    let originals = values |> List.map (fun (name, _) -> name, Environment.GetEnvironmentVariable name)
    try
        for name, value in values do Environment.SetEnvironmentVariable(name, value)
        action ()
    finally
        for name, value in originals do Environment.SetEnvironmentVariable(name, value)

let private detect values =
    let environment = Map.ofList values
    (AiEnvironment.detectWith (fun name -> Map.tryFind name environment |> Option.defaultValue null)).Value

[<Tests>]
let detectionTests =
    testList "AI environment" [
        testCase "generic assertions accept agent names and reject false-like values" (fun () ->
            Expect.isFalse (detect []) "an empty environment is not an agent"
            for name in [ "AGENT"; "AI_AGENT" ] do
                for value in [ "codex"; "goose@1.2.3"; "custom-harness"; "1"; " true " ] do
                    Expect.isTrue (detect [ name, value ]) $"{name}={value} asserts an agent"
                for value in [ ""; "  "; "0"; " false "; "NO"; "Off" ] do
                    Expect.isFalse (detect [ name, value ]) $"{name}={value} is not a signal"
            Expect.isTrue (detect [ "AGENT", "false"; "KILO", "1" ]) "generic false does not disable another marker")

        testCase "legacy markers retain their documented nonempty predicates" (fun () ->
            for name in [ "CLAUDECODE"; "CLAUDE_CODE_ENTRYPOINT"; "CURSOR_AGENT";
                          "CURSOR_SANDBOX"; "GEMINI_CLI"; "QWEN_CODE"; "CODEX_THREAD_ID"; "CODEX_SANDBOX";
                          "CODEX_SANDBOX_NETWORK_DISABLED"; "CODEX_CI"; "CODEBUDDY"; "VECLI_DIR";
                          "CLINE_ACTIVE"; "ROO_CODE_TASK_ID"; "OPENCODE_PID"; "COPILOT_AGENT_JOB_ID";
                          "PI_CODING_AGENT"; "KIRO_AGENT_PATH"; "AMP_CURRENT_THREAD_ID"; "GOOSE_TERMINAL";
                          "ANTIGRAVITY_PROJECT_ID"; "AUGMENT_AGENT"; "CRUSH"; "IFLOW_CLI"; "OZ_RUN_ID";
                          "FIREBENDER_TERMINAL"; "TRAE_AI_SHELL_ID" ] do
                Expect.isTrue (detect [ name, "0" ]) $"{name} accepts any nonempty value"
                Expect.isFalse (detect [ name, "" ]) $"{name} rejects empty values")

        testCase "exact markers reject different case whitespace and false values" (fun () ->
            for name, value in [ "CLAUDE_CODE_CHILD_SESSION", "1"; "DSH_SHELL", "1"; "HERMES_AGENT", "true";
                                 "OPENCLAW_SHELL", "exec"; "KILO", "1"; "VTCODE", "1";
                                 "CURSOR_EXTENSION_HOST_ROLE", "agent-exec" ] do
                Expect.isTrue (detect [ name, value ]) $"{name} matches its exact marker"
                for wrong in [ " " + value; "false"; ""; "0" ] do
                    Expect.isFalse (detect [ name, wrong ]) $"{name} rejects {wrong}"
            Expect.isFalse (detect [ "HERMES_AGENT", "TRUE" ]) "exact markers are case sensitive")

        testCase "session fallbacks require nonblank values" (fun () ->
            for name in [ "CODEX_SESSION_ID"; "PI_SESSION_ID"; "HERMES_SESSION_ID"; "GROK_SESSION_ID"; "JUNIE_SHIM_PATH" ] do
                Expect.isTrue (detect [ name, "session-or-path" ]) $"{name} establishes a harness"
                Expect.isFalse (detect [ name, " \t" ]) $"{name} rejects whitespace")

        testCase "compound and substring signals keep their documented boundaries" (fun () ->
            Expect.isFalse (detect [ "CURSOR_TRACE_ID", "human-terminal" ]) "a Cursor human terminal has a trace too"
            Expect.isFalse (detect [ "PAGER", "head -n 10000 | cat" ]) "a pager alone does not establish a harness"
            Expect.isTrue (detect [ "CURSOR_TRACE_ID", "trace"; "PAGER", "head -n 10000 | cat" ]) "the pair identifies Cursor agent execution"
            Expect.isFalse (detect [ "CURSOR_TRACE_ID", "trace"; "PAGER", " head -n 10000 | cat" ]) "the pager predicate is exact"
            Expect.isFalse (detect [ "AGENT_CONTEXT_OUT", "fifo" ]) "one generic FIFO name is insufficient"
            Expect.isFalse (detect [ "AGENT_DISPLAY_OUT", "fifo" ]) "the other FIFO alone is insufficient"
            Expect.isTrue (detect [ "AGENT_CONTEXT_OUT", "fifo1"; "AGENT_DISPLAY_OUT", "fifo2" ]) "the pair identifies Kiro"
            Expect.isTrue (detect [ "PS1", "prefix ###PS1JSON### suffix" ]) "the OpenHands prompt marker matches"
            Expect.isTrue (detect [ "PROMPT_COMMAND", "###PS1JSON###" ]) "the alternate prompt marker matches"
            Expect.isTrue (detect [ "AWS_EXECUTION_ENV", "AmazonQ-For-CLI-1.0" ]) "the Amazon Q substring matches"
            Expect.isFalse (detect [ "AWS_EXECUTION_ENV", "AWS_Lambda_dotnet" ]) "ordinary AWS execution is not an agent")

        testCase "credentials configuration and human session metadata do not establish identity" (fun () ->
            Expect.isFalse (detect [ "COPILOT_GITHUB_TOKEN", "secret"; "COPILOT_MODEL", "model";
                                     "COPILOT_ALLOW_ALL", "1"; "REPL_ID", "workspace"; "GROK_AGENT", "profile";
                                     "DSH_SESSION_ID", "human-web-terminal"; "DEEPSEEK_API_KEY", "secret";
                                     "CODEX_VERSION", "1.0"; "CODEX_PERMISSION_PROFILE", "full";
                                     "TRACEPARENT", "trace"; "JUNIE_DATA", "path" ]) "only identity signals enable defaults")

        testCase "the disable override wins and short circuits marker reads" (fun () ->
            for value in [ "1"; "TRUE"; " yes "; "on" ] do
                let read name =
                    if name = "PARTAS_BUILD_DISABLE_AI" then value
                    else failwith "agent markers must not be read when disabled"
                Expect.isFalse ((AiEnvironment.detectWith read).Value) "disable is authoritative"
            for value in [ ""; " \t"; "0"; "FALSE"; " no "; "off" ] do
                Expect.isTrue (detect [ "PARTAS_BUILD_DISABLE_AI", value; "AGENT", "codex" ]) "a false override does not disable detection")

        testCase "detection is deferred and cached only within the returned lazy value" (fun () ->
            let mutable agent = null
            let reads = ResizeArray<string>()
            let read name =
                reads.Add name
                if name = "AGENT" then agent else null
            let result = AiEnvironment.detectWith read
            Expect.isEmpty reads "construction reads no environment"
            agent <- "codex"
            Expect.isTrue result.Value "first evaluation sees the current environment"
            let count = reads.Count
            agent <- null
            Expect.isTrue result.Value "the same lazy result stays stable"
            Expect.equal reads.Count count "forcing twice does not read again"
            Expect.isFalse ((AiEnvironment.detectWith read).Value) "a new detection sees changes")
    ]

[<Tests>]
let jsonDefaultTests =
    testList "AI JSON defaults" [
        testCase "a reused command defaults to JSON for an agent and honors the disable override" (fun () ->
            let root = Command.root { pipeline "build" { quiet; stage "work" { () } } }
            let invoke args =
                use output = new StringWriter()
                let result = root.Invoke(args, output = output)
                Expect.equal result.ExitCode 0 "the command still runs"
                output.ToString()

            withEnvironment [ "AGENT", "codex"; "PARTAS_BUILD_DISABLE_AI", "0" ] (fun () ->
                let text = invoke []
                let lastLine = text.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.last
                Expect.isTrue (lastLine.TrimStart().StartsWith "{") "the default appends JSON after step output"
                use document = JsonDocument.Parse lastLine
                Expect.equal (document.RootElement.GetProperty("outcome").GetString()) "succeeded" "the default writes a run result"
                Expect.isFalse ((invoke [ "--json"; "false" ]).Contains "\"formatVersion\"") "an explicit false wins over detection")

            withEnvironment [ "AGENT", "codex"; "PARTAS_BUILD_DISABLE_AI", "1" ] (fun () ->
                Expect.isFalse ((invoke []).Contains "\"formatVersion\"") "the override is read for the next invocation"
                let lastLine = (invoke [ "--json" ]).Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.last
                use document = JsonDocument.Parse lastLine
                Expect.equal (document.RootElement.GetProperty("outcome").GetString()) "succeeded" "the override does not suppress explicit JSON"))
    ] |> testSequenced
