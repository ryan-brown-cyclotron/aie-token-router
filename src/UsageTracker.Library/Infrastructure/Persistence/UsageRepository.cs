using System.Collections.Concurrent;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace UsageTracker;

public interface IUsageRepository
{
    Task UpsertProjectContextAsync(ProjectContextWindow context, CancellationToken cancellationToken = default);
    Task EndActiveProjectContextAsync(string user, string projectKey, DateTimeOffset endedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ProjectContextWindow>> ActiveProjectContextsAsync(string user, DateTimeOffset at, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ProjectContextWindow>> RecentProjectContextsAsync(string user, int limit, CancellationToken cancellationToken = default);
    Task RecordEventAsync(NormalizedUsageEvent usageEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<UsageSummaryRow>> SummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default);
    Task<NormalizedUsageEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retroactively rewrites already-recorded events for <paramref name="user"/> +
    /// <paramref name="sessionId"/> that currently have "unknown"/"ambiguous" attribution to
    /// <paramref name="attribution"/>. Confidently-attributed events ("session"/"workspace"/
    /// "user-window") are never touched. Returns the count of events changed.
    /// </summary>
    Task<int> BackfillSessionAttributionAsync(string user, string sessionId, ProjectAttribution attribution, CancellationToken cancellationToken = default);
}

public sealed class InMemoryUsageRepository : IUsageRepository
{
    private static readonly HashSet<string> BackfillEligibleConfidence = new(StringComparer.OrdinalIgnoreCase) { "unknown", "ambiguous" };

    private readonly ConcurrentDictionary<string, ProjectContextWindow> _contexts = new();
    private readonly ConcurrentDictionary<string, NormalizedUsageEvent> _events = new(StringComparer.OrdinalIgnoreCase);

    public Task UpsertProjectContextAsync(ProjectContextWindow context, CancellationToken cancellationToken = default)
    {
        _contexts[context.Id] = context;
        return Task.CompletedTask;
    }

    public Task EndActiveProjectContextAsync(string user, string projectKey, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        foreach (var context in _contexts.Values.Where(context =>
                     context.User.Equals(user, StringComparison.OrdinalIgnoreCase) &&
                     context.ProjectKey.Equals(projectKey, StringComparison.OrdinalIgnoreCase) &&
                     context.IsActiveAt(endedAt)))
        {
            _contexts[context.Id] = context with { EndedAt = endedAt };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<ProjectContextWindow>> ActiveProjectContextsAsync(string user, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<ProjectContextWindow> contexts = _contexts.Values
            .Where(context => context.User.Equals(user, StringComparison.OrdinalIgnoreCase) && context.IsActiveAt(at))
            .ToList();

        return Task.FromResult(contexts);
    }

    public Task<IReadOnlyCollection<ProjectContextWindow>> RecentProjectContextsAsync(string user, int limit, CancellationToken cancellationToken = default)
    {
        IReadOnlyCollection<ProjectContextWindow> contexts = _contexts.Values
            .Where(context => context.User.Equals(user, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(context => context.StartedAt)
            .Take(limit)
            .ToList();

        return Task.FromResult(contexts);
    }

    public Task RecordEventAsync(NormalizedUsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        _events[usageEvent.Id] = usageEvent;
        return Task.CompletedTask;
    }

    public Task<NormalizedUsageEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default)
    {
        _events.TryGetValue(id, out var match);
        return Task.FromResult(match);
    }

    public Task<IReadOnlyCollection<UsageSummaryRow>> SummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var events = _events.Values.Where(evt =>
            (from is null || evt.ReceivedAt >= from) &&
            (to is null || evt.ReceivedAt < to));

        IReadOnlyCollection<UsageSummaryRow> summary = events
            .GroupBy(evt => (evt.Platform, evt.Model, evt.User, evt.ProjectKey, evt.ProjectName, evt.AttributionConfidence))
            .Select(group => new UsageSummaryRow(
                group.Key.Platform,
                group.Key.Model,
                group.Key.User,
                group.Key.ProjectKey,
                group.Key.ProjectName,
                group.Key.AttributionConfidence,
                group.Select(evt => evt.SessionId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Count(),
                group.Count(evt => evt.EventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase)),
                group.Count(),
                group.Sum(evt => evt.Usage.InputTokens),
                group.Sum(evt => evt.Usage.OutputTokens),
                group.Sum(evt => evt.Usage.CacheReadTokens),
                group.Sum(evt => evt.Usage.CacheCreationTokens)))
            .OrderByDescending(row => row.TotalTokens)
            .ToList();

        return Task.FromResult(summary);
    }

    public Task<int> BackfillSessionAttributionAsync(string user, string sessionId, ProjectAttribution attribution, CancellationToken cancellationToken = default)
    {
        var changed = 0;

        foreach (var evt in _events.Values.Where(evt =>
                     evt.User.Equals(user, StringComparison.OrdinalIgnoreCase) &&
                     evt.SessionId is not null && evt.SessionId.Equals(sessionId, StringComparison.Ordinal) &&
                     BackfillEligibleConfidence.Contains(evt.AttributionConfidence)))
        {
            var updated = evt with
            {
                ProjectKey = attribution.ProjectKey,
                ProjectName = attribution.ProjectName,
                AttributionConfidence = attribution.Confidence,
                PartitionKey = NormalizedUsageEvent.ComputePartitionKey(evt.ReceivedAt, evt.User, attribution.ProjectKey)
            };

            if (_events.TryUpdate(evt.Id, updated, evt))
                changed++;
        }

        return Task.FromResult(changed);
    }
}

public sealed class CosmosUsageRepository : IUsageRepository
{
    private const string EventsContainerName = "events";
    private const string ContextsContainerName = "projectContexts";

    private readonly CosmosClient _client;
    private readonly string _databaseName;
    private readonly Lazy<Task> _initializer;

    public CosmosUsageRepository(CosmosClient client, IConfiguration configuration)
    {
        _client = client;
        _databaseName = configuration["UsageTracker:Cosmos:DatabaseName"] ?? "usage-tracker";
        _initializer = new Lazy<Task>(InitializeAsync);
    }

    public async Task UpsertProjectContextAsync(ProjectContextWindow context, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        await Contexts.UpsertItemAsync(context, new PartitionKey(context.User), cancellationToken: cancellationToken);
    }

    public async Task EndActiveProjectContextAsync(string user, string projectKey, DateTimeOffset endedAt, CancellationToken cancellationToken = default)
    {
        var contexts = await ActiveProjectContextsAsync(user, endedAt, cancellationToken);

        foreach (var context in contexts.Where(context => context.ProjectKey.Equals(projectKey, StringComparison.OrdinalIgnoreCase)))
        {
            await UpsertProjectContextAsync(context with { EndedAt = endedAt }, cancellationToken);
        }
    }

    public async Task<IReadOnlyCollection<ProjectContextWindow>> ActiveProjectContextsAsync(string user, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        var query = Contexts.GetItemLinqQueryable<ProjectContextWindow>(requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(user)
            })
            .Where(context => context.User == user && context.EndedAt == null && context.StartedAt <= at && (context.ExpiresAt == null || context.ExpiresAt > at))
            .ToFeedIterator();

        return await ReadAllAsync(query, cancellationToken);
    }

    public async Task RecordEventAsync(NormalizedUsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();
        await Events.CreateItemAsync(usageEvent, new PartitionKey(usageEvent.PartitionKey), cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyCollection<UsageSummaryRow>> SummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        var query = new QueryDefinition("SELECT * FROM c WHERE (@from = null OR c.receivedAt >= @from) AND (@to = null OR c.receivedAt < @to)")
            .WithParameter("@from", from)
            .WithParameter("@to", to);

        var iterator = Events.GetItemQueryIterator<NormalizedUsageEvent>(query);
        var events = await ReadAllAsync(iterator, cancellationToken);

        return events
            .GroupBy(evt => (evt.Platform, evt.Model, evt.User, evt.ProjectKey, evt.ProjectName, evt.AttributionConfidence))
            .Select(group => new UsageSummaryRow(
                group.Key.Platform,
                group.Key.Model,
                group.Key.User,
                group.Key.ProjectKey,
                group.Key.ProjectName,
                group.Key.AttributionConfidence,
                group.Select(evt => evt.SessionId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Count(),
                group.Count(evt => evt.EventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase)),
                group.Count(),
                group.Sum(evt => evt.Usage.InputTokens),
                group.Sum(evt => evt.Usage.OutputTokens),
                group.Sum(evt => evt.Usage.CacheReadTokens),
                group.Sum(evt => evt.Usage.CacheCreationTokens)))
            .OrderByDescending(row => row.TotalTokens)
            .ToList();
    }

    public async Task<IReadOnlyCollection<ProjectContextWindow>> RecentProjectContextsAsync(string user, int limit, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        var query = Contexts.GetItemLinqQueryable<ProjectContextWindow>(requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(user)
            })
            .Where(context => context.User == user)
            .OrderByDescending(context => context.StartedAt)
            .Take(limit)
            .ToFeedIterator();

        return await ReadAllAsync(query, cancellationToken);
    }

    public async Task<NormalizedUsageEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        // Cross-partition point-read by id: callers reach this only from the dashboard drill-in.
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", id);
        var iterator = Events.GetItemQueryIterator<NormalizedUsageEvent>(query);
        var events = await ReadAllAsync(iterator, cancellationToken);

        return events.FirstOrDefault();
    }

    public async Task<int> BackfillSessionAttributionAsync(string user, string sessionId, ProjectAttribution attribution, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        // Cross-partition, same unindexed-query pattern as GetEventAsync above. Naturally bounded:
        // sessionId is a fresh, never-reused opaque id, so this only ever matches one session's events.
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.user = @user AND c.sessionId = @sessionId AND c.attributionConfidence IN ('unknown','ambiguous')")
            .WithParameter("@user", user)
            .WithParameter("@sessionId", sessionId);
        var matches = await ReadAllAsync(Events.GetItemQueryIterator<NormalizedUsageEvent>(query), cancellationToken);

        var changed = 0;
        foreach (var evt in matches)
        {
            var newPartitionKey = NormalizedUsageEvent.ComputePartitionKey(evt.ReceivedAt, evt.User, attribution.ProjectKey);
            var updated = evt with
            {
                ProjectKey = attribution.ProjectKey,
                ProjectName = attribution.ProjectName,
                AttributionConfidence = attribution.Confidence,
                PartitionKey = newPartitionKey
            };

            if (newPartitionKey == evt.PartitionKey)
            {
                // Project key resolved to the same partition (e.g. re-applying the same project) -
                // a plain in-place replace, no partition move needed.
                await Events.ReplaceItemAsync(updated, updated.Id, new PartitionKey(evt.PartitionKey), cancellationToken: cancellationToken);
            }
            else
            {
                // Cosmos partition keys are immutable per item, so "moving" one means creating the
                // item under its new partition and deleting the old copy. Create-then-delete (not
                // delete-then-create) so a failure partway through never loses the event. A 409
                // Conflict on create means a prior retry already migrated this event - treat that as
                // success and still attempt the delete, making the whole operation retry-safe.
                try
                {
                    await Events.CreateItemAsync(updated, new PartitionKey(newPartitionKey), cancellationToken: cancellationToken);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    // Already migrated by a prior retry - fall through to delete the stale copy.
                }

                await Events.DeleteItemAsync<NormalizedUsageEvent>(evt.Id, new PartitionKey(evt.PartitionKey), cancellationToken: cancellationToken);
            }

            changed++;
        }

        return changed;
    }

    private Container Events => _client.GetContainer(_databaseName, EventsContainerName);
    private Container Contexts => _client.GetContainer(_databaseName, ContextsContainerName);

    private Task EnsureInitializedAsync() => _initializer.Value;

    private async Task InitializeAsync()
    {
        var database = await _client.CreateDatabaseIfNotExistsAsync(_databaseName);
        await database.Database.CreateContainerIfNotExistsAsync(EventsContainerName, "/partitionKey");
        await database.Database.CreateContainerIfNotExistsAsync(ContextsContainerName, "/user");
    }

    private static async Task<IReadOnlyCollection<T>> ReadAllAsync<T>(FeedIterator<T> iterator, CancellationToken cancellationToken)
    {
        var results = new List<T>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            results.AddRange(response);
        }

        return results;
    }
}
