# Local Setup

## Prerequisites

- .NET 8 SDK.
- Aspire templates installed locally.
- PowerShell for scripts in `/scripts`.

## Build And Test

```powershell
./scripts/build.ps1
./scripts/test.ps1
```

Equivalent direct commands:

```powershell
dotnet build src/UsageTracker.sln
dotnet test src/UsageTracker.sln
```

## Run The Functions App

```powershell
./scripts/run-functions.ps1
```

This wraps `func start`. The Functions app listens on:

```text
http://localhost:7071
```

All routes use the `api` prefix. Useful endpoints:

```text
POST   http://localhost:7071/api/hooks/claude-code
POST   http://localhost:7071/api/hooks/github-copilot
POST   http://localhost:7071/api/hooks/cursor
GET    http://localhost:7071/api/dashboard/sessions
GET    http://localhost:7071/api/dashboard/usage
GET    http://localhost:7071/api/dashboard/projects
GET    http://localhost:7071/api/dashboard/events/{id}
POST   http://localhost:7071/api/context/project
GET    http://localhost:7071/api/context/project
GET    http://localhost:7071/api/health
```

To let the Blazor dashboard call the Functions app cross-origin during local
development, start `func` with CORS enabled, e.g. `func start --cors "*"`.

## Run With Aspire

```powershell
./scripts/run-apphost.ps1
```

The Aspire dashboard shows the Functions app, the Cosmos DB emulator, and the Blazor
dashboard. A full run needs Docker (Cosmos emulator and Azurite storage for the
Functions host).

## Development Project Context

The project context endpoint requires a resolved user. Until Entra ID auth is wired in, development runs can use fake dev headers:

```powershell
$body = @{
  projectKey = "token-optimization"
  projectName = "Token Optimization"
  platform = "copilot"
  cwd = "C:\Users\RyanBrown\Projects\token-optimization"
  expiresInMinutes = 240
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:7071/api/context/project `
  -ContentType application/json `
  -Headers @{ "X-Dev-User-Email" = "local@example.com"; "X-Dev-User-Name" = "Local Dev" } `
  -Body $body
```

The request body requires `projectKey` and `projectName`. It does not accept a user field; the app derives the user from token claims or development headers. Project context can also be set through the MCP project-context tools hosted inside `UsageTracker.Functions` (see [mcp-project-context.md](v2/mcp-project-context.md)).

## Hook Config Samples

Root-level sample files are provided for local testing:

- `claude-code.settings.json`
- `copilot.hooks.json`

The repository-level Copilot hook file is committed at:

- `.github/hooks/usage-tracking.json`

These samples point at the local hook endpoint. If you change the local port, update the hook configuration to match the Functions app URL (default `http://localhost:7071/api/hooks/{platform}`).

For local Copilot HTTP hooks, set this in the environment where Copilot runs:

```powershell
$env:COPILOT_HOOK_ALLOW_LOCALHOST = "1"
```

Copilot cloud agent cannot call `localhost`; use a public HTTPS endpoint and any required outbound allow-listing for cloud-agent runs.

## Manual Smoke Test

```powershell
$payload = @{
  hook_event_name = "SessionStart"
  session_id = "local-smoke-test"
  model = "test-model"
  user = @{ email = "local@example.com" }
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post -Uri http://localhost:7071/api/hooks/github-copilot -ContentType application/json -Body $payload
Invoke-RestMethod -Uri http://localhost:7071/api/dashboard/sessions
Invoke-RestMethod -Uri http://localhost:7071/api/dashboard/usage
```