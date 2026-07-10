# Hook Configuration

UsageTracker collects observational events from coding agents using each tool's native hook mechanism. Events arrive at the Function App (`UsageTracker.Functions`) over HTTP; the platform is determined by the route, never guessed from the payload.

## Endpoints

| Route | Platform tag stored |
|---|---|
| `POST /api/hooks/claude-code` | `claude-code` |
| `POST /api/hooks/copilot` | `copilot` |

The Function App listens on `http://localhost:7071` locally (start it with `./scripts/run-functions.ps1`). Hook events here are observational only — no PreToolUse decision payloads are emitted, so a non-2xx response would only log an error on the agent side, never block a turn.

## Claude Code

Claude Code uses a project-level (or user-level) settings file. The working configuration is committed at `.claude/settings.json`.

### Supported events

| Hook event | Notes |
|---|---|
| `SessionStart` | Recorded for session open. |
| `PreToolUse` | Matcher `.*` — all tools. |
| `PostToolUse` | Matcher `.*` — all tools. Transcript is usually available here. |
| `Stop` | Session stop. |
| `SubagentStop` | Subagent stop. |

### Config shape

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": ".*",
        "hooks": [
          {
            "type": "command",
            "command": "curl.exe -s -X POST http://localhost:7071/api/hooks/claude-code -H \"Content-Type: application/json\" --data-binary \"@-\""
          }
        ]
      }
    ]
  }
}
```

Claude Code writes the event JSON to the command's STDIN. `--data-binary "@-"` reads STDIN as the request body. The quotes around `"@-"` are required — without them PowerShell interprets `@-` as a splatting token and the command fails.

### File locations

Claude Code loads hook config from (highest to lowest precedence):

1. Project-level: `.claude/settings.json` in the repo root — used here.
2. User-level: `~/.claude/settings.json`.

### Token extraction

When a hook payload includes `transcript_path`, `TranscriptTokenReader` reads new lines from the JSONL transcript file and accumulates `inputTokens`, `outputTokens`, `cacheReadTokens`, and `cacheCreationTokens`. The reader tracks the last-read offset per path so repeated firings for the same session do not double-count.

When no transcript path is present, `TokenUsage.FromPayload` tries to read inline `usage` fields from the payload root.

## GitHub Copilot

GitHub Copilot loads repo-level hooks from `.github/hooks/*.json`. The configuration is committed at `.github/hooks/usage-tracking.json`.

### Supported events

| Hook event | Notes |
|---|---|
| `SessionStart` | Recorded for session open. |
| `SessionEnd` | Recorded for session close. |
| `UserPromptSubmit` | Fires when a user message is submitted. |
| `PreToolUse` | Fires before each tool call. |
| `PostToolUse` | Fires after each tool call. |
| `Stop` | Session stop. |
| `SubagentStop` | Subagent stop. |

### Config shape

```json
{
  "version": 1,
  "hooks": {
    "PostToolUse": [
      {
        "type": "command",
        "command": "curl.exe -s -X POST http://localhost:7071/api/hooks/copilot -H \"Content-Type: application/json\" --data-binary \"@-\"",
        "timeoutSec": 10
      }
    ]
  }
}
```

The Copilot hook format does not use a `matcher` wrapper. Each event maps directly to a list of command entries. The same `"@-"` quoting rule applies.

### Localhost requirement

Copilot running locally (VS Code agent) can call `localhost`. Set this environment variable before starting VS Code or the Copilot session if the hook is blocked:

```powershell
$env:COPILOT_HOOK_ALLOW_LOCALHOST = "1"
```

Copilot cloud agent cannot call `localhost`. For cloud-agent runs, replace `http://localhost:7071` with a publicly reachable HTTPS endpoint.

## Payload Normalization

`HookEvent.FromJson` extracts a normalized event from the raw JSON regardless of platform. Fields are resolved with alias lists and null-safe fallbacks:

| Normalized field | Claude Code source | Copilot source |
|---|---|---|
| `EventName` | `hook_event_name` | `hook_event_name` |
| `SessionId` | `session_id` | `session_id` |
| `ToolName` | `tool_name` | `tool_name` |
| `TranscriptPath` | `transcript_path` | — |
| `Cwd` | `cwd` | `cwd` |
| `Model` | `model` | `model` |
| `UserId` | `user_id` / `user.id` | `user_id` / `user.id` |
| `UserName` | `user_name` / `user.name` | `user_name` / `user.name` |
| `UserEmail` | `user_email` / `user.email` | `user_email` / `user.email` |

If a resolved HTTP user is available (from token claims or `X-Dev-User-*` headers), it takes precedence over any user fields in the payload.

## User Identity Caveats

Neither Claude Code nor GitHub Copilot currently injects a verified user identity into hook payloads. In local development:

- User fields in the payload are typically absent or `null`.
- The dev-header fallback (`X-Dev-User-Email`, `X-Dev-User-Id`, `X-Dev-User-Name`) can be used to inject a synthetic identity for testing.
- Verified user resolution via Entra ID token claims is planned but not yet implemented.

## Manual Smoke Test

```powershell
# Claude Code hook
$payload = '{"hook_event_name":"PostToolUse","session_id":"smoke-1","tool_name":"edit"}'
$payload | curl.exe -s -X POST http://localhost:7071/api/hooks/claude-code `
  -H "Content-Type: application/json" --data-binary "@-"

# Copilot hook
$payload = '{"hook_event_name":"PostToolUse","session_id":"smoke-2","tool_name":"edit"}'
$payload | curl.exe -s -X POST http://localhost:7071/api/hooks/copilot `
  -H "Content-Type: application/json" --data-binary "@-"

# Check recorded events
curl.exe -s http://localhost:7071/api/dashboard/sessions
curl.exe -s http://localhost:7071/api/dashboard/usage
```

Expected response from each hook call: `200`. The dashboard reads should show incrementing session and tool-call counts for the `claude-code` and `copilot` platforms.
