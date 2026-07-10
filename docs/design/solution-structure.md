# V2 Solution Structure

> **Status: implemented.** The layout below matches `src/` today. The file-movement
> plan is complete and `UsageTracker.Api` has been deleted. Type names differ from the
> original V2 sketch where noted; `src/` is authoritative.

## Actual project layout

```
TOKEN-OPTIMIZATION
│
├── src
│   ├── UsageTracker.Functions              # Azure Functions, isolated worker (net8)
│   │   ├── Functions
│   │   │   ├── HookIngestionFunctions.cs
│   │   │   ├── ProjectContextFunctions.cs
│   │   │   ├── ProjectContextMcpTools.cs    # MCP tool triggers (usage_*_project_context, usage_list_recent_projects)
│   │   │   ├── DashboardQueryFunctions.cs
│   │   │   └── HealthFunctions.cs
│   │   ├── FunctionsUserContext.cs          # host IUserContext implementation
│   │   ├── HttpContextHolder.cs             # captures HttpContext in the worker
│   │   ├── Program.cs
│   │   ├── host.json
│   │   └── local.settings.json
│   │
│   ├── UsageTracker.Library                 # flat `UsageTracker` namespace (net8)
│   │   ├── Domain
│   │   │   ├── Hooks
│   │   │   │   └── HookEvent.cs
│   │   │   ├── Context
│   │   │   │   ├── ProjectContext.cs
│   │   │   │   └── UserContext.cs           # IUserContext + CurrentUser
│   │   │   └── Usage
│   │   │       ├── UsageDocuments.cs         # NormalizedUsageEvent, UsageSummaryRow
│   │   │       └── CompressionResult.cs      # type ToolOutputCompression
│   │   │
│   │   ├── Infrastructure
│   │   │   ├── Persistence
│   │   │   │   ├── UsageRepository.cs        # IUsageRepository, InMemory..., Cosmos...
│   │   │   │   └── UsageStore.cs             # UsageStore, SessionRecord
│   │   │   ├── Compression
│   │   │   │   ├── IToolOutputCompressor.cs      # extension point interface only
│   │   │   │   └── ToolOutputCompressionOptions.cs
│   │   │   └── Tokens
│   │   │       └── TranscriptTokenReader.cs  # TranscriptTokenReader, TokenUsage
│   │   │
│   │   ├── Runtime
│   │   │   ├── Hooks
│   │   │   │   ├── HookIngestionService.cs   # IHookIngestionService, HookIngestionResult
│   │   │   │   └── ToolOutputCompressionService.cs
│   │   │   ├── Attribution
│   │   │   │   └── ProjectAttributionService.cs
│   │   │   ├── Context
│   │   │   │   └── ProjectContextService.cs  # IProjectContextService, ProjectContextResult
│   │   │   └── Dashboard
│   │   │       └── DashboardQueryService.cs  # IDashboardQueryService, SessionView, ProjectUsageRow
│   │   │
│   │   └── DependencyInjection
│   │       └── UsageTrackerServiceCollectionExtensions.cs  # AddUsageTrackerLibrary
│   │
│   ├── UsageTracker.Dashboard               # Blazor WebAssembly (net8)
│   │   ├── Pages
│   │   │   ├── Home.razor
│   │   │   ├── Sessions.razor
│   │   │   ├── Projects.razor
│   │   │   └── AgentUsage.razor
│   │   ├── Layout
│   │   ├── Models/DashboardModels.cs
│   │   ├── Services/DashboardClient.cs
│   │   └── Program.cs
│   │
│   ├── UsageTracker.AppHost                 # Aspire orchestration (AppHost.cs)
│   ├── UsageTracker.ServiceDefaults
│   └── UsageTracker.Tests
│
├── .github
├── docs
├── scripts
├── claude-code.settings.json
├── copilot.hooks.json
└── UsageTracker.sln
```

The original V2 sketch guessed at some file and type names. The implementation
diverged: the Domain layer is flatter (no separate `HookEventType`/`HookPlatform`/
`ToolCallResult`/`ProjectAttribution` files), and the runtime does not use separate
per-platform `Adapters`/`Responses` folders or a standalone `HookNormalizationService`.
See [library.md](library.md) for the type-name reconciliation.

## File movement (done)

Source files previously lived in `src/UsageTracker.Api`, which has been **deleted**.
The table records where each moved. Controllers were removed; HTTP triggers are the
boundary.

| Former API file | Now at | Notes |
| --- | --- | --- |
| `HookEvent.cs` | `UsageTracker.Library/Domain/Hooks/HookEvent.cs` | Core domain data. |
| `ProjectContext.cs` | `UsageTracker.Library/Domain/Context/ProjectContext.cs` | Used by hooks, MCP, dashboard. |
| `UserContext.cs` | `UsageTracker.Library/Domain/Context/UserContext.cs` | Now `IUserContext` + `CurrentUser` only; the ASP.NET `HttpUserContext` was dropped. |
| `UsageDocuments.cs` | `UsageTracker.Library/Domain/Usage/UsageDocuments.cs` | Kept as one file: `NormalizedUsageEvent`, `UsageSummaryRow`. Not split into `UsageRecord`/`TokenUsageEstimate`. |
| `UsageStore.cs` | `UsageTracker.Library/Infrastructure/Persistence/UsageStore.cs` | `UsageStore`, `SessionRecord`. No `IUsageStore` interface. |
| `UsageRepository.cs` | `UsageTracker.Library/Infrastructure/Persistence/UsageRepository.cs` | `IUsageRepository`, `InMemoryUsageRepository`, `CosmosUsageRepository`. |
| `TranscriptTokenReader.cs` | `UsageTracker.Library/Infrastructure/Tokens/TranscriptTokenReader.cs` | `TranscriptTokenReader`, `TokenUsage`. No `ITranscriptTokenReader` interface. |
| `ProjectAttributionService.cs` | `UsageTracker.Library/Runtime/Attribution/ProjectAttributionService.cs` | Runtime orchestration. |
| `HooksController.cs` | `UsageTracker.Functions/Functions/HookIngestionFunctions.cs` | HTTP trigger is the boundary. |
| `ProjectContextController.cs` | `UsageTracker.Functions/Functions/ProjectContextFunctions.cs` | Exposed for MCP/dashboard use. |
| _(new)_ | `UsageTracker.Functions/Functions/ProjectContextMcpTools.cs` | MCP tool triggers, hosted directly in the Function App (no former API equivalent). |
| `Program.cs` (API) | `UsageTracker.Functions/Program.cs` | The Function App is the composition root. |
| `Dockerfile` (API) | `UsageTracker.Functions/Dockerfile` | Built by `scripts/build-container.ps1`. |

## Dependency rules

- `UsageTracker.Functions` references `UsageTracker.Library`.
- `UsageTracker.Dashboard` calls Function endpoints over HTTP.
- MCP clients connect to `UsageTracker.Functions`' MCP tool triggers over remote/SSE
  transport (no separate MCP project).
- `UsageTracker.Tests` references `UsageTracker.Library`.
- `UsageTracker.AppHost` references `UsageTracker.Functions` (via
  `AddAzureFunctionsProject`) and `UsageTracker.Dashboard`, and adds the Azure Cosmos DB
  resource.
- `UsageTracker.Library` must **not** reference `UsageTracker.Functions` or
  `UsageTracker.Dashboard`.
