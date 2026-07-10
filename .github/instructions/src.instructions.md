---
applyTo: "src/**"
description: "Use when editing source code for the UsageTracker hook observability solution."
---

# Source Code Instructions

This repository tracks coding-agent hook usage. Keep source changes focused on ingestion, normalization, token accounting, hosting, and deployment support.

## Solution Structure

```
src/
├── UsageTracker.Library/          # Domain/Infrastructure/Runtime + DI (all reusable behavior)
├── UsageTracker.Functions/        # Azure Functions isolated worker: HTTP boundary (/api/*) + MCP tool triggers
├── UsageTracker.Dashboard/        # Blazor WebAssembly read-only dashboard
├── UsageTracker.AppHost/          # Aspire orchestration host (Functions + Cosmos + Dashboard)
├── UsageTracker.ServiceDefaults/  # Aspire service defaults and health/telemetry wiring
└── UsageTracker.Tests/            # Focused unit tests for normalization and usage accounting
```

`UsageTracker.Api` has been removed. `UsageTracker.Library` must not reference
`UsageTracker.Functions` or `UsageTracker.Dashboard`.

## Build Commands

- Build: `dotnet build src/UsageTracker.sln`
- Test: `dotnet test src/UsageTracker.sln`
- Run Functions (HTTP boundary): `scripts/run-functions.ps1` (wraps `func start`, port 7071)
- Run Aspire host: `scripts/run-apphost.ps1` (or `dotnet run --project src/UsageTracker.AppHost/UsageTracker.AppHost.csproj`; needs Docker)

## Conventions

- Preserve vendor raw payloads when adding new normalization logic.
- Treat hook payload fields as unstable. Prefer alias-based extraction and null-safe fallbacks.
- Do not make hook ingestion block agent turns unless the feature explicitly requires enforcement.
- Keep local defaults friendly to localhost hooks, but let Aspire and container hosts override ports and URLs.