namespace UsageTracker.Daemon.Auth;

/// <summary>
/// Supplies the host-agnostic <see cref="IUserContext"/> to the reused UsageTracker.Library pipeline,
/// backed by the identity of the Entra token the daemon acquired for the signed-in device user. This is
/// what stamps local ingestion with the real user instead of an unauthenticated header.
/// </summary>
public sealed class DaemonUserContext : IUserContext
{
    private readonly EntraTokenService _tokenService;

    public DaemonUserContext(EntraTokenService tokenService) => _tokenService = tokenService;

    public CurrentUser? TryGetCurrentUser() => _tokenService.CurrentIdentity;
}
