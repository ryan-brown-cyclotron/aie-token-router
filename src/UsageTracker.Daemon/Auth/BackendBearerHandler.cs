using System.Net.Http.Headers;

namespace UsageTracker.Daemon.Auth;

/// <summary>
/// Attaches the Entra user token as a Bearer header on every outbound call to the backend. This is the
/// replacement for the old unauthenticated <c>X-User-Email</c> trust model: the Container App's Easy Auth
/// validates this token and injects the verified principal for the backend to read.
/// </summary>
public sealed class BackendBearerHandler : DelegatingHandler
{
    private readonly EntraTokenService _tokenService;
    private readonly ILogger<BackendBearerHandler> _logger;

    public BackendBearerHandler(EntraTokenService tokenService, ILogger<BackendBearerHandler> logger)
    {
        _tokenService = tokenService;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokenService.GetAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else
            _logger.LogWarning("No Entra token available; forwarding backend request unauthenticated (will 401 in production)");

        return await base.SendAsync(request, cancellationToken);
    }
}
