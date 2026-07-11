namespace UsageTracker.Contracts;

/// <summary>
/// Non-secret, per-user daemon configuration persisted as JSON in the user config directory
/// (see <see cref="DaemonPaths.ConfigFilePath"/>). Secrets (the local IPC token, the Entra token
/// cache) are never stored here — they live in an access-restricted secrets file / the MSAL cache.
/// </summary>
public sealed class DaemonConfig
{
    /// <summary>Backend base address the daemon forwards to (the Container App), e.g. https://api.company.com.</summary>
    public string? RemoteEndpoint { get; set; }

    /// <summary>Entra tenant id (GUID or domain) used to build the authority.</summary>
    public string? TenantId { get; set; }

    /// <summary>Entra public-client (daemon) application id.</summary>
    public string? ClientId { get; set; }

    /// <summary>Delegated scope requested for the backend API, e.g. api://&lt;backend&gt;/access_as_user.</summary>
    public string? Scope { get; set; }

    /// <summary>Optional override of the loopback HTTP port used for HTTP-only hook hosts (e.g. Copilot). 0 = disabled.</summary>
    public int LoopbackHttpPort { get; set; }

    /// <summary>
    /// Where tool-output compaction runs. <c>local</c> (default) = the daemon computes the compacted
    /// <c>modifiedResult</c> with no backend round-trip; <c>off</c> = ingest/mirror only, no compaction.
    /// </summary>
    public string CompressionMode { get; set; } = CompressionModes.Local;

    /// <summary>When true, the daemon exposes the project-context MCP tools over a loopback HTTP endpoint.</summary>
    public bool McpEnabled { get; set; }

    /// <summary>Loopback TCP port for the MCP endpoint (127.0.0.1:&lt;port&gt;/mcp). 0 = pick the default port.</summary>
    public int McpPort { get; set; }

    /// <summary>Full path to the daemon executable, recorded by `init` so the CLI can auto-start it on demand.</summary>
    public string? DaemonExecutablePath { get; set; }

    /// <summary>Minimum log level for the daemon ("Information", "Debug", ...).</summary>
    public string LogLevel { get; set; } = "Information";
}

/// <summary>Valid values for <see cref="DaemonConfig.CompressionMode"/>.</summary>
public static class CompressionModes
{
    public const string Local = "local";
    public const string Off = "off";

    /// <summary>The default loopback port for the MCP endpoint when <see cref="DaemonConfig.McpPort"/> is 0.</summary>
    public const int DefaultMcpPort = 47615;

    public static bool IsValid(string? mode) =>
        string.Equals(mode, Local, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(mode, Off, StringComparison.OrdinalIgnoreCase);
}
