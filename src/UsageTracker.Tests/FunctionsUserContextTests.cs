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
    public void X_User_Email_header_resolves_identity_outside_Development()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-User-Email"] = "user@example.com";
        var context = CreateContext(httpContext, isDevelopment: false);

        var user = context.TryGetCurrentUser();

        Assert.NotNull(user);
        Assert.Equal("user@example.com", user!.UserEmail);
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
