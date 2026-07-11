using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Daemon.Configuration;

/// <summary>
/// Reads/writes the non-secret <see cref="DaemonConfig"/> and the local IPC token. The config lives in a
/// plain JSON file; the local token lives in a separate access-restricted secrets file so it never sits
/// next to the readable config. Both daemon and CLI resolve their locations from <see cref="DaemonPaths"/>.
/// </summary>
public sealed class DaemonConfigStore
{
    private readonly ILogger<DaemonConfigStore> _logger;

    public DaemonConfigStore(ILogger<DaemonConfigStore> logger) => _logger = logger;

    public DaemonConfig Load()
    {
        try
        {
            if (File.Exists(DaemonPaths.ConfigFilePath))
            {
                var json = File.ReadAllText(DaemonPaths.ConfigFilePath);
                var config = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.DaemonConfig);
                if (config is not null) return config;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read daemon config at {Path}; using defaults", DaemonPaths.ConfigFilePath);
        }

        return new DaemonConfig();
    }

    public void Save(DaemonConfig config)
    {
        Directory.CreateDirectory(DaemonPaths.ConfigDirectory);
        var json = JsonSerializer.Serialize(config, ContractsJsonContext.Default.DaemonConfig);
        File.WriteAllText(DaemonPaths.ConfigFilePath, json);
    }

    /// <summary>Returns the per-install local token, creating and persisting one on first use.</summary>
    public string GetOrCreateLocalToken() => LocalSecrets.GetOrCreateLocalToken();
}
