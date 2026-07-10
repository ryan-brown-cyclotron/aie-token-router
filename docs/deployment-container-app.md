# Container Apps Deployment

`UsageTracker.Functions` is containerized as an Azure Functions app (isolated worker, .NET 8). Aspire is used for local orchestration; Azure Container Apps can run the published Functions container (see [v2/hosting.md](v2/hosting.md)).

## Build A Local Image

```powershell
./scripts/build-container.ps1
```

This builds `src/UsageTracker.Functions/Dockerfile` from the repo root (the build context is the repo root so the referenced `UsageTracker.Library` and `UsageTracker.ServiceDefaults` projects are available). The default image tag is:

```text
usage-tracker:local
```

## Runtime Ports

The Dockerfile is based on `mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0` and `EXPOSE`s port `80`. Container Apps should provide ingress to that HTTP port. Locally the Functions host listens on `http://localhost:7071` (via `func start`).

## Hook URL Shape

Production hook URLs should be HTTPS and use the `api` route prefix:

```text
https://<container-app-hostname>/api/hooks/claude-code
https://<container-app-hostname>/api/hooks/copilot
```

Some cloud-agent hook surfaces may require outbound firewall allow-listing before they can call an external endpoint.

## Configuration

Useful environment variables:

- `ToolOutputCompression__MinimumCharacters`: minimum tool-output size (in characters) eligible for compression. Compression only runs if the host registers an `IToolOutputCompressor` implementation; none ships by default, so hooks ingest and log with no compression out of the box (fail-open).
- Cosmos DB connection configuration for durable storage via `CosmosUsageRepository`.
- `AzureWebJobsStorage`: storage connection for the Functions host (Azurite locally).

## Production Gaps To Close

Before using this as a shared team service, add:

- Durable, scale-out-safe session storage. `UsageStore` is an in-memory per-process cache, so `/api/dashboard/sessions` and the in-memory metrics fallback reflect a single instance; durable metrics come from Cosmos via `SummaryAsync`.
- Authentication or network restrictions for hook ingestion and read endpoints (routes are currently `AuthorizationLevel.Anonymous`).
- Retention policy for raw payloads and transcript-derived usage data.
- Monitoring and alerts for ingestion failures.