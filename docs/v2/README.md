# V2 Architecture Restructure

V2 restructured UsageTracker away from a single ASP.NET Core API and onto a thin
Azure Functions HTTP boundary, a reusable class library, a Blazor dashboard, and an
optional tool-output compression extension point. MCP project-context tools are
hosted directly inside the Functions app. This directory holds the design for that restructure, which is now
implemented in `src/`.

> **Status: implemented.** The V2 restructure has landed in `src/`. `UsageTracker.Api`
> has been deleted; the solution is now `UsageTracker.Library` + `UsageTracker.Functions`
> + `UsageTracker.Dashboard`, wired together by `UsageTracker.AppHost`. The Functions app
> also hosts the four MCP project-context tools as native Azure Functions MCP tool
> triggers (`Microsoft.Azure.Functions.Worker.Extensions.Mcp`), exposed over remote/SSE
> transport rather than as a separate stdio process. Where an endpoint, type name, or
> code sample below no longer matches `src/`, `src/` is authoritative. Remaining
> follow-ups are tracked in [roadmap.md](roadmap.md).

## Position

The workload is primarily hook ingestion,
event processing, and a dashboard — not a traditional resource API. Azure Functions
HTTP triggers are designed to invoke code from HTTP requests and are explicitly
supported for building serverless APIs and webhooks. The .NET isolated worker model
gives standard dependency injection, process-level control, and fewer assembly
conflicts, which fits the reusable-library pattern better than routing everything
through an ASP.NET controller layer.

The restructure in one sentence:

> `UsageTracker.Api` was removed, reusable logic moved into `UsageTracker.Library`,
> hook endpoints are exposed through `UsageTracker.Functions`, and a Blazor dashboard
> reads from the same runtime services. Project context is set through MCP tools
> hosted inside `UsageTracker.Functions`.

## What changed since the first V2 draft

The original V2 note (project context attribution) treated the MCP server and the
dashboard UI as deferred beyond V2. This restructure folds them back in:

- **MCP project context** is now a first-class input source. See
  [mcp-project-context.md](mcp-project-context.md).
- **The dashboard** becomes its own Blazor project. See [dashboard.md](dashboard.md).
- **Project attribution** (the entire prior V2 doc) is preserved and relocated into
  [mcp-project-context.md](mcp-project-context.md), which now owns the attribution
  model, confidence values, and hook attribution ordering.

## Outcome

`UsageTracker.Api` has been deleted. The implemented design uses:

- `UsageTracker.Functions` — the HTTP boundary (hook ingestion, project context,
  dashboard reads, health), an Azure Functions isolated worker. It also hosts the four
  MCP project-context tools as native Azure Functions MCP tool triggers, exposed over
  remote/SSE transport.
- `UsageTracker.Library` — all domain, infrastructure, and runtime behavior.
- `UsageTracker.Dashboard` — a Blazor WebAssembly read/query experience.
- Tool output compression — an optional `IToolOutputCompressor` extension point; no
  implementation ships by default.

## Dependency direction

```
UsageTracker.Functions   -> UsageTracker.Library
Dashboard                -> Function endpoints over HTTP
MCP client (SSE/remote)  -> UsageTracker.Functions MCP tool triggers
UsageTracker.Tests       -> UsageTracker.Library
UsageTracker.AppHost     -> UsageTracker.Functions (AddAzureFunctionsProject)
                            -> UsageTracker.Dashboard
                            -> Azure Cosmos DB (emulator in dev)
```

`UsageTracker.Library` must **not** reference `UsageTracker.Functions` or
`UsageTracker.Dashboard`.

## Topics

- [Solution structure](solution-structure.md) — project tree, file movement, dependency rules.
- [Function App](functions.md) — HTTP boundary, endpoints, isolated worker, examples.
- [Library layering](library.md) — Domain, Infrastructure, Runtime, and DI wiring.
- [MCP project context](mcp-project-context.md) — MCP tools plus the attribution model.
- [Blazor dashboard](dashboard.md) — pages and read endpoints.
- [Tool output compression](tool-output-compression.md) — compression extension point, scope, and default no-op behavior.
- [Hosting](hosting.md) — Azure Container Apps + Functions hosting.
- [Roadmap](roadmap.md) — implementation phases and acceptance criteria.

## Final design statement

> Restructure UsageTracker around a Function App rather than a traditional API. The
> Function App becomes the thin HTTP boundary for agent hook ingestion, MCP
> project-context updates, and dashboard query endpoints. All reusable behavior moves
> into `UsageTracker.Library`, organized into `Domain`, `Infrastructure`, and
> `Runtime`. The Blazor dashboard becomes a separate experience that reads from
> Function endpoints. Tool-output compression is an optional extension point
> (`IToolOutputCompressor`); no implementation ships by default, so hooks ingest and log
> with no compression unless a host registers one for model-bound post-tool-output
> compression. Raw hook events are always stored first and never overwritten. GitHub Copilot is the first in-path compression
> target because its post-tool-use flow supports modified tool output (`modifiedResult`).
> Claude Code and Cursor are observed immediately, with output replacement enabled only
> after validation.

## References

- [Azure Functions HTTP webhook trigger](https://learn.microsoft.com/en-us/azure/azure-functions/functions-bindings-http-webhook-trigger)
- [Guide for running C# Azure Functions in the isolated worker model](https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide)
