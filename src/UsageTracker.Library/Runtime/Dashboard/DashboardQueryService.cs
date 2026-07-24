namespace UsageTracker;

/// <summary>
/// Read-only query surface for the dashboard. Wraps the in-memory <see cref="UsageStore"/>
/// (live sessions) and the durable <see cref="IUsageRepository"/> (aggregated usage), and
/// never mutates state. The Blazor dashboard calls these via the Function read endpoints.
/// </summary>
public interface IDashboardQueryService
{
    IReadOnlyCollection<SessionView> Sessions();
    Task<IReadOnlyCollection<UsageSummaryRow>> UsageAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ProjectUsageRow>> ProjectsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default);
    Task<NormalizedUsageEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Serialization-stable view of a live session (fields of <see cref="SessionRecord"/> exposed as properties).</summary>
public sealed record SessionView(
    string SessionId,
    string Platform,
    string User,
    string ProjectKey,
    string ProjectName,
    string AttributionConfidence,
    string Model,
    DateTimeOffset StartedAt,
    DateTimeOffset LastEventAt,
    int ToolCalls,
    TokenUsage Usage,
    IReadOnlyDictionary<string, int> Events,
    IReadOnlyList<SessionModelUsage> Models);

/// <summary>Per-model token split within a single session (a session may span multiple models).</summary>
public sealed record SessionModelUsage(string Model, TokenUsage Usage)
{
    public long TotalTokens => Usage.InputTokens + Usage.OutputTokens + Usage.CacheReadTokens + Usage.CacheCreationTokens;
}

/// <summary>Per-project usage rollup derived from the durable usage summary.</summary>
public sealed record ProjectUsageRow(
    string ProjectKey,
    string ProjectName,
    int Sessions,
    int Events,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

public sealed class DashboardQueryService : IDashboardQueryService
{
    private readonly UsageStore _store;
    private readonly IUsageRepository _repository;
    private readonly ILogger<DashboardQueryService> _logger;

    public DashboardQueryService(UsageStore store, IUsageRepository repository, ILogger<DashboardQueryService> logger)
    {
        _store = store;
        _repository = repository;
        _logger = logger;
    }

    public IReadOnlyCollection<SessionView> Sessions() =>
        _store.AllSessions()
            .Select(s => new SessionView(
                s.SessionId, s.Platform, s.User, s.ProjectKey, s.ProjectName, s.AttributionConfidence,
                s.Model, s.StartedAt, s.LastEventAt, s.ToolCalls, s.Usage, s.EventCounts,
                s.ModelUsage
                    .Select(kv => new SessionModelUsage(kv.Key, kv.Value))
                    .OrderByDescending(m => m.TotalTokens)
                    .ToList()))
            .ToList();

    public async Task<IReadOnlyCollection<UsageSummaryRow>> UsageAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.SummaryAsync(from, to, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Durable usage store unavailable; returning in-memory metrics");
            return _store.GetMetricsSummary(from, to);
        }
    }

    public async Task<IReadOnlyCollection<ProjectUsageRow>> ProjectsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var rows = await UsageAsync(from, to, cancellationToken);

        return rows
            .GroupBy(row => (row.ProjectKey, row.ProjectName))
            .Select(group => new ProjectUsageRow(
                group.Key.ProjectKey,
                group.Key.ProjectName,
                group.Sum(row => row.Sessions),
                group.Sum(row => row.Events),
                group.Sum(row => row.InputTokens),
                group.Sum(row => row.OutputTokens),
                group.Sum(row => row.CacheReadTokens),
                group.Sum(row => row.CacheCreationTokens)))
            .OrderByDescending(row => row.TotalTokens)
            .ToList();
    }

    public Task<NormalizedUsageEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default) =>
        _repository.GetEventAsync(id, cancellationToken);
}
