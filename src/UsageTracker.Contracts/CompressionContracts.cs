namespace UsageTracker.Contracts;

/// <summary>
/// Daemon → backend request for the <c>remote</c> compression mode. Single-tool-output oriented so it
/// maps cleanly onto the daemon's <c>IToolOutputCompressor</c> contract; the backend adapts this into
/// the generic message shape the Headroom service expects.
/// </summary>
public sealed record CompressRequest(string ToolOutput, string? Model);

/// <summary>
/// Backend → daemon response for the <c>remote</c> compression mode. When <see cref="Compressed"/> is
/// false the <see cref="Text"/> is the original output unchanged (fail-open) and the token counts are 0.
/// </summary>
public sealed record CompressResponse(bool Compressed, string Text, long TokensBefore, long TokensAfter);
