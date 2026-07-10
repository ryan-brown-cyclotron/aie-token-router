---
applyTo: "src/UsageTracker.Library/**,src/UsageTracker.Functions/**,src/UsageTracker.Tests/**,src/UsageTracker.ServiceDefaults/**,src/UsageTracker.AppHost/**"
description: "Use when editing .NET backend projects for hook ingestion, usage accounting, Aspire hosting, or tests."
---

# Backend Instructions

These conventions apply to the .NET backend implementation for the hook usage tracker.

## UsageTracker.Library

Reusable behavior lives here, in a flat `UsageTracker` namespace, organized into layers:

```
Domain/            # HookEvent, ProjectContext, IUserContext, NormalizedUsageEvent, ToolOutputCompression
Infrastructure/    # UsageStore, IUsageRepository (InMemory/Cosmos), TranscriptTokenReader, IToolOutputCompressor (extension point, no shipped implementation)
Runtime/           # HookIngestionService, ProjectContextService, DashboardQueryService, ProjectAttributionService, ToolOutputCompressionService
DependencyInjection/  # AddUsageTrackerLibrary(IServiceCollection, IConfiguration)
```

Note the DI lifetimes: `UsageStore`, `TranscriptTokenReader`, `IProjectAttributionService`,
and `IUsageRepository` are singletons (stateful process-level caches / Cosmos client);
the ingestion, context, dashboard, and compression services are scoped. The host (not
the library) registers `IUserContext`.

## UsageTracker.Functions

The thin HTTP boundary — an Azure Functions .NET 8 isolated worker (ASP.NET Core
integration, route prefix `api`, `AuthorizationLevel.Anonymous`).

```
Functions/                    # HookIngestion, ProjectContext, DashboardQuery, Health functions
Functions/ProjectContextMcpTools.cs  # MCP tool triggers: usage_set/get/clear_project_context, usage_list_recent_projects
FunctionsUserContext.cs        # host IUserContext (token claims, X-Dev-User-* in Development, Mcp:DefaultUser* fallback for MCP invocations)
HttpContextHolder.cs           # worker middleware captures HttpContext (IHttpContextAccessor is unreliable here)
Program.cs                     # AddServiceDefaults + ConfigureFunctionsWebApplication + AddUsageTrackerLibrary
```

Each function parses the request, calls a library runtime service, and returns the
platform-specific response. Keep domain, attribution, storage, token, and compression
logic in the library, not in the functions.

`ProjectContextMcpTools.cs` hosts the four `usage_*_project_context` /
`usage_list_recent_projects` tools as native Azure Functions MCP tool triggers
(`Microsoft.Azure.Functions.Worker.Extensions.Mcp`, `[McpToolTrigger]` /
`[McpToolProperty]`), calling `IProjectContextService` directly — no HTTP self-proxy.
The extension exposes these over remote/SSE transport rather than stdio, backed by
Azure Queue Storage (`AzureWebJobsStorage`, `UseDevelopmentStorage=true` locally via
Azurite). MCP tool invocations carry no `HttpContext`, so `FunctionsUserContext.cs`
falls back to a single configured identity (`Mcp:DefaultUserEmail` /
`Mcp:DefaultUserId` / `Mcp:DefaultUserName`) in Development when no HttpContext is
present.

## Normalization

- Platform is determined by route, not guessed from payload shape.
- Normalize common fields into `HookEvent`, including event name, session id, tool name, transcript path, model, cwd, and user identity.
- Support snake_case, camelCase, and PascalCase aliases when adding fields.
- Missing user/model/session fields must not crash ingestion.

## Token Accounting

- Transcript files are JSONL and can be written while being read.
- Count only complete lines.
- Do not count the same transcript line twice.
- Keep malformed or partial transcript lines non-fatal.

## Testing

- Add or update tests when changing `HookEvent`, `TranscriptTokenReader`, or `UsageStore`.
- Prefer small unit tests over broad web host tests unless route behavior changes.