using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace UsageTracker;

/// <summary>
/// Registers the host-agnostic UsageTracker runtime, infrastructure, and domain services.
/// A host (the Function App today) calls this from its composition root and adds only the
/// host-specific pieces itself (e.g. its own <see cref="IUserContext"/> implementation).
/// </summary>
public static class UsageTrackerServiceCollectionExtensions
{
    public static IServiceCollection AddUsageTrackerLibrary(this IServiceCollection services, IConfiguration configuration)
    {
        // Stateful process-wide caches — must be singletons so they survive across requests/invocations.
        // (The docs sketch these as scoped; the in-memory session cache and the transcript byte-offset
        //  tracker both hold per-process state, so scoped would silently reset them.)
        services.AddSingleton<UsageStore>();
        services.AddSingleton<TranscriptTokenReader>();

        // OpenTelemetry instruments for hook ingestion. The Meter instance must be a singleton
        // (long-lived); the host opts the meter name into its OTel pipeline itself (see
        // UsageTracker.Functions/Program.cs) so this library stays agnostic of the host's telemetry setup.
        services.AddSingleton<UsageTrackerMetrics>();

        // Stateless orchestration over the repository.
        services.AddSingleton<IProjectAttributionService, ProjectAttributionService>();

        // Durable persistence: Cosmos when a connection string is configured, otherwise in-memory.
        services.AddSingleton<IUsageRepository>(_ => CreateUsageRepository(configuration));

        // DeterministicToolOutputCompressor is the default IToolOutputCompressor; hosts/tests can
        // override it by registering their own before/instead of this call. If none is registered,
        // ToolOutputCompressionService resolves null and hooks just ingest and log with no compression.
        services.Configure<ToolOutputCompressionOptions>(configuration.GetSection(ToolOutputCompressionOptions.SectionName));
        services.AddSingleton<IToolOutputCompressor, DeterministicToolOutputCompressor>();
        services.AddScoped<ToolOutputCompressionService>();

        // Runtime orchestration the Function App calls. Scoped because hook ingestion and context
        // writes depend on the per-request IUserContext supplied by the host.
        services.AddScoped<IHookIngestionService, HookIngestionService>();
        services.AddScoped<IProjectContextService, ProjectContextService>();
        services.AddScoped<IDashboardQueryService, DashboardQueryService>();

        return services;
    }

    /// <summary>
    /// Connection-string precedence mirrors the original API composition root:
    /// Aspire's <c>usage-tracker</c> reference, then <c>cosmos</c>, then an explicit config value.
    /// Blank => in-memory repository (friendly local/dev default).
    /// </summary>
    public static IUsageRepository CreateUsageRepository(IConfiguration configuration)
    {
        var connectionString =
            configuration.GetConnectionString("usage-tracker") ??
            configuration.GetConnectionString("cosmos") ??
            configuration["UsageTracker:Cosmos:ConnectionString"];

        return string.IsNullOrWhiteSpace(connectionString)
            ? new InMemoryUsageRepository()
            : new CosmosUsageRepository(new CosmosClient(connectionString), configuration);
    }
}
