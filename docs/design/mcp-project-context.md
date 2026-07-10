# V2 MCP Project Context

> **Status: implemented.** The four tools below are hosted directly inside
> `UsageTracker.Functions` (`Functions/ProjectContextMcpTools.cs`) as native Azure
> Functions MCP tool triggers, using the `Microsoft.Azure.Functions.Worker.Extensions.Mcp`
> NuGet package (`[McpToolTrigger]` / `[McpToolProperty]`). Each tool calls
> `IProjectContextService` directly — there is no separate MCP process and no HTTP
> self-proxy. The extension exposes the tools over remote/SSE transport rather than
> stdio, so MCP clients connect to the Function App's MCP endpoint over the network
> instead of launching a local executable. The attribution model in this doc carries
> forward the original design and is still accurate.

Project context gives agents an explicit way to say which project is active, instead of
forcing the hook stream to infer everything. Setting context is kept separate from
normal hook ingestion.

## MCP tools

| Tool | Purpose |
| --- | --- |
| `usage_set_project_context` | Set the active project for a session/platform. |
| `usage_get_project_context` | Read the current active project. |
| `usage_clear_project_context` | Close the active project window. |
| `usage_list_recent_projects` | List recent projects for the user. |

Tools run inside the Function App and call `ProjectContextService` directly:

```
Agent MCP client (SSE/remote transport)
  -> UsageTracker.Functions ProjectContextMcpTools (McpToolTrigger)
  -> UsageTracker.Library Runtime/Context (IProjectContextService)
  -> persistence
```

Configuration (`UsageTracker.Functions`, `local.settings.json`):

- `AzureWebJobsStorage` — backs the MCP extension's SSE transport (Azure Queue Storage);
  set to `UseDevelopmentStorage=true` locally, which requires Azurite (standalone or via
  the Aspire AppHost).
- `Mcp:DefaultUserEmail` / `Mcp:DefaultUserId` / `Mcp:DefaultUserName` — Development-only
  fallback identity used when a tool invocation carries no `HttpContext` (the MCP tool
  trigger path never has one), read by `FunctionsUserContext.cs`.

MCP callers use `projectId`; the tool maps it to the Functions app's `projectKey`.

To connect an MCP client (Claude Code, Copilot, etc.) locally, point it at the Function
App's MCP SSE endpoint (see Azure Functions MCP extension docs) instead of launching a
local executable — e.g. via the `mcp-remote` bridge or any client with native remote/SSE
MCP support.

Example set-context payload (MCP-side):

```json
{
  "projectId": "wealthspire-ticketing",
  "projectName": "Wealthspire Ticketing",
  "sessionId": "abc123",
  "platform": "claude-code"
}
```

## Attribution model

Usage attribution is a time-bounded context window, not a single global variable. A
context window records:

- Which user is active.
- Which project is active.
- When that project became active.
- Optionally which platform/session/workspace the project applies to.
- When the context expires or is superseded.

This supports manual selection now and programmatic, session-aware selection later.

## Hook attribution ordering

When a hook event arrives, assign a project using this order:

1. Explicit project metadata on the hook payload, if a platform ever provides it.
2. Active context matching `user + platform + sessionId`.
3. Active context matching `user + sessionId`.
4. Active context matching `user + cwd` or a configured workspace alias.
5. Active context matching only `user`, if there is exactly one unexpired active
   project for that user.
6. `unknown` project with low attribution confidence.

Store the selected `projectKey` and `attributionConfidence` on each normalized event.
That preserves the attribution decision that was true when the event arrived.

## Attribution confidence

- `explicit` — project came directly from payload or caller-provided context.
- `session` — matched by session id.
- `workspace` — matched by current working directory or workspace alias.
- `user-window` — matched only by the user's active project window.
- `unknown` — no safe project match.
- `ambiguous` — multiple active contexts matched.

## Concurrent project challenge

Manual user-level context is not enough for reliable attribution when a user works in
multiple projects at once. If two sessions are active and the payload does not expose
enough metadata to distinguish them, the service should not pretend attribution is
exact — it should record `ambiguous`. MCP session-correlated context is the primary
mitigation.

## Metrics to track

Store raw event facts and compute metrics from them:

- Token totals by platform, model, user, project, and time window.
- Input, output, cache creation, and cache read tokens.
- Tool call count.
- Session count.
- Event count by event type.
- First seen and last seen timestamps per session/project.
- Attribution confidence counts.

Useful derived metrics:

- Total tokens = input + output + cache creation + cache read.
- Billable token estimate, if pricing is added later.
- Tokens per session.
- Tokens per active project hour.
- Tool calls per session.

## Open decisions

- Whether `user` is supplied by the caller or derived from authentication once auth
  exists.
- Default context expiration window. Four hours is a reasonable starting value.
- Whether unknown/ambiguous events appear in project reports by default.
