# Architecture

> **This document describes the retired V1 `UsageTracker.Api` path.** `UsageTracker.Api`
> has been **deleted**. The implemented architecture is now an Azure Functions boundary
> (`UsageTracker.Functions`) over a reusable `UsageTracker.Library`, with a Blazor
> dashboard and MCP project-context tools hosted inside the Functions app. See
> [v2/README.md](v2/README.md) and the topic docs under
> [v2/](v2/). The routes and hosting notes below no longer match `src/`; they are kept
> as historical context for how the concepts (normalization, token accounting, storage,
> dev auth) map into the current library. Where they still apply, the V2 equivalents are
> noted inline.

## Components

```
Agent hook config
  -> UsageTracker.Api
      -> HookEvent normalization
      -> TranscriptTokenReader token extraction
      -> UsageStore in-memory session index
      -> IUsageRepository persisted event/context store
      -> /usage read endpoints

UsageTracker.AppHost
  -> orchestrates UsageTracker.Api and Cosmos DB emulator for local Aspire runs
```

## API Surface

- `POST /hooks/claude-code`
- `POST /hooks/copilot`
- `PUT /usage/context/active`
- `DELETE /usage/context/active?projectKey={projectKey}`
- `GET /usage/summary`
- `GET /usage/sessions`
- `GET /usage/metrics`
- Development health endpoints from Aspire ServiceDefaults: `/health` and `/alive`

## Normalized Event Fields

`HookEvent` currently extracts:

- Platform from the route.
- Event name.
- Session id.
- Tool name.
- Transcript path.
- Current working directory.
- Model.
- User identity aliases: user id, user name, and user email.

Payloads are treated as unstable vendor contracts. Normalization uses alias lists and null-safe fallbacks instead of strict DTO binding.

## Token Accounting

Hook payloads usually do not contain full token usage. When a payload includes `transcript_path`, `TranscriptTokenReader` tails the JSONL transcript file and counts new complete lines only. This avoids recounting tokens when multiple hook events fire for the same session.

## Storage

Normalized hook events and project context windows are stored through `IUsageRepository`. When Cosmos configuration is available, `CosmosUsageRepository` stores events in the `events` container and context windows in the `projectContexts` container. The Aspire AppHost runs Cosmos DB as a local emulator in development and passes the database reference to the API.

The current session read model remains in memory for `/usage/sessions` and `/usage/summary`. Durable session aggregates are a follow-up if restart-safe session history is required.

## Development Auth

Project context endpoints resolve the user from token claims. Until Entra ID authentication is wired in, development runs can use these headers:

- `X-Dev-User-Id`
- `X-Dev-User-Email`
- `X-Dev-User-Name`

The request body does not accept a user field.

## Deployment Shape

For local development, the API defaults to `http://localhost:5179` so hook examples work immediately. For Aspire, Docker, and Azure Container Apps, host-provided URL and port configuration takes precedence.

## V2 Direction (now implemented)

The architecture above is the retired V1 path. The restructure has shipped: the ASP.NET
API was replaced by an Azure Functions boundary (`UsageTracker.Functions`) over a
reusable `UsageTracker.Library`, with a Blazor dashboard (`UsageTracker.Dashboard`), MCP
project-context tools hosted directly inside `UsageTracker.Functions` (native Azure
Functions MCP tool triggers over remote/SSE transport), and an optional tool-output
compression extension point (`IToolOutputCompressor`) with no implementation shipped by
default. `UsageTracker.Api` no longer exists. See [v2/README.md](v2/README.md) for the
current design.