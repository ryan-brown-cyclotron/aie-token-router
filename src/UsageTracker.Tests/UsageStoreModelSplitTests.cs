namespace UsageTracker.Tests;

/// <summary>
/// A single session can legitimately span multiple models (e.g. a mid-session model switch).
/// These tests pin the in-memory <see cref="UsageStore"/> behavior: it keeps a per-model token
/// split (<see cref="SessionRecord.ModelUsage"/>) alongside the session-wide total, and the
/// dashboard <see cref="SessionView"/> surfaces that split.
/// </summary>
public class UsageStoreModelSplitTests
{
    private static HookEvent Event(string sessionId, string eventName, string? model) => new()
    {
        Platform = "claude-code",
        EventName = eventName,
        SessionId = sessionId,
        Model = model
    };

    [Fact]
    public void Session_spanning_two_models_keeps_a_per_model_token_split()
    {
        var store = new UsageStore();
        const string sessionId = "multi-model-session";

        store.RecordEvent(Event(sessionId, "PostToolUse", "claude-opus-4-8"), new TokenUsage(100, 40, 0, 0, 1), null, ProjectAttribution.Unknown);
        store.RecordEvent(Event(sessionId, "PostToolUse", "claude-opus-4-8"), new TokenUsage(50, 10, 0, 0, 1), null, ProjectAttribution.Unknown);
        store.RecordEvent(Event(sessionId, "PostToolUse", "claude-haiku-4-5"), new TokenUsage(20, 5, 0, 0, 1), null, ProjectAttribution.Unknown);

        var session = Assert.Single(store.AllSessions());

        // Session-wide total still sums across every model.
        Assert.Equal(170, session.Usage.InputTokens);
        Assert.Equal(55, session.Usage.OutputTokens);

        // ...and the per-model split is retained.
        Assert.Equal(2, session.ModelUsage.Count);
        Assert.Equal(150, session.ModelUsage["claude-opus-4-8"].InputTokens);
        Assert.Equal(50, session.ModelUsage["claude-opus-4-8"].OutputTokens);
        Assert.Equal(20, session.ModelUsage["claude-haiku-4-5"].InputTokens);
        Assert.Equal(5, session.ModelUsage["claude-haiku-4-5"].OutputTokens);
    }

    [Fact]
    public void Dashboard_session_view_exposes_the_per_model_split_ordered_by_tokens()
    {
        var store = new UsageStore();
        var dashboard = new DashboardQueryService(store, new InMemoryUsageRepository(), Microsoft.Extensions.Logging.Abstractions.NullLogger<DashboardQueryService>.Instance);
        const string sessionId = "s1";

        store.RecordEvent(Event(sessionId, "PostToolUse", "claude-haiku-4-5"), new TokenUsage(20, 5, 0, 0, 1), null, ProjectAttribution.Unknown);
        store.RecordEvent(Event(sessionId, "PostToolUse", "claude-opus-4-8"), new TokenUsage(100, 40, 0, 0, 1), null, ProjectAttribution.Unknown);

        var view = Assert.Single(dashboard.Sessions());

        Assert.Equal(2, view.Models.Count);
        // Ordered by total tokens descending: opus (140) before haiku (25).
        Assert.Equal("claude-opus-4-8", view.Models[0].Model);
        Assert.Equal("claude-haiku-4-5", view.Models[1].Model);
    }

    [Fact]
    public void Event_without_a_model_buckets_under_unknown_and_does_not_clobber_the_known_model()
    {
        var store = new UsageStore();
        const string sessionId = "s1";

        store.RecordEvent(Event(sessionId, "PostToolUse", "claude-opus-4-8"), new TokenUsage(100, 40, 0, 0, 1), null, ProjectAttribution.Unknown);
        store.RecordEvent(Event(sessionId, "Stop", null), new TokenUsage(0, 0, 0, 0, 0), null, ProjectAttribution.Unknown);

        var session = Assert.Single(store.AllSessions());

        // The model-less event must not overwrite the session's last known real model.
        Assert.Equal("claude-opus-4-8", session.Model);
        Assert.True(session.ModelUsage.ContainsKey("unknown"));
    }
}
