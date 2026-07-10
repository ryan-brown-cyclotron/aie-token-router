using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace UsageTracker.Tests;

public class ToolOutputCompressionServiceTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private sealed class FakeCompressor : IToolOutputCompressor
    {
        public Task<ToolOutputCompression> CompressAsync(string toolOutput, string? model, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolOutputCompression(true, "compressed:" + toolOutput, toolOutput.Length, 1));
    }

    private static ToolOutputCompressionService CreateService(bool withCompressor = true)
    {
        var options = Options.Create(new ToolOutputCompressionOptions());
        return new ToolOutputCompressionService(options, withCompressor ? new FakeCompressor() : null);
    }

    [Fact]
    public async Task Skips_events_that_are_not_post_tool_use()
    {
        var service = CreateService();
        var evt = HookEvent.FromJson("claude-code", Parse("{\"hook_event_name\":\"PreToolUse\"}"));

        var result = await service.TryCompressAsync(evt, Parse("{\"output\":\"a very long output string\"}"), null);

        Assert.Null(result);
    }

    [Fact]
    public async Task No_compressor_registered_means_no_compression_attempted()
    {
        // Mirrors production DI: no IToolOutputCompressor is registered by default, so the
        // parameter resolves to null and hooks just ingest/log with no compression attempted.
        var service = CreateService(withCompressor: false);
        var evt = HookEvent.FromJson("claude-code", Parse("{\"hook_event_name\":\"PostToolUse\"}"));

        var result = await service.TryCompressAsync(evt, Parse("{\"output\":\"a very long output string\"}"), null);

        Assert.Null(result);
    }

    [Fact]
    public async Task Structured_object_tool_response_is_compressed_via_its_raw_json_text()
    {
        // Real Claude Code tool_response values aren't always strings (e.g. Bash returns
        // {stdout, stderr}) - ExtractToolOutput must fall back to raw JSON text for those.
        var service = CreateService();
        var evt = HookEvent.FromJson("claude-code", Parse("{\"hook_event_name\":\"PostToolUse\"}"));

        var result = await service.TryCompressAsync(
            evt, Parse("{\"tool_response\":{\"stdout\":\"a very long stdout output string\",\"stderr\":\"\"}}"), null);

        Assert.NotNull(result);
        Assert.True(result!.Compressed);
    }

    [Fact]
    public void BuildResponse_returns_modifiedResult_for_copilot()
    {
        var compression = new ToolOutputCompression(true, "compressed text", 100, 10);

        var json = JsonSerializer.Serialize(ToolOutputCompressionService.BuildResponse("github-copilot", compression));

        Assert.Contains("modifiedResult", json);
        Assert.Contains("compressed text", json);
    }

    [Fact]
    public void BuildResponse_stays_observe_only_for_claude_code()
    {
        var compression = new ToolOutputCompression(true, "compressed text", 100, 10);

        var json = JsonSerializer.Serialize(ToolOutputCompressionService.BuildResponse("claude-code", compression));

        Assert.DoesNotContain("modifiedResult", json);
    }
}

public class ProjectContextServiceTests
{
    private sealed class FakeUserContext : IUserContext
    {
        public CurrentUser? User { get; init; }
        public CurrentUser? TryGetCurrentUser() => User;
    }

    private static ProjectContextService CreateService(IUsageRepository repository, IUserContext userContext, UsageStore? store = null) =>
        new(repository, userContext, store ?? new UsageStore(), NullLogger<ProjectContextService>.Instance);

    [Fact]
    public async Task Set_without_identity_is_unauthorized()
    {
        var service = CreateService(new InMemoryUsageRepository(), new FakeUserContext());

        var result = await service.SetAsync(new ProjectContextRequest("k", "n", null, null, null, null));

        Assert.True(result.Unauthorized);
    }

    [Fact]
    public async Task Set_with_identity_returns_window()
    {
        var repository = new InMemoryUsageRepository();
        var user = new FakeUserContext { User = new CurrentUser("id", "name", "user@example.com") };
        var service = CreateService(repository, user);

        var result = await service.SetAsync(new ProjectContextRequest("proj", "Project", null, null, null, null));

        Assert.False(result.Unauthorized);
        Assert.Null(result.BadRequest);
        Assert.NotNull(result.Window);
        Assert.Equal("proj", result.Window!.ProjectKey);
        Assert.Equal("user@example.com", result.Window.User);
    }

    [Fact]
    public async Task Set_with_blank_project_is_bad_request()
    {
        var user = new FakeUserContext { User = new CurrentUser("id", "name", "user@example.com") };
        var service = CreateService(new InMemoryUsageRepository(), user);

        var result = await service.SetAsync(new ProjectContextRequest("  ", "", null, null, null, null));

        Assert.Null(result.Window);
        Assert.NotNull(result.BadRequest);
    }

    [Fact]
    public async Task Setting_context_with_sessionId_backfills_already_ingested_events_for_that_session()
    {
        var repository = new InMemoryUsageRepository();
        var receivedAt = DateTimeOffset.UtcNow;
        var priorEvent = NormalizedUsageEvent.From(
            HookEvent.FromJson("claude-code", JsonDocument.Parse("{\"session_id\":\"s1\",\"hook_event_name\":\"PreToolUse\"}").RootElement) with { UserEmail = "user@example.com" },
            TokenUsage.Empty, "claude-opus-4-8", ProjectAttribution.Unknown, receivedAt);
        await repository.RecordEventAsync(priorEvent);

        var user = new FakeUserContext { User = new CurrentUser("id", "name", "user@example.com") };
        var store = new UsageStore();
        var service = CreateService(repository, user, store);

        var result = await service.SetAsync(new ProjectContextRequest("proj", "Project", null, "s1", null, null));

        Assert.False(result.Unauthorized);
        var backfilled = await repository.GetEventAsync(priorEvent.Id);
        Assert.NotNull(backfilled);
        Assert.Equal("proj", backfilled!.ProjectKey);
        Assert.Equal("Project", backfilled.ProjectName);
        Assert.Equal("session-backfill", backfilled.AttributionConfidence);
    }

    [Fact]
    public async Task Backfill_never_overwrites_confidently_attributed_events()
    {
        var repository = new InMemoryUsageRepository();
        var receivedAt = DateTimeOffset.UtcNow;
        var confidentEvent = NormalizedUsageEvent.From(
            HookEvent.FromJson("claude-code", JsonDocument.Parse("{\"session_id\":\"s1\",\"hook_event_name\":\"PreToolUse\"}").RootElement) with { UserEmail = "user@example.com" },
            TokenUsage.Empty, "claude-opus-4-8", new ProjectAttribution("other-proj", "Other Project", "session"), receivedAt);
        await repository.RecordEventAsync(confidentEvent);

        var changed = await repository.BackfillSessionAttributionAsync("user@example.com", "s1", new ProjectAttribution("proj", "Project", "session-backfill"));

        Assert.Equal(0, changed);
        var unchanged = await repository.GetEventAsync(confidentEvent.Id);
        Assert.Equal("other-proj", unchanged!.ProjectKey);
        Assert.Equal("session", unchanged.AttributionConfidence);
    }

    [Fact]
    public async Task Backfill_ignores_events_for_other_users_and_sessions()
    {
        var repository = new InMemoryUsageRepository();
        var receivedAt = DateTimeOffset.UtcNow;

        var otherSession = NormalizedUsageEvent.From(
            HookEvent.FromJson("claude-code", JsonDocument.Parse("{\"session_id\":\"s2\",\"hook_event_name\":\"PreToolUse\"}").RootElement) with { UserEmail = "user@example.com" },
            TokenUsage.Empty, "claude-opus-4-8", ProjectAttribution.Unknown, receivedAt);
        var otherUser = NormalizedUsageEvent.From(
            HookEvent.FromJson("claude-code", JsonDocument.Parse("{\"session_id\":\"s1\",\"hook_event_name\":\"PreToolUse\"}").RootElement) with { UserEmail = "someone-else@example.com" },
            TokenUsage.Empty, "claude-opus-4-8", ProjectAttribution.Unknown, receivedAt);
        await repository.RecordEventAsync(otherSession);
        await repository.RecordEventAsync(otherUser);

        var changed = await repository.BackfillSessionAttributionAsync("user@example.com", "s1", new ProjectAttribution("proj", "Project", "session-backfill"));

        Assert.Equal(0, changed);
        Assert.Equal("unknown", (await repository.GetEventAsync(otherSession.Id))!.AttributionConfidence);
        Assert.Equal("unknown", (await repository.GetEventAsync(otherUser.Id))!.AttributionConfidence);
    }
}
