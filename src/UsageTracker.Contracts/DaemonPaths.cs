using System.Security.Cryptography;
using System.Text;

namespace UsageTracker.Contracts;

/// <summary>
/// Resolves the per-user file-system locations and the local IPC endpoint name the daemon and CLI
/// must agree on. Everything is namespaced by a stable hash of the current user so multiple users on
/// one host never collide or reach each other's daemon.
/// </summary>
public static class DaemonPaths
{
    private const string AppName = "UsageTracker";

    /// <summary>Per-user config directory: %APPDATA%\UsageTracker on Windows, $XDG_CONFIG_HOME/usagetracker otherwise.</summary>
    public static string ConfigDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(baseDir, AppName.ToLowerInvariant());
        }
    }

    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>Restricted file holding the local IPC token and the MSAL cache. Kept out of <see cref="ConfigFilePath"/>.</summary>
    public static string SecretsFilePath => Path.Combine(ConfigDirectory, "secrets.json");

    /// <summary>Directory for the persisted MSAL token cache.</summary>
    public static string TokenCacheDirectory => Path.Combine(ConfigDirectory, "msal");

    /// <summary>Log file the daemon appends to; also surfaced by `usagetracker status`.</summary>
    public static string LogFilePath => Path.Combine(ConfigDirectory, "daemon.log");

    /// <summary>Windows named-pipe name (no leading \\.\pipe\ — Kestrel/PipeStream add it).</summary>
    public static string PipeName => $"{AppName}.{UserHash}";

    /// <summary>Unix domain socket path under $XDG_RUNTIME_DIR (falls back to a per-user temp dir).</summary>
    public static string SocketPath
    {
        get
        {
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var baseDir = string.IsNullOrWhiteSpace(runtimeDir) ? Path.GetTempPath() : runtimeDir;
            return Path.Combine(baseDir, $"usagetracker-{UserHash}.sock");
        }
    }

    /// <summary>Stable short hash of the current user (name + domain) for namespacing endpoints/files.</summary>
    public static string UserHash
    {
        get
        {
            var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }
    }
}
