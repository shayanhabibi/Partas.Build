namespace Partas.Build

open System

/// Cooperative agent detection for output defaults, following is-ai-agent's environment rules.
/// Inherited markers identify a harness; they do not prove that an AI initiated a command.
module AiEnvironment =
    /// Set to a nonblank value other than 0, false, no or off to disable automatic agent defaults.
    [<Literal>]
    let DisableVariable = "PARTAS_BUILD_DISABLE_AI"

    let private nonempty value = not (String.IsNullOrEmpty value)
    let private nonblank value = not (String.IsNullOrWhiteSpace value)
    let private enabled (value: string) =
        nonblank value
        && not ([ "0"; "false"; "no"; "off" ] |> List.contains (value.Trim().ToLowerInvariant()))

    // The README retains nonempty predicates for these legacy markers, including values such as "0".
    // Credentials, model configuration, and workspace IDs are deliberately excluded.
    let private legacyMarkers = [
        "CODEBUDDY"; "CODEBUDDY_SESSION_ID"; "CODEBUDDY_PROJECT_DIR"
        "CLAUDECODE"; "CLAUDE_CODE_ENTRYPOINT"; "CLAUDE_CODE_SESSION_ID"; "CLAUDE_CODE_EXECPATH"
        "CURSOR_AGENT"; "CURSOR_SANDBOX"
        "GEMINI_CLI"; "QWEN_CODE"; "VECLI_SANDBOX"; "VECLI_DIR"
        "CODEX_THREAD_ID"; "CODEX_SANDBOX"; "CODEX_SANDBOX_NETWORK_DISABLED"; "CODEX_CI"
        "ANTIGRAVITY_AGENT"; "ANTIGRAVITY_PROJECT_ID"; "AUGMENT_AGENT"
        "CLINE_ACTIVE"; "CLINE_TASK_ID"; "ROO_CODE_TASK_ID"; "CRUSH"; "IFLOW_CLI"; "OZ_RUN_ID"
        "PI_CODING_AGENT"; "KIRO_AGENT_PATH"; "FIREBENDER_TERMINAL"
        "OPENCODE"; "OPENCODE_PID"; "OPENCODE_BIN_PATH"; "OPENCODE_SERVER"
        "OPENCODE_APP_INFO"; "OPENCODE_MODES"; "OPENCODE_CLIENT"
        "TRAE_AI_SHELL_ID"; "GOOSE_TERMINAL"; "AMP_CURRENT_THREAD_ID"
        "COPILOT_AGENT_SESSION_ID"; "COPILOT_AGENT"; "COPILOT_CLI"; "COPILOT_AGENT_JOB_ID"
    ]

    /// A lazy detection using the supplied environment reader. Nothing is read until Value is requested.
    /// The result is cached within this Lazy; create a new one to observe a changed environment.
    /// Only environment signals are used: the README's /opt/.devin filesystem probe is excluded.
    let detectWith (readEnvironment: string -> string): Lazy<bool> = lazy (
        let any predicate names = names |> List.exists (readEnvironment >> predicate)
        let contains name text =
            let value = readEnvironment name
            nonempty value && value.Contains(text: string)

        if enabled (readEnvironment DisableVariable) then false
        else
            // Unknown generic names and bare true values also assert an agent context in the README.
            any enabled [ "AGENT"; "AI_AGENT" ]
            || any nonempty legacyMarkers
            || any nonblank [ "CODEX_SESSION_ID"; "PI_SESSION_ID"; "HERMES_SESSION_ID"; "GROK_SESSION_ID"; "JUNIE_SHIM_PATH" ]
            || ([
                    "CLAUDE_CODE_CHILD_SESSION", "1"
                    "CURSOR_EXTENSION_HOST_ROLE", "agent-exec"
                    "DSH_SHELL", "1"
                    "HERMES_AGENT", "true"
                    "OPENCLAW_SHELL", "exec"
                    "KILO", "1"
                    "VTCODE", "1"
                ] |> List.exists (fun (name, expected) -> readEnvironment name = expected))
            || (nonempty (readEnvironment "CURSOR_TRACE_ID") && readEnvironment "PAGER" = "head -n 10000 | cat")
            || (any nonempty [ "AGENT_CONTEXT_OUT" ] && any nonempty [ "AGENT_DISPLAY_OUT" ])
            || contains "PS1" "###PS1JSON###"
            || contains "PROMPT_COMMAND" "###PS1JSON###"
            || contains "AWS_EXECUTION_ENV" "AmazonQ-For-CLI")

    /// A fresh lazy detection of the process environment, evaluated only when its Value is requested.
    /// Use a fresh result for each invocation in a long-lived host.
    let detect () = detectWith Environment.GetEnvironmentVariable
