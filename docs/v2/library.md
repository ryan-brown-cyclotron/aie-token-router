# V2 Library Layering

> **Status: implemented.** `UsageTracker.Library` is a net8 class library using a flat
> `UsageTracker` namespace, organized into three layers plus a `DependencyInjection`
> folder. The type names below match `src/`. The original V2 sketch invented some
> idealized names (`IUsageStore`, `ITranscriptTokenReader`, `UsageRecord`,
> `TokenUsageEstimate`, `ProjectAttribution`); those are **not** the implemented names —
> the reconciliation is called out below.

`UsageTracker.Library` owns all reusable behavior, organized into three layers.

## Domain

Pure data and value types with no infrastructure dependencies.

- `Hooks` — `HookEvent` (`Domain/Hooks/HookEvent.cs`).
- `Context` — `ProjectContext`; `IUserContext` + `CurrentUser` (the ASP.NET
  `HttpUserContext` was dropped — the host now supplies `IUserContext`).
- `Usage` — `NormalizedUsageEvent` and `UsageSummaryRow` (`UsageDocuments.cs`);
  `ToolOutputCompression` (declared in `CompressionResult.cs` — the file name kept the
  old label, but the type is `ToolOutputCompression`).

There is no separate `HookEventType`, `HookPlatform`, `ToolCallResult`, `UsageRecord`,
`TokenUsageEstimate`, or `ProjectAttribution` type — those were sketch names.

## Infrastructure

External-facing implementations.

- `Persistence` — `UsageStore` + `SessionRecord` (`UsageStore.cs`; a concrete class,
  there is no `IUsageStore`); `IUsageRepository` with `InMemoryUsageRepository` and
  `CosmosUsageRepository` (`UsageRepository.cs`).
- `Compression` — `IToolOutputCompressor` (a pure extension-point interface; no
  implementation ships in the repo), `ToolOutputCompressionOptions`.
- `Tokens` — `TranscriptTokenReader` + `TokenUsage` (`TranscriptTokenReader.cs`; a
  concrete class, there is no `ITranscriptTokenReader`).

## Runtime

Orchestration services the Function App calls.

- `Hooks` — `IHookIngestionService` / `HookIngestionService` (with `HookIngestionResult`)
  and `ToolOutputCompressionService`. Normalization lives inside these services; there
  is no separate `HookNormalizationService`, and no per-platform `Adapters`/`Responses`
  folders.
- `Attribution` — `ProjectAttributionService`.
- `Context` — `IProjectContextService` / `ProjectContextService` (with
  `ProjectContextResult`).
- `Dashboard` — `IDashboardQueryService` / `DashboardQueryService` (with `SessionView`
  and `ProjectUsageRow`).

## Hook ingestion runtime flow

```
HookIngestionService
  -> persist raw event
  -> normalize event
  -> evaluate project context
  -> decide if tool output is compressible
  -> if compressible, call ToolOutputCompressionService
  -> build platform-specific response
```

Raw storage always happens before compression, and raw events are never overwritten.

## Compression scope

Compress only:

- `PostToolUse` / `postToolUse`
- Large shell output
- Test output
- Build logs
- File reads
- Search results
- Transcript slices before model summarization

Do not compress:

- Raw hook payloads before storage
- Session metadata
- Project/user context records
- Normalized usage events
- Small payloads below the configured threshold

## DI wiring

The host composes the library through a single extension method,
`AddUsageTrackerLibrary(IServiceCollection, IConfiguration)`. The library never
references the Function App.

Lifetimes are mixed, which is a deliberate change from the original all-scoped sketch:

- **Singletons** — `UsageStore`, `TranscriptTokenReader`, `IProjectAttributionService`,
  and `IUsageRepository`. These hold stateful process-level caches: `UsageStore` is an
  in-memory session cache and `TranscriptTokenReader` tracks per-file byte offsets, both
  of which must survive across invocations; the Cosmos client is expensive to recreate.
- **Scoped** — `ToolOutputCompressionService`, `IHookIngestionService`,
  `IProjectContextService`, `IDashboardQueryService`.
- `Configure<ToolOutputCompressionOptions>` binds the `ToolOutputCompression` config
  section. No `IToolOutputCompressor` implementation is registered by default —
  `ToolOutputCompressionService`'s constructor takes `IToolOutputCompressor? compressor
  = null`, so it resolves to `null` and hooks just ingest and log normally with no
  compression attempted. A host that wants compression registers its own
  `IToolOutputCompressor` implementation in its own composition root.

`IUserContext` is **not** registered by the library — the host registers it
(`UsageTracker.Functions` registers `FunctionsUserContext`).

```csharp
public static IServiceCollection AddUsageTrackerLibrary(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // Stateful, process-level caches -> singletons.
    services.AddSingleton<UsageStore>();
    services.AddSingleton<TranscriptTokenReader>();
    services.AddSingleton<IProjectAttributionService, ProjectAttributionService>();
    services.AddSingleton<IUsageRepository, /* InMemory or Cosmos per config */>();

    // Compression is optional: no IToolOutputCompressor is registered here, so
    // ToolOutputCompressionService resolves it as null and hooks just ingest and log
    // normally with no compression attempted. Register an IToolOutputCompressor
    // implementation to opt in.
    services.Configure<ToolOutputCompressionOptions>(
        configuration.GetSection(ToolOutputCompressionOptions.SectionName));
    services.AddScoped<ToolOutputCompressionService>();

    // Request/invocation-scoped orchestration.
    services.AddScoped<IHookIngestionService, HookIngestionService>();
    services.AddScoped<IProjectContextService, ProjectContextService>();
    services.AddScoped<IDashboardQueryService, DashboardQueryService>();

    return services;
}
```
