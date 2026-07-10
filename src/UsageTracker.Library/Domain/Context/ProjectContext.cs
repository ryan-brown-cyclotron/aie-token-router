using Newtonsoft.Json;

namespace UsageTracker;

public sealed record ProjectContextRequest(
    string ProjectKey,
    string ProjectName,
    string? Platform,
    string? SessionId,
    string? Cwd,
    int? ExpiresInMinutes);

public sealed record ProjectContextWindow(
    [property: JsonProperty("id")] string Id,
    [property: JsonProperty("user")] string User,
    [property: JsonProperty("projectKey")] string ProjectKey,
    [property: JsonProperty("projectName")] string ProjectName,
    [property: JsonProperty("platform")] string? Platform,
    [property: JsonProperty("sessionId")] string? SessionId,
    [property: JsonProperty("cwd")] string? Cwd,
    [property: JsonProperty("source")] string Source,
    [property: JsonProperty("startedAt")] DateTimeOffset StartedAt,
    [property: JsonProperty("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonProperty("endedAt")] DateTimeOffset? EndedAt)
{
    public bool IsActiveAt(DateTimeOffset at) =>
        EndedAt is null &&
        StartedAt <= at &&
        (ExpiresAt is null || ExpiresAt > at);
}

public sealed record ProjectAttribution(
    string ProjectKey,
    string ProjectName,
    string Confidence)
{
    public static ProjectAttribution Unknown { get; } = new("unknown", "Unknown", "unknown");
    public static ProjectAttribution Ambiguous { get; } = new("unknown", "Unknown", "ambiguous");
}
