using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using UsageTracker.Functions.Dashboard.Pages;

namespace UsageTracker.Functions;

/// <summary>
/// Read-only admin dashboard rendered server-side inside the Function App itself, via
/// <see cref="HtmlRenderer"/> (non-interactive Razor-components-to-HTML rendering - see
/// https://learn.microsoft.com/aspnet/core/blazor/components/render-components-outside-of-aspnetcore).
/// This is not Blazor WASM or Blazor Server: there's no client runtime, no SignalR circuit, no
/// @@onclick handlers - each request renders a fresh, static HTML snapshot by calling
/// <see cref="IDashboardQueryService"/> directly (in-process, no HTTP round-trip). Replaces the
/// former standalone UsageTracker.Dashboard Blazor WASM project, which required its own hosting
/// resource; the dashboard is read-only, so it never needed WASM's client-side interactivity.
/// </summary>
public sealed class DashboardFunctions
{
    private readonly IServiceProvider _services;
    private readonly ILoggerFactory _loggerFactory;

    public DashboardFunctions(IServiceProvider services, ILoggerFactory loggerFactory)
    {
        _services = services;
        _loggerFactory = loggerFactory;
    }

    [Function("Dashboard")]
    public Task<IActionResult> Dashboard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")] HttpRequest req) =>
        RenderPage<DashboardPage>();

    /// <summary>
    /// Renders <typeparamref name="TComponent"/> to an HTML string using the current invocation's
    /// scoped <see cref="IServiceProvider"/> (so @@inject IDashboardQueryService etc. resolve the
    /// same per-request scoped instances the rest of the Function App uses), and returns it as the
    /// HTTP response body. Per the HtmlRenderer docs, RenderComponentAsync must run through the
    /// renderer's own dispatcher.
    /// </summary>
    private async Task<IActionResult> RenderPage<TComponent>() where TComponent : IComponent
    {
        await using var renderer = new HtmlRenderer(_services, _loggerFactory);

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
            return output.ToHtmlString();
        });

        return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = 200 };
    }
}
