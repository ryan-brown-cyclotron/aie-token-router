namespace UsageTracker.Contracts;

/// <summary>
/// The single normalized request the CLI sends to the daemon for every invocation. The CLI holds no
/// daemon logic: it fills this envelope from argv + stdin and posts it over the local pipe/socket.
/// </summary>
/// <param name="Kind">"hook" | "command" | "trace" — how the daemon should treat the call.</param>
/// <param name="Name">
/// For hook/command calls this is the platform or command name (e.g. "claude-code", "copilot").
/// </param>
/// <param name="Args">Positional arguments after the command name.</param>
/// <param name="Stdin">The raw payload piped to the CLI (the hook JSON), or null when none.</param>
/// <param name="Trace">When true, the daemon returns human-readable diagnostics the CLI writes to stderr.</param>
/// <param name="SchemaVersion">Envelope version so daemon/CLI can evolve independently.</param>
public sealed record CommandEnvelope(
    string Kind,
    string Name,
    string[] Args,
    string? Stdin,
    bool Trace,
    string SchemaVersion = CommandEnvelope.CurrentSchemaVersion)
{
    public const string CurrentSchemaVersion = "1";

    public const string KindHook = "hook";
    public const string KindCommand = "command";
    public const string KindTrace = "trace";
}
