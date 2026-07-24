using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;
using UsageTracker.Functions.Dashboard.Pages;
using UsageTracker.Functions.Infrastructure;

namespace UsageTracker.Tests;

/// <summary>
/// Exercises the actual HtmlRenderer rendering path for the dashboard page (see
/// DashboardFunctions.cs) - the riskiest, previously-unverified part of replacing the Blazor WASM
/// Dashboard with server-side, non-interactive Razor-component-to-HTML rendering.
/// </summary>
public class DashboardPageTests
{
    private sealed class FakeDashboardQueryService : IDashboardQueryService
    {
        public IReadOnlyCollection<SessionView> SessionsResult { get; set; } = Array.Empty<SessionView>();
        public IReadOnlyCollection<UsageSummaryRow> UsageResult { get; set; } = Array.Empty<UsageSummaryRow>();
        public IReadOnlyCollection<ProjectUsageRow> ProjectsResult { get; set; } = Array.Empty<ProjectUsageRow>();

        public IReadOnlyCollection<SessionView> Sessions() => SessionsResult;
        public Task<IReadOnlyCollection<UsageSummaryRow>> UsageAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default) =>
            Task.FromResult(UsageResult);
        public Task<IReadOnlyCollection<ProjectUsageRow>> ProjectsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProjectsResult);
        public Task<NormalizedUsageEvent?> GetEventAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<NormalizedUsageEvent?>(null);
    }

    private static async Task<string> RenderAsync<TComponent>(FakeDashboardQueryService dashboard) where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDashboardQueryService>(dashboard);
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoOpJsRuntime>();
        await using var provider = services.BuildServiceProvider();

        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
            return output.ToHtmlString();
        });
    }

    [Fact]
    public async Task DashboardPage_renders_totals_from_dashboard_query_service()
    {
        var dashboard = new FakeDashboardQueryService
        {
            SessionsResult = new[] { MakeSession("s1") },
            ProjectsResult = new[] { new ProjectUsageRow("proj", "Project", 1, 3, 100, 50, 0, 0) }
        };

        var html = await RenderAsync<DashboardPage>(dashboard);

        Assert.Contains("UsageTracker", html);
        Assert.Contains("<div class=\"ut-kpi-value\">1</div>", html); // session count
        Assert.Contains("150", html); // total tokens (100 + 50)
    }

    [Fact]
    public async Task DashboardPage_tabs_are_css_only_with_no_onclick_handlers()
    {
        // This page renders via a non-interactive HtmlRenderer (see DashboardFunctions.cs) - there's
        // no circuit, so @onclick handlers never fire in the browser. Guards against reintroducing
        // click-driven tab state that silently does nothing for users. (MudChart brings its own
        // inert internal event markup, so this checks the tab bar specifically rather than the
        // whole document.)
        var html = await RenderAsync<DashboardPage>(new FakeDashboardQueryService());

        var tabsStart = html.IndexOf("<nav class=\"ut-tabs\"", StringComparison.Ordinal);
        var tabsEnd = html.IndexOf("</nav>", tabsStart, StringComparison.Ordinal);
        Assert.True(tabsStart >= 0 && tabsEnd > tabsStart, "Expected to find the ut-tabs nav block.");
        var tabsHtml = html[tabsStart..tabsEnd];

        Assert.DoesNotContain("onclick", tabsHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"ut-tab-overview\"", html);
        Assert.Contains("for=\"ut-tab-overview\"", tabsHtml);
        Assert.Contains("data-pane=\"sessions\"", html);
    }

    [Fact]
    public async Task DashboardPage_renders_empty_state_with_no_data()
    {
        var html = await RenderAsync<DashboardPage>(new FakeDashboardQueryService());

        Assert.Contains("No sessions recorded yet.", html);
        Assert.Contains("No project usage recorded yet.", html);
        Assert.Contains("No usage recorded yet.", html);
    }

    [Fact]
    public async Task DashboardPage_renders_a_populated_session_row()
    {
        var dashboard = new FakeDashboardQueryService { SessionsResult = new[] { MakeSession("session-abc") } };

        var html = await RenderAsync<DashboardPage>(dashboard);

        Assert.Contains("session-abc", html);
        Assert.Contains("claude-code", html);
        Assert.Contains("proj", html);
    }

    [Fact]
    public async Task DashboardPage_renders_a_populated_project_row()
    {
        var dashboard = new FakeDashboardQueryService
        {
            ProjectsResult = new[] { new ProjectUsageRow("proj-key", "My Project", 2, 5, 200, 100, 10, 5) }
        };

        var html = await RenderAsync<DashboardPage>(dashboard);

        Assert.Contains("My Project", html);
        Assert.Contains("proj-key", html);
    }

    [Fact]
    public async Task DashboardPage_renders_a_populated_usage_row()
    {
        var dashboard = new FakeDashboardQueryService
        {
            UsageResult = new[] { new UsageSummaryRow("claude-code", "claude-opus-4-8", "user@example.com", "proj", "Project", "session", 1, 2, 3, 100, 50, 0, 0) }
        };

        var html = await RenderAsync<DashboardPage>(dashboard);

        Assert.Contains("claude-opus-4-8", html);
        Assert.Contains("user@example.com", html);
    }

    private static SessionView MakeSession(string sessionId, IReadOnlyList<SessionModelUsage>? models = null) => new(
        SessionId: sessionId,
        Platform: "claude-code",
        User: "user@example.com",
        ProjectKey: "proj",
        ProjectName: "proj",
        AttributionConfidence: "session",
        Model: "claude-opus-4-8",
        StartedAt: DateTimeOffset.UtcNow,
        LastEventAt: DateTimeOffset.UtcNow,
        ToolCalls: 2,
        Usage: new TokenUsage(10, 5, 0, 0, 1),
        Events: new Dictionary<string, int>(),
        Models: models ?? new[] { new SessionModelUsage("claude-opus-4-8", new TokenUsage(10, 5, 0, 0, 1)) });
}
