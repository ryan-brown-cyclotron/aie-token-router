# V2 Function App

> **Status: implemented.** `UsageTracker.Functions` runs as a .NET 8 Azure Functions
> isolated worker with ASP.NET Core integration (`ConfigureFunctionsWebApplication`),
> route prefix `api`, and `AuthorizationLevel.Anonymous`. Verified end-to-end with
> `func start` on port `7071` (use `scripts/run-functions.ps1`).

The Function App is a thin trigger boundary. Each HTTP trigger parses the request,
calls a `UsageTracker.Library` runtime service, and returns the platform-specific
response. No controllers, no API-layer domain logic, no compression logic in the HTTP
layer.

```
HTTP trigger
  -> call UsageTracker.Library runtime service
  -> return platform-specific response
```

Use the .NET isolated worker model so the Function App owns startup and configuration
cleanly while keeping business logic in the library.

## What it owns

- Claude Code hook ingestion
- GitHub Copilot hook ingestion
- Cursor hook/webhook ingestion
- MCP tool calls for setting project context
- Dashboard read endpoints
- Basic health/status endpoints

## What it does not own

- Domain models
- Attribution logic
- Usage storage internals
- Token reading implementation
- Compression logic (delegated to an optional, host-registered `IToolOutputCompressor`)
- Project context runtime behavior

Those live in [UsageTracker.Library](library.md).

## Endpoints

| Function | Method + route | Purpose |
| --- | --- | --- |
| `HookIngestionFunctions` | `POST /api/hooks/{platform}` | Ingest a hook payload for `claude-code`, `github-copilot`, or `cursor`. |
| `ProjectContextFunctions` | `POST /api/context/project` | Set active project context (MCP/admin). |
| `ProjectContextFunctions` | `DELETE /api/context/project?projectKey=` | Clear the active project window. |
| `ProjectContextFunctions` | `GET /api/context/project` | Read the current active project. |
| `ProjectContextFunctions` | `GET /api/context/project/recent?limit=` | List recent projects for the user. |
| `DashboardQueryFunctions` | `GET /api/dashboard/sessions` | Session read model. |
| `DashboardQueryFunctions` | `GET /api/dashboard/usage` | Usage read model. |
| `DashboardQueryFunctions` | `GET /api/dashboard/projects` | Project read model. |
| `DashboardQueryFunctions` | `GET /api/dashboard/events/{id}` | Raw event drill-in by id. |
| `HealthFunctions` | `GET /api/health` | Health/status. |

## Identity and startup

`Program.cs` uses `FunctionsApplication.CreateBuilder`, then
`AddServiceDefaults`, `ConfigureFunctionsWebApplication`, and `AddUsageTrackerLibrary`.
`IUserContext` is registered by the host (not the library): the app registers
`HttpContextHolder` and `FunctionsUserContext`, plus a worker middleware that captures
the `HttpContext` for each invocation. This middleware exists because
`IHttpContextAccessor` is unreliable in the isolated worker. Identity resolves from
token claims, falling back to `X-Dev-User-*` headers in `Development`.

## Response contract

Hook ingestion is observational. It always returns `200` and never blocks an agent
turn. For platforms that support output replacement, the response carries the
platform-specific transformed result (see [platform behavior](#platform-behavior)).

## Function examples

Signatures below are illustrative. The implemented functions use the ASP.NET Core
integration model with `AuthorizationLevel.Anonymous` (not `HttpResponseData` +
`AuthorizationLevel.Function` as sketched here); see `src/UsageTracker.Functions` for
the actual code.

### Hook ingestion

```csharp
public sealed class HookIngestionFunctions
{
    private readonly IHookIngestionService _hooks;

    public HookIngestionFunctions(IHookIngestionService hooks)
    {
        _hooks = hooks;
    }

    [Function("IngestHook")]
    public async Task<HttpResponseData> IngestHookAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "hooks/{platform}")]
        HttpRequestData request,
        string platform,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(
            request.Body,
            cancellationToken: cancellationToken);

        var result = await _hooks.IngestAsync(
            platform,
            document.RootElement,
            cancellationToken);

        var response = request.CreateResponse(result.StatusCode);
        await response.WriteAsJsonAsync(result.ResponsePayload, cancellationToken);
        return response;
    }
}
```

### Project context

```csharp
public sealed class ProjectContextFunctions
{
    private readonly IProjectContextService _context;

    public ProjectContextFunctions(IProjectContextService context)
    {
        _context = context;
    }

    [Function("SetProjectContext")]
    public async Task<HttpResponseData> SetProjectContextAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "context/project")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var command = await request.ReadFromJsonAsync<SetProjectContextCommand>(
            cancellationToken);

        var result = await _context.SetAsync(command!, cancellationToken);

        var response = request.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(result, cancellationToken);
        return response;
    }
}
```

## Platform behavior

Platform is determined by the route, never inferred from the payload.

### GitHub Copilot

The only in-path compression target today. The Copilot post-tool-use hook is called
after a tool executes successfully and can transform or filter tool results. Documented
outputs include `modifiedResult`, `additionalContext`, and `suppressOutput`.

```
Copilot postToolUse
  -> /api/hooks/github-copilot
  -> raw store
  -> compress via IToolOutputCompressor, if a host registered one (no-op by default)
  -> return modifiedResult
```

### Claude Code

Observe-only. Output replacement has not been validated for Claude Code, so the original
output is preserved.

```
Claude PostToolUse
  -> /api/hooks/claude-code
  -> raw store
  -> normalize
```

### Cursor

Observe-only.

```
Cursor hooks/webhooks
  -> /api/hooks/cursor
  -> raw store
  -> normalize
```

Enable in-path compression for Claude Code and Cursor only after validating whether
their local hooks can return modified tool output.

## References

- [Azure Functions HTTP webhook trigger](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-http-webhook-trigger)
- [Guide for running C# Azure Functions in the isolated worker model](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)
- [GitHub Copilot SDK — post-tool-use hook](https://docs.github.com/en/copilot/how-tos/copilot-sdk/hooks/post-tool-use)
