using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace UsageTracker.Functions;

/// <summary>
/// HTTP boundary for project-context management, called by the MCP server (and admin tooling).
/// Set/clear/read/list; all behavior lives in <see cref="IProjectContextService"/>.
/// </summary>
public sealed class ProjectContextFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IProjectContextService _context;

    public ProjectContextFunctions(IProjectContextService context) => _context = context;

    [Function("SetProjectContext")]
    public async Task<IActionResult> Set(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "context/project")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        ProjectContextRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ProjectContextRequest>(req.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult(new { error = "Request body must be valid JSON." });
        }

        if (request is null)
            return new BadRequestObjectResult(new { error = "Request body is required." });

        var result = await _context.SetAsync(request, cancellationToken);
        return Map(result, r => new OkObjectResult(r.Window));
    }

    [Function("ClearProjectContext")]
    public async Task<IActionResult> Clear(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "context/project")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var projectKey = req.Query["projectKey"].FirstOrDefault() ?? string.Empty;
        var result = await _context.ClearAsync(projectKey, cancellationToken);
        return Map(result, _ => new NoContentResult());
    }

    [Function("GetProjectContext")]
    public async Task<IActionResult> GetActive(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "context/project")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var active = await _context.GetActiveAsync(cancellationToken);
        return new OkObjectResult(active);
    }

    [Function("ListRecentProjects")]
    public async Task<IActionResult> ListRecent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "context/project/recent")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        var limit = int.TryParse(req.Query["limit"].FirstOrDefault(), out var parsed) && parsed > 0 ? parsed : 20;
        var recent = await _context.ListRecentAsync(limit, cancellationToken);
        return new OkObjectResult(recent);
    }

    private static IActionResult Map(ProjectContextResult result, Func<ProjectContextResult, IActionResult> onSuccess)
    {
        if (result.Unauthorized)
            return new UnauthorizedObjectResult(new { error = "User identity is required." });
        if (result.BadRequest is not null)
            return new BadRequestObjectResult(new { error = result.BadRequest });
        return onSuccess(result);
    }
}
