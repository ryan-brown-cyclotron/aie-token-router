using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace UsageTracker.Tests;

public class HookEventTests
{
    [Fact]
    public void FromJson_ExtractsUserIdentityFromNestedAliases()
    {
        using var doc = JsonDocument.Parse("""
            {
              "hook_event_name": "SessionStart",
              "session_id": "session-1",
              "user": {
                "id": "user-1",
                "login": "ryan",
                "email": "ryan@example.com"
              }
            }
            """);

        var hookEvent = HookEvent.FromJson("copilot", doc.RootElement);

        Assert.Equal("SessionStart", hookEvent.EventName);
        Assert.Equal("session-1", hookEvent.SessionId);
        Assert.Equal("user-1", hookEvent.UserId);
        Assert.Equal("ryan", hookEvent.UserName);
        Assert.Equal("ryan@example.com", hookEvent.UserEmail);
        Assert.Equal("ryan@example.com", hookEvent.UserKey);
    }

    [Fact]
    public void FromJson_UsesUnknownUserKeyWhenIdentityIsMissing()
    {
        using var doc = JsonDocument.Parse("""
            {
              "hookEventName": "PreToolUse",
              "sessionId": "session-1"
            }
            """);

        var hookEvent = HookEvent.FromJson("copilot", doc.RootElement);

        Assert.Equal("unknown", hookEvent.UserKey);
    }
}

public class UsageStoreTests
{
    [Fact]
    public void Summary_GroupsUsageByPlatformModelAndUser()
    {
        var store = new UsageStore();

        store.RecordEvent(
            new HookEvent
            {
                Platform = "copilot",
                EventName = "PreToolUse",
                SessionId = "session-1",
                Model = "gpt-5",
                UserEmail = "ryan@example.com"
            },
            new TokenUsage(10, 4, 0, 2, 1),
            modelFromTranscript: null,
            new ProjectAttribution("token-optimization", "Token Optimization", "session"));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(store.Summary()));
        var summary = Assert.Single(doc.RootElement.EnumerateArray());

        Assert.Equal("copilot", summary.GetProperty("platform").GetString());
        Assert.Equal("gpt-5", summary.GetProperty("model").GetString());
        Assert.Equal("ryan@example.com", summary.GetProperty("user").GetString());
        Assert.Equal("token-optimization", summary.GetProperty("projectKey").GetString());
        Assert.Equal("Token Optimization", summary.GetProperty("projectName").GetString());
        Assert.Equal("session", summary.GetProperty("attributionConfidence").GetString());
        Assert.Equal(1, summary.GetProperty("sessions").GetInt32());
        Assert.Equal(1, summary.GetProperty("toolCalls").GetInt32());
        Assert.Equal(10, summary.GetProperty("inputTokens").GetInt64());
        Assert.Equal(4, summary.GetProperty("outputTokens").GetInt64());
        Assert.Equal(2, summary.GetProperty("cacheReadTokens").GetInt64());
    }
}

public class ProjectAttributionTests
{
    [Fact]
    public async Task ResolveAsync_UsesSessionSpecificContext()
    {
        var repository = new InMemoryUsageRepository();
        var now = DateTimeOffset.UtcNow;

        await repository.UpsertProjectContextAsync(new ProjectContextWindow(
            Id: "context-1",
            User: "ryan@example.com",
            ProjectKey: "token-optimization",
            ProjectName: "Token Optimization",
            Platform: "copilot",
            SessionId: "session-1",
            Cwd: null,
            Source: "manual",
            StartedAt: now.AddMinutes(-1),
            ExpiresAt: now.AddHours(4),
            EndedAt: null));

        var service = new ProjectAttributionService(repository);
        var attribution = await service.ResolveAsync(new HookEvent
        {
            Platform = "copilot",
            EventName = "PreToolUse",
            SessionId = "session-1",
            UserEmail = "ryan@example.com"
        }, now);

        Assert.Equal("token-optimization", attribution.ProjectKey);
        Assert.Equal("Token Optimization", attribution.ProjectName);
        Assert.Equal("session", attribution.Confidence);
    }

    [Fact]
    public async Task SummaryAsync_GroupsPersistedEventsByProjectAndConfidence()
    {
        var repository = new InMemoryUsageRepository();

        await repository.RecordEventAsync(new NormalizedUsageEvent(
            Id: "event-1",
            PartitionKey: "20260709:ryan@example.com:token-optimization",
            ReceivedAt: DateTimeOffset.UtcNow,
            Platform: "copilot",
            EventName: "PreToolUse",
            SessionId: "session-1",
            User: "ryan@example.com",
            Model: "gpt-5",
            ToolName: "Bash",
            Cwd: null,
            ProjectKey: "token-optimization",
            ProjectName: "Token Optimization",
            AttributionConfidence: "session",
            Usage: new TokenUsage(12, 3, 1, 2, 1),
            RawJson: "{}"));

        var row = Assert.Single(await repository.SummaryAsync(null, null));

        Assert.Equal("token-optimization", row.ProjectKey);
        Assert.Equal("Token Optimization", row.ProjectName);
        Assert.Equal("session", row.AttributionConfidence);
        Assert.Equal(1, row.ToolCalls);
        Assert.Equal(18, row.TotalTokens);
    }
}

public class TranscriptTokenReaderTests
{
    [Fact]
    public void ReadNewUsage_CountsCompleteLinesOnceAndIgnoresTrailingPartialLine()
    {
        var transcriptPath = Path.Combine(Path.GetTempPath(), $"usage-tracker-{Guid.NewGuid():N}.jsonl");

        try
        {
            File.WriteAllText(transcriptPath, """
                {"message":{"model":"claude-sonnet","usage":{"input_tokens":11,"output_tokens":7,"cache_creation_input_tokens":3,"cache_read_input_tokens":5}}}
                {"message":
                """);

            var reader = new TranscriptTokenReader(NullLogger<TranscriptTokenReader>.Instance);

            var firstRead = reader.ReadNewUsage(transcriptPath);
            var secondRead = reader.ReadNewUsage(transcriptPath);

            Assert.Equal("claude-sonnet", firstRead.ModelSeen);
            Assert.Equal(new TokenUsage(11, 7, 3, 5, 1), firstRead.Usage);
            Assert.Equal(TokenUsage.Empty, secondRead.Usage);
            Assert.Null(secondRead.ModelSeen);
        }
        finally
        {
            File.Delete(transcriptPath);
        }

    }
}