namespace UsageTracker;

/// <summary>
/// Extension point for compressing large tool outputs before they're returned in a hook response.
/// No implementation is registered by default (see <c>AddUsageTrackerLibrary</c>) - hosts opt in by
/// registering one; without it, <see cref="ToolOutputCompressionService"/> skips compression
/// entirely and hooks just ingest and log normally.
/// </summary>
public interface IToolOutputCompressor
{
    /// <summary>
    /// Compresses a single tool output. Always fail-open: on any error, returns
    /// <see cref="ToolOutputCompression.Unchanged"/> with the original text.
    /// </summary>
    Task<ToolOutputCompression> CompressAsync(string toolOutput, string? model, CancellationToken cancellationToken = default);
}
