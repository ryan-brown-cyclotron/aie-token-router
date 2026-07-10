namespace UsageTracker;

/// <summary>
/// Bound from the <c>ToolOutputCompression</c> configuration section. Governs eligibility only;
/// whether compression actually happens depends on whether an <see cref="IToolOutputCompressor"/>
/// implementation is registered (see <see cref="ToolOutputCompressionService"/>).
/// </summary>
public sealed class ToolOutputCompressionOptions
{
    public const string SectionName = "ToolOutputCompression";

    /// <summary>Outputs shorter than this are not worth a round-trip to a compressor.</summary>
    public int MinimumCharacters { get; set; } = 2000;
}
