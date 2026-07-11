using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using UsageTracker.Functions;

namespace UsageTracker.Tests;

public class FunctionsUserContextTests
{
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static FunctionsUserContext CreateContext(HttpContext httpContext, bool isDevelopment, IConfiguration? configuration = null)
    {
        var holder = new HttpContextHolder { Current = httpContext };
        var environment = new FakeHostEnvironment { EnvironmentName = isDevelopment ? Environments.Development : Environments.Production };
        return new FunctionsUserContext(holder, environment, configuration ?? new ConfigurationBuilder().Build());
    }

    [Fact]
    public void X_User_Email_header_is_ignored_outside_Development()
    {
        // The daemon now supplies a verified Entra identity in production, so the unauthenticated
        // X-User-Email header must not be trusted there.
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Email"] = "user@example.com";
        var context = CreateContext(httpContext, isDevelopment: false);

        Assert.Null(context.TryGetCurrentUser());
    }

    [Fact]
    public void X_User_Email_header_still_works_in_Development()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Email"] = "user@example.com";
        var context = CreateContext(httpContext, isDevelopment: true);

        var user = context.TryGetCurrentUser();

        Assert.NotNull(user);
        Assert.Equal("user@example.com", user!.UserEmail);
    }

    [Fact]
    public void EasyAuth_principal_header_resolves_identity_in_production()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = EncodePrincipal(
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", "oid-123"),
            ("preferred_username", "easyauth@example.com"),
            ("name", "Easy Auth User"));
        var context = CreateContext(httpContext, isDevelopment: false);

        var user = context.TryGetCurrentUser();

        Assert.NotNull(user);
        Assert.Equal("oid-123", user!.UserId);
        Assert.Equal("easyauth@example.com", user.UserEmail);
        Assert.Equal("Easy Auth User", user.UserName);
    }

    [Fact]
    public void EasyAuth_flattened_headers_resolve_identity_when_principal_absent()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = "oid-456";
        httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"] = "flat@example.com";
        var context = CreateContext(httpContext, isDevelopment: false);

        var user = context.TryGetCurrentUser();

        Assert.NotNull(user);
        Assert.Equal("oid-456", user!.UserId);
        Assert.Equal("flat@example.com", user.UserEmail);
    }

    [Fact]
    public void EasyAuth_principal_wins_over_X_User_Email_header_in_Development()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Email"] = "header@example.com";
        httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = EncodePrincipal(
            ("oid", "oid-789"),
            ("preferred_username", "verified@example.com"));
        var context = CreateContext(httpContext, isDevelopment: true);

        var user = context.TryGetCurrentUser();

        Assert.NotNull(user);
        Assert.Equal("verified@example.com", user!.UserEmail);
    }

    private static string EncodePrincipal(params (string Type, string Value)[] claims)
    {
        var claimsJson = string.Join(",", claims.Select(c => $"{{\"typ\":\"{c.Type}\",\"val\":\"{c.Value}\"}}"));
        var json = $"{{\"auth_typ\":\"aad\",\"claims\":[{claimsJson}]}}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void X_Dev_User_headers_are_ignored_outside_Development()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Dev-User-Email"] = "dev@example.com";
        var context = CreateContext(httpContext, isDevelopment: false);

        Assert.Null(context.TryGetCurrentUser());
    }

    [Fact]
    public void X_Dev_User_headers_still_work_in_Development()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Dev-User-Email"] = "dev@example.com";
        var context = CreateContext(httpContext, isDevelopment: true);

        var user = context.TryGetCurrentUser();

        Assert.NotNull(user);
        Assert.Equal("dev@example.com", user!.UserEmail);
    }

    [Fact]
    public void Token_claims_win_over_X_User_Email_header()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Email"] = "header@example.com";
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("preferred_username", "token@example.com") },
                authenticationType: "TestAuth"));
        var context = CreateContext(httpContext, isDevelopment: false);

        var user = context.TryGetCurrentUser();

        Assert.NotNull(user);
        Assert.Equal("token@example.com", user!.UserEmail);
    }

    [Fact]
    public void No_identity_source_returns_null()
    {
        var httpContext = new DefaultHttpContext();
        var context = CreateContext(httpContext, isDevelopment: false);

        Assert.Null(context.TryGetCurrentUser());
    }
}
