# V2 Roadmap

> **Status: phases 1-5 done.** The restructure is implemented in `src/`. Remaining work
> is tracked under [Remaining / follow-ups](#remaining--follow-ups).

## Implementation backlog (done)

### Phase 1 — Replace API with Function App — DONE

1. Added `UsageTracker.Functions`.
2. Configured the .NET 8 isolated worker (ASP.NET Core integration).
3. Moved API controller endpoints into HTTP-triggered functions.
4. `UsageTracker.Api` deleted.

### Phase 2 — Add `UsageTracker.Library` — DONE

1. Added `Domain`, `Infrastructure`, `Runtime` (plus `DependencyInjection`).
2. Moved models and services out of the former API project.
3. Added the `AddUsageTrackerLibrary` DI extension method.
4. The library does not reference Functions.

### Phase 3 — Add MCP project context support — DONE

1. `UsageTracker.Functions/Functions/ProjectContextMcpTools.cs` defines four MCP tool
   triggers for setting, reading, clearing, and listing recent project context, hosted
   directly inside the Function App (`Microsoft.Azure.Functions.Worker.Extensions.Mcp`,
   remote/SSE transport) rather than as a separate stdio server.
2. MCP tools call `IProjectContextService` directly — no HTTP self-proxy.
3. Context is stored as a session/platform/project mapping.
4. Hook ingestion uses project context when present.

### Phase 4 — Add Blazor dashboard — DONE

1. Added `UsageTracker.Dashboard` (Blazor WebAssembly).
2. Pages: `Home`, `Sessions`, `Projects`, `AgentUsage`.
3. Dashboard reads from Function endpoints via `DashboardClient`.
4. No runtime logic duplicated in Blazor.

### Phase 5 — Add tool output compression extension point — DONE

1. `IToolOutputCompressor` added as a pure extension point interface; no
   implementation ships in the repo.
2. `ToolOutputCompressionService` takes an optional `IToolOutputCompressor`; nothing is
   registered by default.
3. Compresses all PostToolUse tool outputs when a host registers a compressor.
4. Raw event stored before compression is attempted.
5. Fails open to the original output — including when no compressor is registered at
   all, which is the default.

## Remaining / follow-ups

- **Claude Code / Cursor output replacement.** Only Copilot returns `modifiedResult`
  today. Claude Code and Cursor are observe-only until their local hooks are validated
  for returning modified tool output.
- **Durable session store.** `UsageStore` is an in-memory per-process cache;
  `/api/dashboard/sessions` and the in-memory metrics fallback reflect a single instance
  and do not survive scale-out. Durable metrics come from Cosmos via `SummaryAsync`.
- **Transcript reading in the cloud.** `TranscriptTokenReader` reads local disk and
  tracks byte offsets in-process; the Functions path relies on inline `usage` in the
  hook payload rather than `transcript_path`.
- **Authentication.** Identity resolves from token claims with an `X-Dev-User-*` header
  fallback in Development; Entra ID / multi-tenant authorization is not yet wired.

## Acceptance criteria

### Restructure

- `UsageTracker.Api` is removed from the primary design.
- `UsageTracker.Functions` owns HTTP-triggered hook, context, and dashboard endpoints.
- `UsageTracker.Library` owns all domain, infrastructure, and runtime behavior.
- `UsageTracker.Dashboard` is display/query only.
- The library does not reference the Function App or Dashboard.

### Function App

- Claude, Copilot, and Cursor hooks can post to function endpoints.
- MCP project context can be set through function endpoints.
- The dashboard can query sessions, projects, usage, and raw event summaries.
- The Function App uses DI from `UsageTracker.Library`.

### Tool output compression

- No `IToolOutputCompressor` implementation is registered by default; a host opts in
  by registering its own.
- Eligibility is gated through `ToolOutputCompressionOptions` configuration.
- Raw hook events are stored before compression is attempted.
- Large post-tool outputs are compressed only when a compressor is registered.
- Compression failure, or no compressor registered, returns the original output.
- Claude/Copilot response shapes are platform-specific.
- Cursor remains observe-only until output replacement is validated.
