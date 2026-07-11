using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using UsageTracker.Contracts;
using UsageTracker.Daemon.Configuration;

namespace UsageTracker.Daemon.Auth;

/// <summary>
/// Snapshot of the daemon's current authentication state, surfaced by <c>usagetracker status</c>.
/// </summary>
public sealed record TokenStatus(
    string State,               // "acquired" | "device-code-pending" | "unauthenticated" | "not-configured"
    string? UserEmail,
    string? UserId,
    DateTimeOffset? ExpiresOn,
    string? PendingDeviceCodeMessage);

/// <summary>
/// Acquires and refreshes an Entra ID <em>user</em> access token for the backend API scope, using the
/// enrolled/Entra-joined device: WAM broker silent SSO first, interactive broker next, and device-code
/// as the headless/WSL fallback. Runs a background refresh so a valid token is always ready when a hook
/// fires. Also exposes the signed-in identity so local ingestion records the real user.
/// </summary>
public sealed class EntraTokenService : BackgroundService
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly DaemonConfigStore _configStore;
    private readonly ILogger<EntraTokenService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPublicClientApplication? _app;
    private DaemonConfig _config = new();
    private AuthenticationResult? _current;
    private string? _pendingDeviceCode;

    public EntraTokenService(DaemonConfigStore configStore, ILogger<EntraTokenService> logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    /// <summary>True when Entra auth is configured (tenant/client/scope present); false in local dev.</summary>
    public bool IsEntraConfigured => IsConfigured(_config);

    public CurrentUser? CurrentIdentity
    {
        get
        {
            var result = _current;
            if (result is null) return DevFallbackIdentity();

            var oid = FindClaim(result, "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier", "sub");
            var upn = FindClaim(result, "preferred_username", "upn", "email") ?? result.Account?.Username;
            var name = FindClaim(result, "name") ?? upn ?? oid;
            if (string.IsNullOrWhiteSpace(oid) && string.IsNullOrWhiteSpace(upn)) return DevFallbackIdentity();

            return new CurrentUser(oid ?? upn!, name ?? "unknown", upn ?? string.Empty);
        }
    }

    /// <summary>
    /// Development-only identity used when Entra auth is not configured. Because production requires Entra +
    /// Easy Auth, an unconfigured daemon is by definition a local/dev setup, so local ingestion is stamped
    /// with the OS-signed-in user for realistic attribution. This identity is never trusted as a production
    /// principal: with no Entra token the backend mirror carries no Bearer, and Easy Auth would reject it.
    /// </summary>
    private CurrentUser? DevFallbackIdentity()
    {
        if (IsConfigured(_config)) return null;

        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user)) return null;

        var domain = Environment.UserDomainName;
        var id = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
        return new CurrentUser(id, user, string.Empty);
    }

    public TokenStatus Status()
    {
        if (!IsConfigured(_config))
        {
            var dev = DevFallbackIdentity();
            return dev is not null
                ? new TokenStatus("local-dev (unverified)", dev.UserKey, dev.UserId, null, null)
                : new TokenStatus("not-configured", null, null, null, null);
        }

        var result = _current;
        var identity = CurrentIdentity;
        if (result is not null && result.ExpiresOn > DateTimeOffset.UtcNow)
            return new TokenStatus("acquired", identity?.UserEmail, identity?.UserId, result.ExpiresOn, null);

        if (_pendingDeviceCode is not null)
            return new TokenStatus("device-code-pending", identity?.UserEmail, identity?.UserId, null, _pendingDeviceCode);

        return new TokenStatus("unauthenticated", identity?.UserEmail, identity?.UserId, null, null);
    }

    /// <summary>Returns a valid access token, acquiring/refreshing on demand. Null when auth is unavailable.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var current = _current;
        if (current is not null && current.ExpiresOn - RefreshSkew > DateTimeOffset.UtcNow)
            return current.AccessToken;

        return (await AcquireAsync(cancellationToken))?.AccessToken;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _config = _configStore.Load();
                if (IsConfigured(_config))
                    await AcquireAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Background token refresh failed; will retry");
            }

            // Re-check ~every 5 min; refresh happens ~5 min before expiry via the skew above.
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<AuthenticationResult?> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while we waited on the gate.
            var current = _current;
            if (current is not null && current.ExpiresOn - RefreshSkew > DateTimeOffset.UtcNow)
                return current;

            _config = _configStore.Load();
            if (!IsConfigured(_config))
            {
                _logger.LogDebug("Entra auth not configured (tenantId/clientId/scope missing); skipping token acquisition");
                return null;
            }

            var app = await GetOrBuildAppAsync();
            var scopes = new[] { _config.Scope! };

            var accounts = await app.GetAccountsAsync();
            var account = accounts.FirstOrDefault();

            try
            {
                _current = account is not null
                    ? await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken)
                    : await app.AcquireTokenSilent(scopes, PublicClientApplication.OperatingSystemAccount).ExecuteAsync(cancellationToken);
                _pendingDeviceCode = null;
                _logger.LogInformation("Acquired Entra token silently for {User}", CurrentIdentity?.UserEmail);
                return _current;
            }
            catch (MsalUiRequiredException)
            {
                return await AcquireWithUserInteractionAsync(app, scopes, account, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Token acquisition failed");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AuthenticationResult?> AcquireWithUserInteractionAsync(
        IPublicClientApplication app, string[] scopes, IAccount? account, CancellationToken cancellationToken)
    {
        // On an Entra-joined Windows device the broker completes interactively without a password prompt.
        if (BrokerAvailable())
        {
            try
            {
                var builder = app.AcquireTokenInteractive(scopes);
                if (account is not null) builder = builder.WithAccount(account);
                _current = await builder.ExecuteAsync(cancellationToken);
                _pendingDeviceCode = null;
                _logger.LogInformation("Acquired Entra token interactively (broker) for {User}", CurrentIdentity?.UserEmail);
                return _current;
            }
            catch (MsalException ex)
            {
                _logger.LogWarning(ex, "Interactive broker acquisition failed; falling back to device code");
            }
        }

        // Headless / WSL / Linux without a broker: surface a device code the user completes once.
        _current = await app.AcquireTokenWithDeviceCode(scopes, deviceCode =>
        {
            _pendingDeviceCode = deviceCode.Message;
            _logger.LogWarning("Entra device-code sign-in required: {Message}", deviceCode.Message);
            return Task.CompletedTask;
        }).ExecuteAsync(cancellationToken);

        _pendingDeviceCode = null;
        _logger.LogInformation("Acquired Entra token via device code for {User}", CurrentIdentity?.UserEmail);
        return _current;
    }

    private async Task<IPublicClientApplication> GetOrBuildAppAsync()
    {
        if (_app is not null) return _app;

        var builder = PublicClientApplicationBuilder
            .Create(_config.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{_config.TenantId}")
            .WithDefaultRedirectUri();

        if (BrokerAvailable())
        {
            builder = builder
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
                .WithParentActivityOrWindow(GetForegroundWindowHandle);
        }

        _app = builder.Build();
        await RegisterTokenCacheAsync(_app);
        return _app;
    }

    private static async Task RegisterTokenCacheAsync(IPublicClientApplication app)
    {
        Directory.CreateDirectory(DaemonPaths.TokenCacheDirectory);
        var storage = new StorageCreationPropertiesBuilder("usagetracker.msalcache", DaemonPaths.TokenCacheDirectory)
            .WithMacKeyChain("com.cyclotron.usagetracker", "MSALCache")
            .WithLinuxKeyring(
                "com.cyclotron.usagetracker",
                MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "MSAL token cache for UsageTracker",
                new KeyValuePair<string, string>("Version", "1"),
                new KeyValuePair<string, string>("Product", "UsageTracker"))
            .Build();

        var helper = await MsalCacheHelper.CreateAsync(storage);
        helper.RegisterCache(app.UserTokenCache);
    }

    private static bool IsConfigured(DaemonConfig config) =>
        !string.IsNullOrWhiteSpace(config.TenantId) &&
        !string.IsNullOrWhiteSpace(config.ClientId) &&
        !string.IsNullOrWhiteSpace(config.Scope);

    // WAM broker is supported on Windows via Microsoft.Identity.Client.Broker. macOS/Linux/WSL fall back
    // to the device-code flow (their access token still persists in the MSAL cache after first sign-in).
    private static bool BrokerAvailable() => OperatingSystem.IsWindows();

    private static string? FindClaim(AuthenticationResult result, params string[] types)
    {
        var principal = result.ClaimsPrincipal;
        if (principal is null) return null;
        return types
            .Select(t => principal.FindFirst(t)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static IntPtr GetForegroundWindowHandle()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var handle = GetConsoleWindow();
        return handle != IntPtr.Zero ? handle : GetDesktopWindow();
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
