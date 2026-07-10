# V2 Hosting

> **Status: implemented (local Aspire).** The AppHost models Functions, the Blazor
> dashboard, and an Azure Cosmos DB resource. Local orchestration works via
> `scripts/run-apphost.ps1`; a full run needs Docker (Cosmos emulator and Azurite
> storage for the Functions host). Azure Container Apps is the target deployment shape.

## Decision

Host `UsageTracker.Functions` as a **containerized Azure Functions app on Azure
Container Apps**.

```
UsageTracker.Functions
  hosted as Azure Functions on Azure Container Apps (kind=functionapp)
```

## Why this model is validated

1. **Azure Functions can run as containers.** Functions supports containerized function
   apps that run in an Azure Container Apps environment, making it straightforward to
   deploy and run function apps as Linux containers you create and maintain.
2. **Functions on Azure Container Apps includes sidecar support, if ever needed.** The
   integration gives Function apps access to Azure Container Apps features — including
   sidecars — when deployed through the `Microsoft.App` resource provider with
   `kind=functionapp`. Tool output compression does not require this today: it is an
   in-process extension point (`IToolOutputCompressor`), not a sidecar. A host that
   later wants to run its own compressor as a sidecar rather than in-process still has
   that option available.
3. **Functions can run beside other containers.** The model is intended for running
   Functions alongside other containerized apps such as microservices, APIs, or
   websites.

## Runtime architecture

```
Claude Code / Copilot / Cursor hooks
        |
        v
UsageTracker.Functions container
        |
        |-- store raw event
        |-- normalize event
        |-- resolve project context
        |
        |-- if PostToolUse + large model-bound output
        |   and a host-registered IToolOutputCompressor:
                |
                v
        compressor invoked (none registered by default -> no-op, fail open)
                |
                v
        compressed output returned by Function
```

## Local development vs deployment

For local Aspire, Cosmos DB runs as an emulator (in `Development`), and the Functions
app is added with `AddAzureFunctionsProject` (not `AddProject`). For Azure, target
Azure Container Apps with Functions support. The implemented `AppHost.cs` looks like
this:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var cosmos = builder.AddAzureCosmosDB("cosmos");

if (builder.Environment.IsDevelopment())
{
    cosmos.RunAsEmulator();
}

var usageDatabase = cosmos.AddCosmosDatabase("usage-tracker");

var functions = builder.AddAzureFunctionsProject<Projects.UsageTracker_Functions>("usage-tracker-functions")
    .WithHttpEndpoint(port: 7071, targetPort: 7071, name: "http")
    .WithReference(usageDatabase);

builder.AddProject<Projects.UsageTracker_Dashboard>("usage-tracker-dashboard")
    .WithReference(functions);

builder.Build().Run();
```

The AppHost uses stable Aspire packages: `Aspire.Hosting.Azure.CosmosDB` and
`Aspire.Hosting.Azure.Functions` (both net8). `Aspire.Hosting.Azure.Functions` is
stable, so `AddAzureFunctionsProject` is used for the Functions project rather than the
generic `AddProject`. There is no compression sidecar in the app model; a host that
wants compression registers its own `IToolOutputCompressor` in the Functions
composition root (see [tool-output-compression.md](../design/tool-output-compression.md)).

## Dashboard deploys separately

This doc covers `UsageTracker.Functions` only. `UsageTracker.Dashboard` (the Blazor
WebAssembly app) does not deploy onto Azure Container Apps alongside Functions — it
targets Azure Static Web Apps with Functions as its API backend instead. See
[dashboard.md](../design/dashboard.md#hosting-note) for that deployment shape.

## References

- [Azure Functions on Azure Container Apps overview](https://learn.microsoft.com/en-us/azure/container-apps/functions-overview)
- [Create a function app in a Linux container](https://learn.microsoft.com/en-us/azure/azure-functions/functions-how-to-custom-container)
- [Azure App Service sidecar overview](https://learn.microsoft.com/en-us/azure/app-service/overview-sidecar)
- [Aspire — add a Dockerfile to the app model](https://aspire.dev/app-host/withdockerfile/)
- [Aspire — Docker Compose integration](https://aspire.dev/integrations/compute/docker/)
