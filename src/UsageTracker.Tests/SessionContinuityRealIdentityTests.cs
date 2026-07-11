using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UsageTracker.Functions;

namespace UsageTracker.Tests;

/// <summary>
/// Same continuity story as <see cref="SessionContinuityTests"/>, but driven by the real
/// <see cref="FunctionsUserContext"/> instead of a fake, under the assumptions this system
/// actually runs under: hook traffic carries the caller's verified identity via the Container Apps
/// Easy Auth principal header (the daemon presents an Entra token; Easy Auth injects the principal),
/// and project-context-setting calls are identified via token claims. Proves both identity paths
/// resolve to the same UserKey and the continuity/backfill logic doesn't care which one produced it.
/// </summary>
public class SessionContinuityRealIdentityTests
{
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static JsonElement Payload(string sessionId, string eventName, long inputTokens, long outputTokens) =>
        JsonDocument.Parse(
            $"{{\"session_id\":\"{sessionId}\",\"hook_event_name\":\"{eventName}\",\"model\":\"claude-opus-4-8\",\"usage\":{{\"input_tokens\":{inputTokens},\"output_tokens\":{outputTokens}}}}}"
        ).RootElement;

    [Fact]
    public async Task Hook_traffic_identified_via_header_and_context_set_via_token_claims_still_organize_by_project()
    {
        var repository = new InMemoryUsageRepository();
        var store = new UsageStore();
        var attribution = new ProjectAttributionService(repository);
        var transcripts = new TranscriptTokenReader(NullLogger<TranscriptTokenReader>.Instance);
        var compression = new ToolOutputCompressionService(Options.Create(new ToolOutputCompressionOptions()));
        var metrics = new UsageTrackerMetrics();

        var holder = new HttpContextHolder();
        var environment = new FakeHostEnvironment(); // Production - the dev-only fallbacks must not be what's carrying this
        var userContext = new FunctionsUserContext(holder, environment, new ConfigurationBuilder().Build());

        var hooks = new HookIngestionService(
            store, repository, attribution, transcripts, compression, userContext, metrics, NullLogger<HookIngestionService>.Instance);
        var contextService = new ProjectContextService(repository, userContext, store, NullLogger<ProjectContextService>.Instance);
        var dashboard = new DashboardQueryService(store, repository, NullLogger<DashboardQueryService>.Instance);

        const string sessionId = "session-real-identity-1";
        const string userEmail = "user@example.com";

        // Hook traffic arrives with the caller identified via the Easy Auth principal - the verified
        // mechanism every hook now uses through the daemon (see docs/design/daemon-cli.md).
        holder.Current = HttpContextWithEasyAuth(userEmail);
        await hooks.IngestAsync("claude-code", Payload(sessionId, "PreToolUse", 10, 0));
        await hooks.IngestAsync("claude-code", Payload(sessionId, "PostToolUse", 20, 5));

        var beforeContext = await dashboard.UsageAsync(null, null);
        Assert.All(beforeContext, row => Assert.Equal("unknown", row.ProjectKey));

        // Setting project context arrives identified via token claims instead - standing in for the
        // MCP transport once it has real auth, rather than the Mcp:DefaultUserEmail dev-only
        // fallback this same class uses today for the no-HttpContext MCP path.
        holder.Current = HttpContextWithClaims(userEmail);
        var setResult = await contextService.SetAsync(new ProjectContextRequest("proj", "My Project", "claude-code", sessionId, null, null));
        Assert.False(setResult.Unauthorized);
        Assert.Equal(userEmail, setResult.Window!.User);

        // More hook traffic, again identified via Easy Auth, continues on the same session.
        holder.Current = HttpContextWithEasyAuth(userEmail);
        await hooks.IngestAsync("claude-code", Payload(sessionId, "PostToolUse", 30, 10));

        // Both the Easy-Auth-identified and claims-identified calls resolved to the same UserKey, so
        // the whole window - backfilled and live-attributed alike - lands under one project.
        var projects = await dashboard.ProjectsAsync(null, null);
        var projectRow = Assert.Single(projects, p => p.ProjectKey == "proj");
        Assert.Equal(60, projectRow.InputTokens); // 10 + 20 + 30

        var usage = await dashboard.UsageAsync(null, null);
        Assert.DoesNotContain(usage, row => row.ProjectKey == "unknown");
    }

    private static HttpContext HttpContextWithEasyAuth(string email)
    {
        var json = $"{{\"auth_typ\":\"aad\",\"claims\":[{{\"typ\":\"preferred_username\",\"val\":\"{email}\"}}]}}";
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        return httpContext;
    }

    private static HttpContext HttpContextWithClaims(string email)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("preferred_username", email) },
            authenticationType: "TestAuth"));
        return httpContext;
    }
}
