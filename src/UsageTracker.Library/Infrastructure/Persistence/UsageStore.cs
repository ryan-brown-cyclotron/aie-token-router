using System.Collections.Concurrent;

namespace UsageTracker;

public sealed class SessionRecord
{
    public required string SessionId { get; init; }
    public required string Platform { get; init; }
    public string User { get; set; } = "unknown";
    public string Model { get; set; } = "unknown";
    public string ProjectKey { get; set; } = "unknown";
    public string ProjectName { get; set; } = "Unknown";
    public string AttributionConfidence { get; set; } = "unknown";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastEventAt { get; set; } = DateTimeOffset.UtcNow;
    public TokenUsage Usage { get; set; } = TokenUsage.Empty;
    public int ToolCalls { get; set; }
    public readonly ConcurrentDictionary<string, int> EventCounts = new();

    /// <summary>
    /// Per-model token split within this session. A session may span multiple models
    /// (e.g. a model switch mid-session), so token usage is accumulated per resolved
    /// model here in addition to the session-wide <see cref="Usage"/> total. The single
    /// <see cref="Model"/> above remains the most-recently-seen model.
    /// </summary>
    public readonly ConcurrentDictionary<string, TokenUsage> ModelUsage = new();
}

/// <summary>
/// Everything lives in memory, keyed by session_id. This is intentionally not
/// a database - it's enough to answer "what am I actually spending, by tool
/// and by model, right now" locally. Swap in SQLite later if you want history
/// across server restarts.
/// </summary>
public sealed class UsageStore
{
    private readonly ConcurrentDictionary<string, SessionRecord> _sessions = new();

    public SessionRecord GetOrCreateSession(string sessionId, string platform) =>
        _sessions.GetOrAdd(sessionId, id => new SessionRecord { SessionId = id, Platform = platform });

    public void RecordEvent(HookEvent evt, TokenUsage delta, string? modelFromTranscript, ProjectAttribution attribution)
    {
        if (string.IsNullOrEmpty(evt.SessionId)) return;

        var session = GetOrCreateSession(evt.SessionId, evt.Platform);
        session.LastEventAt = DateTimeOffset.UtcNow;
        session.EventCounts.AddOrUpdate(evt.EventName, 1, (_, count) => count + 1);

        if (evt.UserKey != "unknown") session.User = evt.UserKey;
        session.ProjectKey = attribution.ProjectKey;
        session.ProjectName = attribution.ProjectName;
        session.AttributionConfidence = attribution.Confidence;

        // Resolve the model for this event with the same precedence as the durable
        // NormalizedUsageEvent (payload model -> transcript model -> "unknown") so the
        // per-model split keys match the model labels shown elsewhere on the dashboard.
        var model = !string.IsNullOrEmpty(evt.Model) ? evt.Model
            : !string.IsNullOrEmpty(modelFromTranscript) ? modelFromTranscript
            : "unknown";
        if (model != "unknown") session.Model = model;

        if (evt.EventName.Equals("PreToolUse", StringComparison.OrdinalIgnoreCase))
            session.ToolCalls++;

        session.Usage = session.Usage.Add(delta);
        session.ModelUsage.AddOrUpdate(model, delta, (_, existing) => existing.Add(delta));
    }

    public IReadOnlyCollection<SessionRecord> AllSessions() => _sessions.Values.ToList();

    /// <summary>
    /// Immediately overwrites a live session's project attribution, rather than waiting for the
    /// next hook event to naturally overwrite it. Called after a durable backfill
    /// (<see cref="IUsageRepository.BackfillSessionAttributionAsync"/>) so the dashboard's
    /// <c>Sessions()</c> view reflects the new project right away. No-op if the session isn't
    /// currently tracked in memory (e.g. process restarted since the session started).
    /// </summary>
    public void ForceSessionAttribution(string sessionId, ProjectAttribution attribution)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;

        session.ProjectKey = attribution.ProjectKey;
        session.ProjectName = attribution.ProjectName;
        session.AttributionConfidence = attribution.Confidence;
    }

    public IReadOnlyCollection<UsageSummaryRow> GetMetricsSummary(DateTimeOffset? from, DateTimeOffset? to)
    {
        var sessions = AllSessions()
            .Where(s => (from == null || s.StartedAt >= from) && (to == null || s.StartedAt <= to))
            .ToList();

        return sessions
            .GroupBy(s => (s.Platform, s.Model, s.User, s.ProjectKey, s.ProjectName, s.AttributionConfidence))
            .Select(g => new UsageSummaryRow(
                Platform: g.Key.Platform,
                Model: g.Key.Model,
                User: g.Key.User,
                ProjectKey: g.Key.ProjectKey,
                ProjectName: g.Key.ProjectName,
                AttributionConfidence: g.Key.AttributionConfidence,
                Sessions: g.Count(),
                ToolCalls: g.Sum(s => s.ToolCalls),
                Events: g.Sum(s => s.EventCounts.Values.Sum()),
                InputTokens: g.Sum(s => s.Usage.InputTokens),
                OutputTokens: g.Sum(s => s.Usage.OutputTokens),
                CacheReadTokens: g.Sum(s => s.Usage.CacheReadTokens),
                CacheCreationTokens: g.Sum(s => s.Usage.CacheCreationTokens)))
            .OrderByDescending(x => x.TotalTokens)
            .ToList();
    }

    public object Summary()
    {
        var sessions = AllSessions();
        return sessions
            .GroupBy(s => (s.Platform, s.Model, s.User, s.ProjectKey, s.ProjectName, s.AttributionConfidence))
            .Select(g => new
            {
                platform = g.Key.Platform,
                model = g.Key.Model,
                user = g.Key.User,
                projectKey = g.Key.ProjectKey,
                projectName = g.Key.ProjectName,
                attributionConfidence = g.Key.AttributionConfidence,
                sessions = g.Count(),
                toolCalls = g.Sum(s => s.ToolCalls),
                inputTokens = g.Sum(s => s.Usage.InputTokens),
                outputTokens = g.Sum(s => s.Usage.OutputTokens),
                cacheReadTokens = g.Sum(s => s.Usage.CacheReadTokens),
                cacheCreationTokens = g.Sum(s => s.Usage.CacheCreationTokens)
            })
            .OrderByDescending(x => x.inputTokens + x.outputTokens)
            .ToList();
    }
}
