using System.Text.Json;

namespace UsageTracker.Contracts;

/// <summary>
/// The daemon's reply to a <see cref="CommandEnvelope"/>. The CLI writes <see cref="Stdout"/> verbatim
/// to its own stdout (the byte-clean result a hook consumer reads), <see cref="Diagnostics"/> to stderr
/// (only populated for trace calls), and exits with <see cref="ExitCode"/>.
/// </summary>
/// <param name="ExitCode">Process exit code the CLI should return. 0 = success / fail-open no-op.</param>
/// <param name="Stdout">Clean result for stdout. For hooks this is the hook response JSON body.</param>
/// <param name="Diagnostics">Trace/debug text for stderr; null on non-trace calls.</param>
/// <param name="Payload">Optional structured payload (kept for future use; not written to stdout).</param>
public sealed record CommandResponse(
    int ExitCode,
    string Stdout,
    string? Diagnostics = null,
    JsonElement? Payload = null)
{
    /// <summary>A fail-open success: empty stdout, exit 0. Used when the daemon has nothing to return.</summary>
    public static CommandResponse Empty(string? diagnostics = null) => new(0, string.Empty, diagnostics);
}
