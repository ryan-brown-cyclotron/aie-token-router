using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace UsageTracker.Functions;

/// <summary>
/// Isolated-worker port of the former ASP.NET <c>HttpUserContext</c>. Identity precedence:
/// (1) the Azure Container Apps <em>Easy Auth</em> principal (<c>X-MS-CLIENT-PRINCIPAL*</c> headers),
/// which is the verified caller identity in production once built-in authentication is enabled;
/// (2) token claims on <c>HttpContext.User</c> (populated only if in-worker JWT validation is ever
/// added); (3) the <c>X-User-Email</c> header - <em>Development only</em>, since the local daemon now
/// supplies a real Entra token in production and this unauthenticated header must not be trusted there;
/// (4) <c>X-Dev-User-*</c> headers (Development only); (5) the MCP no-HttpContext fallback below.
/// Uses <see cref="IHostEnvironment"/> and the HttpContext surfaced by the Functions ASP.NET Core
/// integration.
///
/// MCP tool trigger invocations carry no HttpContext - <c>ToolInvocationContext</c> exposes only
/// the tool name and arguments, so there are no headers to read at all. For that path, Development
/// falls back to a single configured local identity (Mcp:DefaultUser*), the same role the old
/// stdio MCP server's UsageTracker:UserEmail setting played.
/// </summary>
public sealed class FunctionsUserContext : IUserContext
{
    private readonly HttpContextHolder _httpContextHolder;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public FunctionsUserContext(HttpContextHolder httpContextHolder, IHostEnvironment environment, IConfiguration configuration)
    {
        _httpContextHolder = httpContextHolder;
        _environment = environment;
        _configuration = configuration;
    }

    public CurrentUser? TryGetCurrentUser()
    {
        var httpContext = _httpContextHolder.Current;
        if (httpContext is null)
            return _environment.IsDevelopment() ? TryGetConfiguredDevelopmentUser() : null;

        // Production identity: the Container Apps Easy Auth principal, injected by the platform after it
        // validates the daemon's Entra Bearer token. This is the only trusted identity in production.
        var easyAuthUser = TryGetEasyAuthUser(httpContext.Request.Headers);
        if (easyAuthUser is not null) return easyAuthUser;

        var tokenUser = TryGetTokenUser(httpContext.User);
        if (tokenUser is not null) return tokenUser;

        if (!_environment.IsDevelopment())
            return null;

        // Development-only fallbacks. X-User-Email was previously trusted everywhere; it is no longer,
        // because the daemon now carries a verified identity and unauthenticated headers must not be
        // trusted in production.
        var headerUser = TryGetUserEmailHeaderUser(httpContext.Request.Headers);
        if (headerUser is not null) return headerUser;

        return TryGetDevelopmentHeaderUser(httpContext.Request.Headers);
    }

    /// <summary>
    /// Reads the Azure App Service / Container Apps Easy Auth principal. Prefers the base64 JSON
    /// <c>X-MS-CLIENT-PRINCIPAL</c> header (full claim set) and falls back to the flattened
    /// <c>X-MS-CLIENT-PRINCIPAL-ID</c> / <c>-NAME</c> headers.
    /// </summary>
    private static CurrentUser? TryGetEasyAuthUser(IHeaderDictionary headers)
    {
        var (oid, upn, name) = TryReadPrincipalClaims(FirstHeader(headers, "X-MS-CLIENT-PRINCIPAL"));

        oid ??= FirstHeader(headers, "X-MS-CLIENT-PRINCIPAL-ID");
        upn ??= FirstHeader(headers, "X-MS-CLIENT-PRINCIPAL-NAME");
        name ??= upn ?? oid;

        return string.IsNullOrWhiteSpace(oid) && string.IsNullOrWhiteSpace(upn)
            ? null
            : new CurrentUser(oid ?? upn!, name ?? "unknown", upn ?? string.Empty);
    }

    private static (string? Oid, string? Upn, string? Name) TryReadPrincipalClaims(string? encodedPrincipal)
    {
        if (string.IsNullOrWhiteSpace(encodedPrincipal))
            return (null, null, null);

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPrincipal));
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Array)
                return (null, null, null);

            string? oid = null, upn = null, name = null;
            foreach (var claim in claims.EnumerateArray())
            {
                var type = claim.TryGetProperty("typ", out var t) ? t.GetString() : null;
                var value = claim.TryGetProperty("val", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value))
                    continue;

                switch (type)
                {
                    case "oid":
                    case "http://schemas.microsoft.com/identity/claims/objectidentifier":
                        oid ??= value;
                        break;
                    case "preferred_username":
                    case "upn":
                    case ClaimTypes.Upn:
                    case ClaimTypes.Email:
                        upn ??= value;
                        break;
                    case "name":
                    case ClaimTypes.Name:
                        name ??= value;
                        break;
                }
            }

            return (oid, upn, name);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, null, null);
        }
    }

    private CurrentUser? TryGetConfiguredDevelopmentUser()
    {
        var email = _configuration["Mcp:DefaultUserEmail"];
        if (string.IsNullOrWhiteSpace(email)) return null;

        var id = _configuration["Mcp:DefaultUserId"] ?? email;
        var name = _configuration["Mcp:DefaultUserName"] ?? email;
        return new CurrentUser(id, name, email);
    }

    private static CurrentUser? TryGetTokenUser(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;

        var userId = FirstClaim(principal,
            "oid",
            "http://schemas.microsoft.com/identity/claims/objectidentifier",
            ClaimTypes.NameIdentifier,
            "sub");

        var email = FirstClaim(principal,
            "preferred_username",
            "upn",
            ClaimTypes.Upn,
            ClaimTypes.Email,
            "email");

        var name = FirstClaim(principal, "name", ClaimTypes.Name) ?? email ?? userId;

        return string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(email)
            ? null
            : new CurrentUser(userId ?? email!, name ?? "unknown", email ?? string.Empty);
    }

    private static CurrentUser? TryGetUserEmailHeaderUser(IHeaderDictionary headers)
    {
        var email = FirstHeader(headers, "X-User-Email");
        return string.IsNullOrWhiteSpace(email) ? null : new CurrentUser(email, email, email);
    }

    private static CurrentUser? TryGetDevelopmentHeaderUser(IHeaderDictionary headers)
    {
        var userId = FirstHeader(headers, "X-Dev-User-Id");
        var email = FirstHeader(headers, "X-Dev-User-Email");
        var name = FirstHeader(headers, "X-Dev-User-Name") ?? email ?? userId;

        return string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(email)
            ? null
            : new CurrentUser(userId ?? email!, name ?? "dev-user", email ?? string.Empty);
    }

    private static string? FirstClaim(ClaimsPrincipal principal, params string[] claimTypes) =>
        claimTypes.Select(type => principal.FindFirst(type)?.Value).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? FirstHeader(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var values) ? values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) : null;
}
