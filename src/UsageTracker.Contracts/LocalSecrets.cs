using System.Security.Cryptography;
using System.Text.Json;

namespace UsageTracker.Contracts;

/// <summary>
/// Shared access to the local IPC token used to authenticate CLI→daemon calls. Kept in Contracts so the
/// daemon and the CLI agree on format and location without referencing each other. Whichever process runs
/// first creates the token; the other reads it. The file is access-restricted on Unix (chmod 600); on
/// Windows the per-user %APPDATA% ACL scopes it.
/// </summary>
public static class LocalSecrets
{
    private const string LocalTokenKey = "localToken";

    public static string GetOrCreateLocalToken()
    {
        Directory.CreateDirectory(DaemonPaths.ConfigDirectory);

        var secrets = Read();
        if (secrets.TryGetValue(LocalTokenKey, out var existing) && !string.IsNullOrWhiteSpace(existing))
            return existing;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        secrets[LocalTokenKey] = token;
        Write(secrets);
        return token;
    }

    public static string? TryReadLocalToken()
    {
        var secrets = Read();
        return secrets.TryGetValue(LocalTokenKey, out var token) && !string.IsNullOrWhiteSpace(token) ? token : null;
    }

    private static Dictionary<string, string> Read()
    {
        try
        {
            if (File.Exists(DaemonPaths.SecretsFilePath))
            {
                var json = File.ReadAllText(DaemonPaths.SecretsFilePath);
                return JsonSerializer.Deserialize(json, ContractsJsonContext.Default.DictionaryStringString) ?? New();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall through to an empty set; the caller will recreate as needed.
        }

        return New();
    }

    private static void Write(Dictionary<string, string> secrets)
    {
        var json = JsonSerializer.Serialize(secrets, ContractsJsonContext.Default.DictionaryStringString);
        File.WriteAllText(DaemonPaths.SecretsFilePath, json);

        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(DaemonPaths.SecretsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { /* non-fatal: pipe/socket ACL is the primary boundary */ }
    }

    private static Dictionary<string, string> New() => new(StringComparer.Ordinal);
}
