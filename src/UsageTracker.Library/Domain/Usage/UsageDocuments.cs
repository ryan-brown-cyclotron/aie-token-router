using System.Text.Json;
using Newtonsoft.Json;

namespace UsageTracker;

public sealed record NormalizedUsageEvent(
    [property: JsonProperty("id")] string Id,
    [property: JsonProperty("partitionKey")] string PartitionKey,
    [property: JsonProperty("receivedAt")] DateTimeOffset ReceivedAt,
    [property: JsonProperty("platform")] string Platform,
    [property: JsonProperty("eventName")] string EventName,
    [property: JsonProperty("sessionId")] string? SessionId,
    [property: JsonProperty("user")] string User,
    [property: JsonProperty("model")] string Model,
    [property: JsonProperty("toolName")] string? ToolName,
    [property: JsonProperty("cwd")] string? Cwd,
    [property: JsonProperty("projectKey")] string ProjectKey,
    [property: JsonProperty("projectName")] string ProjectName,
    [property: JsonProperty("attributionConfidence")] string AttributionConfidence,
    [property: JsonProperty("usage")] TokenUsage Usage,
    [property: JsonProperty("rawJson")] string RawJson)
{
    public static NormalizedUsageEvent From(HookEvent evt, TokenUsage usage, string model, ProjectAttribution attribution, DateTimeOffset receivedAt)
    {
        var user = evt.UserKey;
        var projectKey = string.IsNullOrWhiteSpace(attribution.ProjectKey) ? "unknown" : attribution.ProjectKey;

        return new NormalizedUsageEvent(
            Id: Guid.NewGuid().ToString("N"),
            PartitionKey: ComputePartitionKey(receivedAt, user, projectKey),
            ReceivedAt: receivedAt,
            Platform: evt.Platform,
            EventName: evt.EventName,
            SessionId: evt.SessionId,
            User: user,
            Model: model,
            ToolName: evt.ToolName,
            Cwd: evt.Cwd,
            ProjectKey: projectKey,
            ProjectName: attribution.ProjectName,
            AttributionConfidence: attribution.Confidence,
            Usage: usage,
            RawJson: evt.Raw.ValueKind == JsonValueKind.Undefined ? "{}" : evt.Raw.GetRawText());
    }

    /// <summary>
    /// Shared with backfill (<see cref="IUsageRepository.BackfillSessionAttributionAsync"/>) so a
    /// rewritten event's partition key is computed the exact same way as at original ingestion.
    /// </summary>
    public static string ComputePartitionKey(DateTimeOffset receivedAt, string user, string projectKey)
    {
        var partitionUser = user.Replace(':', '_');
        var partitionProject = (string.IsNullOrWhiteSpace(projectKey) ? "unknown" : projectKey).Replace(':', '_');
        return $"{receivedAt:yyyyMMdd}:{partitionUser}:{partitionProject}";
    }
}

public sealed record UsageSummaryRow(
    string Platform,
    string Model,
    string User,
    string ProjectKey,
    string ProjectName,
    string AttributionConfidence,
    int Sessions,
    int ToolCalls,
    int Events,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}
