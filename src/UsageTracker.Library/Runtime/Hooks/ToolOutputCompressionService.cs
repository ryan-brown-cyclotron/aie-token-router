using System.Text.Json;
using Microsoft.Extensions.Options;

namespace UsageTracker;

/// <summary>
/// Decides whether a hook's tool output is worth compressing and, if so, calls the registered
/// <see cref="IToolOutputCompressor"/> and builds the platform-specific response. Scope (from
/// docs/design/library.md): compress only large PostToolUse tool outputs; never compress raw payloads,
/// metadata, or small outputs. Always fail-open - including when no compressor is registered at
/// all, in which case hooks just ingest and log normally with no compression attempted.
/// </summary>
public sealed class ToolOutputCompressionService
{
    private readonly IToolOutputCompressor? _compressor;
    private readonly ToolOutputCompressionOptions _options;

    public ToolOutputCompressionService(IOptions<ToolOutputCompressionOptions> options, IToolOutputCompressor? compressor = null)
    {
        _compressor = compressor;
        _options = options.Value;
    }

    /// <summary>
    /// Returns the compression result when a compressor is registered and the event is an
    /// eligible, large PostToolUse output; otherwise null (nothing to compress).
    /// </summary>
    public async Task<ToolOutputCompression?> TryCompressAsync(HookEvent evt, JsonElement root, string? model, CancellationToken cancellationToken = default)
    {
        if (_compressor is null)
            return null;

        if (!IsPostToolUse(evt.EventName))
            return null;

        var output = ExtractToolOutput(root);
        if (string.IsNullOrEmpty(output))
            return null;

        return await _compressor.CompressAsync(output, model, cancellationToken);
    }

    /// <summary>
    /// Builds the observational hook response. Copilot documents a <c>modifiedResult</c> replacement
    /// field, so we return the compressed text there. Claude Code and Cursor stay observe-only until
    /// their output-replacement hook contracts are validated (see docs/design/headroom-sidecar.md).
    /// </summary>
    public static object BuildResponse(string platform, ToolOutputCompression? compression)
    {
        if (compression is null || !compression.Compressed)
            return new { };

        return platform.ToLowerInvariant() switch
        {
            "github-copilot" or "copilot" => new { modifiedResult = compression.Output },
            _ => new { }
        };
    }

    private static bool IsPostToolUse(string eventName) =>
        eventName.Equals("PostToolUse", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractToolOutput(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        // Best-effort across platforms; validate per-platform field names as vendors are confirmed.
        // tool_response may be a plain string or a structured object/array (e.g. Bash's
        // {stdout, stderr}) depending on the tool - Compress() below already knows how to factor
        // JSON objects/arrays, so pass its raw text through rather than requiring a string value.
        foreach (var name in new[] { "tool_response", "toolResponse", "tool_result", "toolResult", "output", "result", "stdout", "content" })
        {
            if (!root.TryGetProperty(name, out var value))
                continue;

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                _ => null
            };

            if (!string.IsNullOrEmpty(text))
                return text;
        }

        return null;
    }
}
