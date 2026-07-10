namespace UsageTracker;

/// <summary>
/// Result of attempting to compress a tool output via the registered
/// <see cref="IToolOutputCompressor"/>. When <see cref="Compressed"/> is false the
/// <see cref="Output"/> is the original text unchanged (fail-open, including when no compressor is
/// registered at all), and the token counts are zero.
/// </summary>
public sealed record ToolOutputCompression(bool Compressed, string Output, long TokensBefore, long TokensAfter)
{
    public long TokensSaved => Math.Max(0, TokensBefore - TokensAfter);

    public static ToolOutputCompression Unchanged(string output) => new(false, output, 0, 0);
}
