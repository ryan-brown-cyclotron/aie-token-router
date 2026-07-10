# UsageTracker Documentation

UsageTracker is a local-first hook receiver for observing coding-agent activity from tools such as Claude Code and GitHub Copilot. It receives hook payloads, normalizes key fields, reads token usage from transcript files when available, and exposes summary endpoints for inspection.

## Contents

- [Architecture](architecture.md)
- [Hook configuration](hooks.md)
- [V2 architecture restructure](v2/README.md)
- [Local setup](setup-local.md)
- [Container Apps deployment](deployment-container-app.md)

## Current Scope

Implemented now (the V2 structure — see [V2 architecture restructure](v2/README.md)):

- `UsageTracker.Functions` (Azure Functions isolated worker) hosts hook ingestion,
  project context, dashboard read, and health endpoints under `/api`, plus the four MCP
  project-context tools as native Azure Functions MCP tool triggers (remote/SSE
  transport).
- `UsageTracker.Library` owns all domain, infrastructure, and runtime behavior.
- `UsageTracker.Dashboard` (Blazor WebAssembly) reads the dashboard endpoints.
- Hook ingestion for Claude Code, GitHub Copilot, and Cursor routes.
- Project context attribution and MCP context tools.
- In-memory session tracking plus Cosmos-backed persistence via `IUsageRepository`.
- Transcript JSONL token extraction.
- Optional tool-output compression extension point (`IToolOutputCompressor`) for
  model-bound post-tool output; no implementation ships by default, so hooks ingest
  and log with no compression unless a host registers one (Copilot in-path when
  registered; Claude Code and Cursor observe-only).
- Aspire AppHost (Functions + Cosmos + Dashboard) and ServiceDefaults.
- Focused unit tests for normalization, token reading, and summary grouping.

Follow-ups (see [roadmap.md](v2/roadmap.md) → Remaining / follow-ups):

- Claude Code and Cursor in-path output replacement, pending validation.
- Durable, scale-out-safe session store.
- Authentication and multi-tenant authorization.

The former `UsageTracker.Api` ASP.NET Core project has been deleted; the notes in
[architecture.md](architecture.md) describe that retired V1 path.