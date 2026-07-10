# V2 Blazor Dashboard

> **Status: implemented.** `UsageTracker.Dashboard` is a net8 Blazor WebAssembly app. It
> reads from Function endpoints via `DashboardClient` (a scoped `HttpClient`) and does
> not duplicate runtime logic.

## Pages

Implemented pages: `Home`, `Sessions`, `Projects`, `AgentUsage` (under `Pages/`), which
together cover sessions, project usage, agent/platform activity, and raw-event
drill-in.

## Read endpoints

The dashboard calls the Function App, not a separate API:

```
UsageTracker.Dashboard
  -> GET /api/dashboard/sessions
  -> GET /api/dashboard/projects
  -> GET /api/dashboard/usage
  -> GET /api/dashboard/events/{id}
```

## Write behavior

The dashboard does not write project context directly. Project context is set through
MCP tools (see [mcp-project-context.md](mcp-project-context.md)). An admin override in
the dashboard is a possible later addition, not a V2 requirement.

## Configuration

The Functions base URL comes from `Functions:BaseUrl`. Locally
(`wwwroot/appsettings.Development.json`) it is `http://localhost:7071/`.

## Hosting note

Azure Static Web Apps supports deploying Blazor WebAssembly apps with an Azure
Functions API backend. Deployed through Static Web Apps the dashboard and Functions are
same-origin, so no CORS configuration is needed. For **local** development the WASM app
and `func` run on different origins, so start the Functions app with CORS enabled, for
example `func start --cors "*"`.

This is a different deployment target than `UsageTracker.Functions` itself, which
targets Azure Container Apps when run standalone (see [hosting.md](hosting.md)). The
two are not mutually exclusive: Static Web Apps' "bring your own Functions API" model
still requires a Functions app to point at, and Container Apps is one valid way to host
that Functions app; the two docs describe complementary halves of the same deployment,
not competing choices.

## References

- [Deploy a Blazor app with Azure Static Web Apps](https://learn.microsoft.com/en-us/azure/static-web-apps/deploy-blazor)
