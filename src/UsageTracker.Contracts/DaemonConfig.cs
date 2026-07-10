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

    /// <summary>Full path to the daemon executable, recorded by `init` so the CLI can auto-start it on demand.</summary>
    public string? DaemonExecutablePath { get; set; }

    /// <summary>Minimum log level for the daemon ("Information", "Debug", ...).</summary>
    public string LogLevel { get; set; } = "Information";
}
