using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UsageTracker.Tests;

/// <summary>
/// End-to-end validation of the realistic "continuity" workflow this whole pipeline exists for:
/// hook traffic arrives for a session, project context gets set partway through (typically via the
/// usage_set_project_context MCP tool), and the entire window of that session's traffic - both
/// what already arrived and what arrives afterward - ends up organized under one project. Exercises
/// the real HookIngestionService, ProjectContextService, ProjectAttributionService, and
/// DashboardQueryService together (not each in isolation), against an InMemoryUsageRepository.
/// </summary>
public class SessionContinuityTests
{
    private sealed class FakeUserContext : IUserContext
    {
        public CurrentUser? User { get; set; }
        public CurrentUser? TryGetCurrentUser() => User;
    }

    private static JsonElement Payload(string sessionId, string eventName, long inputTokens, long outputTokens) =>
        JsonDocument.Parse(
            $"{{\"session_id\":\"{sessionId}\",\"hook_event_name\":\"{eventName}\",\"model\":\"claude-opus-4-8\",\"usage\":{{\"input_tokens\":{inputTokens},\"output_tokens\":{outputTokens}}}}}"
        ).RootElement;

    private sealed record Harness(
        HookIngestionService Hooks,
        ProjectContextService Context,
        DashboardQueryService Dashboard,
        InMemoryUsageRepository Repository,
        FakeUserContext UserContext);

    private static Harness CreateHarness(CurrentUser user)
    {
        var repository = new InMemoryUsageRepository();
        var store = new UsageStore();
        var attribution = new ProjectAttributionService(repository);
        var transcripts = new TranscriptTokenReader(NullLogger<TranscriptTokenReader>.Instance);
        var compression = new ToolOutputCompressionService(Options.Create(new ToolOutputCompressionOptions()));
        var userContext = new FakeUserContext { User = user };
        var metrics = new UsageTrackerMetrics();

        var hooks = new HookIngestionService(
            store, repository, attribution, transcripts, compression, userContext, metrics, NullLogger<HookIngestionService>.Instance);
        var context = new ProjectContextService(repository, userContext, store, NullLogger<ProjectContextService>.Instance);
        var dashboard = new DashboardQueryService(store, repository, NullLogger<DashboardQueryService>.Instance);

        return new Harness(hooks, context, dashboard, repository, userContext);
    }

    [Fact]
    public async Task Full_session_lifecycle_organizes_the_entire_traffic_window_under_one_project()
    {
        var user = new CurrentUser("id", "name", "user@example.com");
        var (hooks, context, dashboard, _, _) = CreateHarness(user);
        const string sessionId = "session-continuity-1";

        // Phase 1: traffic arrives before any project context exists - lands as unknown.
        await hooks.IngestAsync("claude-code", Payload(sessionId, "PreToolUse", 10, 0));
        await hooks.IngestAsync("claude-code", Payload(sessionId, "PostToolUse", 20, 5));

        var beforeContext = await dashboard.UsageAsync(null, null);
        Assert.All(beforeContext, row => Assert.Equal("unknown", row.ProjectKey));

        // Phase 2: the agent sets project context mid-session (e.g. via usage_set_project_context),
        // supplying the same sessionId the hook traffic has been using.
        var setResult = await context.SetAsync(new ProjectContextRequest("proj", "My Project", "claude-code", sessionId, null, null));
        Assert.False(setResult.Unauthorized);
        Assert.Equal("proj", setResult.Window!.ProjectKey);

        // Phase 3: traffic continues on the SAME session after context is set - resolves live via
        // the attribution service's "session" tier, no backfill needed for these.
        await hooks.IngestAsync("claude-code", Payload(sessionId, "PostToolUse", 30, 10));
        await hooks.IngestAsync("claude-code", Payload(sessionId, "Stop", 0, 0));

        // The whole window - pre-context (backfilled) and post-context (live-attributed) events -
        // should now be organized under one project, with correct combined totals.
        var projects = await dashboard.ProjectsAsync(null, null);
        var projectRow = Assert.Single(projects, p => p.ProjectKey == "proj");
        Assert.Equal(60, projectRow.InputTokens);  // 10 + 20 + 30
        Assert.Equal(15, projectRow.OutputTokens); // 0 + 5 + 10
        Assert.Equal(4, projectRow.Events);

        var usage = await dashboard.UsageAsync(null, null);
        Assert.DoesNotContain(usage, row => row.ProjectKey == "unknown");
        // The pre-context events were backfilled (confidence "session-backfill"); the post-context
        // events resolved live (confidence "session") - both confidences should be present, proving
        // the whole window was covered rather than only the events after the context call.
        Assert.Contains(usage, row => row.AttributionConfidence == "session-backfill");
        Assert.Contains(usage, row => row.AttributionConfidence == "session");

        var sessionView = Assert.Single(dashboard.Sessions(), s => s.SessionId == sessionId);
        Assert.Equal("proj", sessionView.ProjectKey);
        Assert.Equal("My Project", sessionView.ProjectName);
    }

    [Fact]
    public async Task Setting_context_for_one_users_session_does_not_bleed_into_another_users_traffic()
    {
        // Two different users/agent sessions hitting the same deployed system concurrently -
        // deliberately NOT same-user/same-platform, since the attribution service's "user-window"
        // fallback tier would (correctly) absorb any otherwise-unmatched same-user/same-platform
        // traffic into a single active window; that's real, intended behavior, not a bleed bug.
        // Real isolation is scoped by user - this proves setting context for one user's session
        // never touches another user's traffic, concurrent or not.
        var userA = new CurrentUser("id-a", "name-a", "user-a@example.com");
        var userB = new CurrentUser("id-b", "name-b", "user-b@example.com");
        var (hooks, context, dashboard, _, userContext) = CreateHarness(userA);

        await hooks.IngestAsync("claude-code", Payload("session-a", "PreToolUse", 10, 0));

        userContext.User = userB;
        await hooks.IngestAsync("claude-code", Payload("session-b", "PreToolUse", 40, 0));

        userContext.User = userA;
        await context.SetAsync(new ProjectContextRequest("proj-a", "Project A", "claude-code", "session-a", null, null));

        // More traffic on both users' sessions after only user A's context was set.
        await hooks.IngestAsync("claude-code", Payload("session-a", "PostToolUse", 5, 5));
        userContext.User = userB;
        await hooks.IngestAsync("claude-code", Payload("session-b", "PostToolUse", 15, 15));

        var projects = await dashboard.ProjectsAsync(null, null);
        var projectA = Assert.Single(projects, p => p.ProjectKey == "proj-a");
        Assert.Equal(15, projectA.InputTokens); // 10 + 5, only user A's events

        // User B was never given a project context, so their traffic - both before and after
        // user A's context call - stays unattributed rather than bleeding into project-a.
        var unknown = Assert.Single(projects, p => p.ProjectKey == "unknown");
        Assert.Equal(55, unknown.InputTokens); // 40 + 15, only user B's events

        var sessionAView = Assert.Single(dashboard.Sessions(), s => s.SessionId == "session-a");
        var sessionBView = Assert.Single(dashboard.Sessions(), s => s.SessionId == "session-b");
        Assert.Equal("proj-a", sessionAView.ProjectKey);
        Assert.Equal("unknown", sessionBView.ProjectKey);
    }
}
