using System.Text.Json;

namespace UsageTracker;

/// <summary>
/// A normalized view over a raw hook payload. Claude Code and Copilot use
/// different field-naming conventions (snake_case vs camelCase) and neither
/// is guaranteed to stay fixed, so we don't bind to a strict DTO - we pull
/// known fields out under a list of aliases and keep the raw JSON alongside.
/// </summary>
public sealed record HookEvent
{
    public required string Platform { get; init; }        // "claude-code" | "copilot" - from the route, not the payload
    public required string EventName { get; init; }        // e.g. "PreToolUse", "SessionStart"
    public string? SessionId { get; init; }
    public string? ToolName { get; init; }
    public string? TranscriptPath { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }                     // only reliably present on SessionStart
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public string? UserEmail { get; init; }
    public JsonElement Raw { get; init; }

    public string UserKey => UserEmail ?? UserId ?? UserName ?? "unknown";

    public static HookEvent FromJson(string platform, JsonElement root)
    {
        return new HookEvent
        {
            Platform = platform,
            EventName = FirstString(root, "hook_event_name", "hookEventName", "HookEventName", "event", "eventName")
                        ?? "Unknown",
            SessionId = FirstString(root, "session_id", "sessionId", "SessionId"),
            ToolName = FirstString(root, "tool_name", "toolName", "ToolName"),
            TranscriptPath = FirstString(root, "transcript_path", "transcriptPath", "TranscriptPath"),
            Cwd = FirstString(root, "cwd", "Cwd"),
            Model = FirstString(root, "model", "modelId", "model_id", "modelName", "model_name"),
            UserId = FirstString(root, "user_id", "userId", "UserId", "actor_id", "actorId", "ActorId")
                     ?? FirstNestedString(root, ("user", "id"), ("actor", "id"), ("sender", "id")),
            UserName = FirstString(root, "user", "username", "userName", "UserName", "actor", "actorName", "ActorName", "login")
                       ?? FirstNestedString(root, ("user", "name"), ("user", "login"), ("actor", "name"), ("actor", "login"), ("sender", "name")),
            UserEmail = FirstString(root, "email", "user_email", "userEmail", "UserEmail", "actor_email", "actorEmail", "ActorEmail")
                        ?? FirstNestedString(root, ("user", "email"), ("actor", "email"), ("sender", "email")),
            Raw = root.Clone()
        };
    }

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static string? FirstNestedString(JsonElement root, params (string Outer, string Inner)[] paths)
    {
        foreach (var path in paths)
        {
            if (root.TryGetProperty(path.Outer, out var outer) &&
                outer.ValueKind == JsonValueKind.Object &&
                outer.TryGetProperty(path.Inner, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
