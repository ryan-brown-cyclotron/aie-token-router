using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace UsageTracker.Functions;

/// <summary>
/// Isolated-worker port of the former ASP.NET <c>HttpUserContext</c>. Identity precedence:
/// (1) token claims, (2) the <c>X-User-Email</c> header - trusted in <em>all</em> environments,
/// since hook-originated requests (Claude Code, GitHub Copilot) have no other identity mechanism
/// and this is an internal tool, not a security boundary - (3) <c>X-Dev-User-*</c> headers, kept
/// Development-only for existing local dev-testing workflows, (4) the MCP no-HttpContext fallback
/// below. Uses <see cref="IHostEnvironment"/> instead of <c>IWebHostEnvironment</c> and the
/// HttpContext surfaced by the Functions ASP.NET Core integration.
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

        var tokenUser = TryGetTokenUser(httpContext.User);
        if (tokenUser is not null) return tokenUser;

        var headerUser = TryGetUserEmailHeaderUser(httpContext.Request.Headers);
        if (headerUser is not null) return headerUser;

        return _environment.IsDevelopment() ? TryGetDevelopmentHeaderUser(httpContext.Request.Headers) : null;
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
