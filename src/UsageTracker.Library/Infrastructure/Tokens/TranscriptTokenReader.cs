using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace UsageTracker;

public sealed record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    int TurnsCounted)
{
    public static TokenUsage Empty => new(0, 0, 0, 0, 0);

    public TokenUsage Add(TokenUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheCreationTokens + other.CacheCreationTokens,
        CacheReadTokens + other.CacheReadTokens,
        TurnsCounted + other.TurnsCounted);

    /// <summary>
    /// Try to parse inline usage from a hook event payload body (when no transcript path is available).
    /// Handles both snake_case and camelCase field names and both flat and nested shapes.
    /// Returns <see cref="Empty"/> if no recognizable usage fields are present.
    /// </summary>
    public static TokenUsage FromPayload(JsonElement root)
    {
        // Check top-level `usage` object first, then fall back to flat fields.
        var u = TryGetUsageElement(root);
        if (u is null) return Empty;

        var input = GetLongFromElement(u.Value, "input_tokens", "inputTokens");
        var output = GetLongFromElement(u.Value, "output_tokens", "outputTokens");
        var cacheCreate = GetLongFromElement(u.Value, "cache_creation_input_tokens", "cacheCreationInputTokens");
        var cacheRead = GetLongFromElement(u.Value, "cache_read_input_tokens", "cacheReadInputTokens");

        return (input | output | cacheCreate | cacheRead) == 0
            ? Empty
            : new TokenUsage(input, output, cacheCreate, cacheRead, 1);
    }

    private static JsonElement? TryGetUsageElement(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var u)) return u;
        // Also accept message.usage (Claude Code shape)
        if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var mu)) return mu;
        // Flat payload - check whether any known token field exists at the root level
        if (root.TryGetProperty("input_tokens", out _) || root.TryGetProperty("inputTokens", out _)) return root;
        return null;
    }

    private static long GetLongFromElement(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (el.TryGetProperty(name, out var v) && v.TryGetInt64(out var n))
                return n;
        return 0;
    }
}

/// <summary>
/// Transcript files (referenced by transcript_path in hook payloads) are JSONL:
/// one JSON object per line, growing over the session. Token usage lives on
/// assistant-turn entries under message.usage (or occasionally a top-level
/// "usage" object) - not in the hook payload itself. We tail the file rather
/// than re-parsing it whole, since Stop/PostToolUse can fire many times per
/// session and we only want each transcript line counted once.
/// </summary>
public sealed class TranscriptTokenReader
{
    // transcript path -> bytes already consumed
    private readonly ConcurrentDictionary<string, long> _offsets = new();
    private readonly ILogger<TranscriptTokenReader> _logger;

    public TranscriptTokenReader(ILogger<TranscriptTokenReader> logger) => _logger = logger;

    public (TokenUsage Usage, string? ModelSeen) ReadNewUsage(string transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
            return (TokenUsage.Empty, null);

        try
        {
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var startOffset = _offsets.GetOrAdd(transcriptPath, 0);

            // File got shorter than our recorded offset (truncated/rotated) - start over.
            if (startOffset > stream.Length) startOffset = 0;

            stream.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();

            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0)
                return (TokenUsage.Empty, null); // no complete line yet - wait for the next hook fire

            var completeChunk = text[..lastNewline];
            _offsets[transcriptPath] = startOffset + Encoding.UTF8.GetByteCount(completeChunk) + 1;

            var usage = TokenUsage.Empty;
            string? modelSeen = null;

            foreach (var line in completeChunk.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parsed = TryExtractUsage(line);
                if (parsed is null) continue;
                usage = usage.Add(parsed.Value.Usage);
                modelSeen ??= parsed.Value.Model;
            }

            return (usage, modelSeen);
        }
        catch (Exception ex)
        {
            // Local-file access can fail transiently (rotation, lock, permissions).
            // Never let this take down the hook response - just skip this read.
            _logger.LogWarning(ex, "Could not read transcript at {Path}", transcriptPath);
            return (TokenUsage.Empty, null);
        }
    }

    private static (TokenUsage Usage, string? Model)? TryExtractUsage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            // Claude Code shape: { "message": { "model": "...", "usage": { ... } } }
            // Fallback shape:     { "model": "...", "usage": { ... } }
            var usageElement = TryGetNested(root, "message", "usage") ?? TryGet(root, "usage");
            if (usageElement is null) return null;

            var model = TryGetString(TryGetNested(root, "message", "model") ?? TryGet(root, "model"));

            var usage = new TokenUsage(
                InputTokens: GetLong(usageElement.Value, "input_tokens", "inputTokens"),
                OutputTokens: GetLong(usageElement.Value, "output_tokens", "outputTokens"),
                CacheCreationTokens: GetLong(usageElement.Value, "cache_creation_input_tokens", "cacheCreationInputTokens"),
                CacheReadTokens: GetLong(usageElement.Value, "cache_read_input_tokens", "cacheReadInputTokens"),
                TurnsCounted: 1);

            return (usage, model);
        }
        catch (JsonException)
        {
            return null; // malformed or partial line - skip it, don't crash the whole read
        }
    }

    private static JsonElement? TryGet(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : null;

    private static JsonElement? TryGetNested(JsonElement el, string outer, string inner)
    {
        var o = TryGet(el, outer);
        return o is null ? null : TryGet(o.Value, inner);
    }

    private static string? TryGetString(JsonElement? el) =>
        el?.ValueKind == JsonValueKind.String ? el.Value.GetString() : null;

    private static long GetLong(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetInt64();
        }
        return 0;
    }
}
