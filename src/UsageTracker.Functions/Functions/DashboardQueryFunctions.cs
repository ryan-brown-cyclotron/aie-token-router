using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace UsageTracker.Functions;

/// <summary>
/// Read-only dashboard endpoints. The Blazor dashboard is the only intended caller; every handler
/// delegates to <see cref="IDashboardQueryService"/> and returns JSON. No writes here.
/// </summary>
public sealed class DashboardQueryFunctions
{
    private readonly IDashboardQueryService _dashboard;

    public DashboardQueryFunctions(IDashboardQueryService dashboard) => _dashboard = dashboard;

    [Function("DashboardSessions")]
    public IActionResult Sessions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/sessions")] HttpRequest req) =>
        new OkObjectResult(_dashboard.Sessions());

    [Function("DashboardUsage")]
    public async Task<IActionResult> Usage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/usage")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var (from, to) = ReadRange(req);
        return new OkObjectResult(await _dashboard.UsageAsync(from, to, cancellationToken));
    }

    [Function("DashboardProjects")]
    public async Task<IActionResult> Projects(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/projects")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var (from, to) = ReadRange(req);
        return new OkObjectResult(await _dashboard.ProjectsAsync(from, to, cancellationToken));
    }

    [Function("DashboardEvent")]
    public async Task<IActionResult> Event(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/events/{id}")] HttpRequest req,
        string id,
        CancellationToken cancellationToken)
    {
        var evt = await _dashboard.GetEventAsync(id, cancellationToken);
        return evt is null ? new NotFoundResult() : new OkObjectResult(evt);
    }

    private static (DateTimeOffset? From, DateTimeOffset? To) ReadRange(HttpRequest req) =>
        (ParseDate(req.Query["from"].FirstOrDefault()), ParseDate(req.Query["to"].FirstOrDefault()));

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
