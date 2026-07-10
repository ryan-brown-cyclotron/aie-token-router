using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace UsageTracker.Functions;

/// <summary>
/// MCP tool endpoints for project-context management, hosted directly in the Function App via the
/// McpToolTrigger binding (no separate stdio process). Each tool calls
/// <see cref="IProjectContextService"/> directly - the same runtime service behind the HTTP
/// endpoints in <see cref="ProjectContextFunctions"/> - so identity resolution goes through
/// <see cref="FunctionsUserContext"/>'s Mcp:DefaultUser* fallback rather than dev headers, since MCP
/// tool invocations carry no HttpContext.
///
/// Function names carry an "Mcp" suffix because Azure Functions requires unique function names
/// across the whole app - HTTP and MCP triggers share that namespace - and these methods would
/// otherwise collide with the identically-named HTTP functions in <see cref="ProjectContextFunctions"/>.
/// The MCP tool names exposed to clients (McpToolTrigger's first argument) are unaffected.
/// </summary>
public sealed class ProjectContextMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IProjectContextService _context;

    public ProjectContextMcpTools(IProjectContextService context) => _context = context;

    [Function("SetProjectContextMcp")]
    public async Task<string> SetProjectContext(
        [McpToolTrigger("usage_set_project_context", "Set the active project for a session/platform so agent usage is attributed to it.")]
            ToolInvocationContext toolContext,
        [McpToolProperty("projectId", "Stable project key/id, e.g. 'wealthspire-ticketing'.", isRequired: true)] string projectId,
        [McpToolProperty("projectName", "Human-readable project name.", isRequired: true)] string projectName,
        [McpToolProperty("sessionId", "Agent session id, if known.")] string? sessionId,
        [McpToolProperty("platform", "Platform: claude-code, github-copilot, or cursor.")] string? platform,
        CancellationToken cancellationToken)
    {
        var request = new ProjectContextRequest(projectId, projectName, platform, sessionId, null, null);
        var result = await _context.SetAsync(request, cancellationToken);
        return MapResult(result, r => JsonSerializer.Serialize(r.Window, JsonOptions));
    }

    [Function("GetProjectContextMcp")]
    public async Task<string> GetProjectContext(
        [McpToolTrigger("usage_get_project_context", "Read the caller's currently active project context window(s).")]
            ToolInvocationContext toolContext,
        CancellationToken cancellationToken)
    {
        var active = await _context.GetActiveAsync(cancellationToken);
        return JsonSerializer.Serialize(active, JsonOptions);
    }

    [Function("ClearProjectContextMcp")]
    public async Task<string> ClearProjectContext(
        [McpToolTrigger("usage_clear_project_context", "Close the active project context window for the given project key.")]
            ToolInvocationContext toolContext,
        [McpToolProperty("projectKey", "Project key to clear.", isRequired: true)] string projectKey,
        CancellationToken cancellationToken)
    {
        var result = await _context.ClearAsync(projectKey, cancellationToken);
        return MapResult(result, _ => "{\"status\":\"cleared\"}");
    }

    [Function("ListRecentProjectsMcp")]
    public async Task<string> ListRecentProjects(
        [McpToolTrigger("usage_list_recent_projects", "List the caller's recent project context windows.")]
            ToolInvocationContext toolContext,
        [McpToolProperty("limit", "Maximum number of results (default 20).")] int? limit,
        CancellationToken cancellationToken)
    {
        var recent = await _context.ListRecentAsync(limit is > 0 ? limit.Value : 20, cancellationToken);
        return JsonSerializer.Serialize(recent, JsonOptions);
    }

    private static string MapResult(ProjectContextResult result, Func<ProjectContextResult, string> onSuccess)
    {
        if (result.Unauthorized) return "{\"error\":\"User identity is required.\"}";
        if (result.BadRequest is not null) return JsonSerializer.Serialize(new { error = result.BadRequest }, JsonOptions);
        return onSuccess(result);
    }
}
