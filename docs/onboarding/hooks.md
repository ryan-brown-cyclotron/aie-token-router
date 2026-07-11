# Hook Configuration

UsageTracker collects observational events from coding agents using each tool's native hook mechanism.
Agents invoke the local `usagetracker` CLI (a command hook); the CLI forwards the event to the resident
daemon, which runs local compaction, attaches your verified Entra identity, and forwards to the backend.
The platform is passed as the CLI argument (`claude-code`, `copilot`), never guessed from the payload.

```
agent event (JSON on stdin) → usagetracker command <platform> --stdin → daemon → backend
```

Hook events are observational only — no PreToolUse decision payloads are emitted — and the CLI always
**fails open** (exit 0, no output) if the daemon is unreachable, so a hook never blocks a turn.

Run `usagetracker init` once to install the CLI and register the daemon (see [README.md](README.md)).

## Claude Code

Claude Code uses a project-level or user-level settings file. Copy [claude-code.settings.json](claude-code.settings.json),
or run `usagetracker setup claude` to generate it.

### Supported events

| Hook event | Notes |
|---|---|
| `SessionStart` | Session open. |
| `PreToolUse` | Matcher `*` — all tools. |
| `PostToolUse` | Matcher `*` — all tools. Transcript is usually available here. |
| `Stop` | Session stop. |
| `SubagentStop` | Subagent stop. |

(The full example also wires `SessionEnd`, `UserPromptSubmit`, `PostToolUseFailure`, `SubagentStart`, `PreCompact`.)

### Config shape

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "*",
        "hooks": [
          { "type": "command", "command": "usagetracker command claude-code --stdin", "timeout": 10 }
        ]
      }
    ]
  }
}
```

Claude Code writes the event JSON to the command's stdin; `--stdin` tells the CLI to read it. No
`USER_EMAIL` / header is needed — identity comes from the daemon's Entra token.

### File locations

1. Project-level: `.claude/settings.json` in the repo root (highest precedence).
2. User-level: `~/.claude/settings.json`.

> Contributing to UsageTracker itself? This repo's `.claude/settings.json` calls the locally-built CLI
> via `$CLAUDE_PROJECT_DIR` so you don't need a global install — see [README.md](README.md).

### Token extraction

When a hook payload includes `transcript_path`, `TranscriptTokenReader` reads new lines from the JSONL
transcript file and accumulates `inputTokens`, `outputTokens`, `cacheReadTokens`, and
`cacheCreationTokens`. Because the transcript is a **local file**, the daemon (running on your machine)
is what reads it — the cloud backend cannot. The reader tracks the last-read offset per path so repeated
firings for the same session do not double-count. When no transcript path is present,
`TokenUsage.FromPayload` reads inline `usage` fields from the payload root.

## GitHub Copilot

GitHub Copilot loads repo-level hooks from `.github/hooks/*.json` (must be on the default branch for the
cloud agent) or user-level hooks from `~/.copilot/hooks/*.json`. Copilot delivers each event as JSON on
stdin, so it uses the **same command-hook path as Claude Code**. Copy [copilot.hooks.json](copilot.hooks.json),
or run `usagetracker setup github`.

### Supported events

Copilot uses **camelCase** event names (PascalCase aliases such as `PreToolUse` also work for VS Code
compatibility):

| Hook event | Notes |
|---|---|
| `sessionStart` | Session open. |
| `sessionEnd` | Session close. |
| `userPromptSubmitted` | User message submitted. |
| `preToolUse` | Before each tool call. |
| `postToolUse` | After each tool call. |
| `errorOccurred` | Tool/agent error. |

### Config shape

Copilot hook entries use `bash` and `powershell` keys (chosen by OS) rather than a single `command`, and
there is no `matcher` wrapper:

```json
{
  "version": 1,
  "hooks": {
    "postToolUse": [
      {
        "type": "command",
        "bash": "usagetracker command copilot --stdin",
        "powershell": "usagetracker command copilot --stdin",
        "timeoutSec": 10
      }
    ]
  }
}
```

No `COPILOT_HOOK_ALLOW_LOCALHOST` or local token is needed — the command hook talks to the daemon over
the local pipe/socket via the CLI, not over HTTP. (Copilot also supports `http` and `prompt` hook types;
the command type is used here for parity with Claude Code and to avoid any localhost-HTTP restrictions.)

## Payload Normalization

`HookEvent.FromJson` extracts a normalized event from the raw JSON regardless of platform, using alias
lists and null-safe fallbacks:

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

The resolved caller identity (from the daemon's Entra token in production) takes precedence over any user
fields in the payload.

## User Identity

Coding agents do not inject a verified user identity into hook payloads. UsageTracker resolves it instead
from the **daemon's Entra token**, acquired via your enrolled device (see
[../design/daemon-cli.md](../design/daemon-cli.md)). In local development, the backend also accepts
`X-Dev-User-*` headers and `X-User-Email` for synthetic identities; those are ignored in production.

## Manual Smoke Test

Use `trace` to exercise the full path and see diagnostics on stderr while keeping stdout clean:

```bash
echo '{"hook_event_name":"PostToolUse","session_id":"smoke-1","tool_name":"edit"}' \
  | usagetracker trace claude-code --stdin

echo '{"hook_event_name":"postToolUse","session_id":"smoke-2","tool_name":"edit"}' \
  | usagetracker trace copilot --stdin

usagetracker status   # daemon health, auth state, remote endpoint
```

`stderr` shows `[trace]` lines (daemon, user, compaction, backend status). The dashboard reads should show
incrementing session and tool-call counts for the `claude-code` and `copilot` platforms.
