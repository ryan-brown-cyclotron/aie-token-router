# Container Apps Deployment

`UsageTracker.Functions` is containerized as an Azure Functions app (isolated worker, .NET 8). Aspire is used for local orchestration; Azure Container Apps can run the published Functions container (see [hosting.md](hosting.md)).

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

- Cosmos DB connection configuration for durable storage via `CosmosUsageRepository`.
- `AzureWebJobsStorage`: storage connection for the Functions host (Azurite locally).

## Authentication (Easy Auth)

Caller identity is provided by **Azure Container Apps built-in authentication (Easy Auth)** with Microsoft
Entra as the identity provider. The local daemon (see [daemon-cli.md](../design/daemon-cli.md)) acquires an
Entra **user** token via the enrolled device and sends it as a Bearer token; Easy Auth validates it and
injects the verified principal, which `FunctionsUserContext` reads from the `X-MS-CLIENT-PRINCIPAL*`
headers. `X-User-Email` is now trusted in Development only.

Configure it in **API mode** (return 401 rather than redirect to a login page), scoped to the backend
API app registration's App ID URI:

```bash
az containerapp auth microsoft update \
  --name <container-app> --resource-group <rg> \
  --client-id <backend-app-guid> \
  --allowed-audiences api://<backend-app-guid> \
  --tenant-id <tenant-id>

az containerapp auth update \
  --name <container-app> --resource-group <rg> \
  --unauthenticated-client-action Return401 \
  --enabled true
```

Notes:
- Easy Auth gates **all** ingress, including the in-worker MCP/SSE endpoints. That is acceptable while the
  token-bearing daemon is the only caller; revisit if an IDE must reach MCP directly.
- Hook ingestion stays fail-open for **malformed bodies** only. Unauthenticated requests now get a 401
  from Easy Auth (before the worker runs) so misconfiguration is visible instead of silently succeeding.
- The two app registrations (backend API app + daemon public client) and admin consent are prerequisites;
  see [daemon-cli.md](../design/daemon-cli.md#app-registrations-one-time-in-scope).

## Production Gaps To Close

Before using this as a shared team service, add:

- Durable, scale-out-safe session storage. `UsageStore` is an in-memory per-process cache, so `/api/dashboard/sessions` and the in-memory metrics fallback reflect a single instance; durable metrics come from Cosmos via `SummaryAsync`.
- Retention policy for raw payloads and transcript-derived usage data.
- Monitoring and alerts for ingestion failures.