using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UsageTracker.Daemon.Mcp;

/// <summary>
/// Project-context MCP tools, hosted by the daemon over its loopback HTTP endpoint (see Program.cs,
/// <c>MapMcp("/mcp")</c>). These are thin wrappers over <see cref="IProjectContextService"/> - the same
/// host-agnostic runtime service behind the Function App's HTTP endpoints - so all logic stays in
/// UsageTracker.Library. Identity is the daemon's <c>DaemonUserContext</c> (Entra token, or the local
/// OS-user dev fallback), so tools attribute to the real signed-in user with no HttpContext needed.
///
/// Tool names are kept stable (<c>usage_*</c>) so existing IDE MCP configurations keep working after the
/// move off the Function App's McpToolTrigger binding.
/// </summary>
[McpServerToolType]
public sealed class ProjectContextMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IProjectContextService _context;

    public ProjectContextMcpTools(IProjectContextService context) => _context = context;

    [McpServerTool(Name = "usage_set_project_context", Title = "Set Project Context")]
    [Description("Set the active project for a session/platform so agent usage is attributed to it.")]
    public async Task<string> SetProjectContext(
        [Description("Stable project key/id, e.g. 'wealthspire-ticketing'.")] string projectKey,
        [Description("Human-readable project name.")] string projectName,
        [Description("Agent session id, if known.")] string? sessionId = null,
        [Description("Platform: claude-code, github-copilot, or cursor.")] string? platform = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ProjectContextRequest(projectKey, projectName, platform, sessionId, null, null);
        var result = await _context.SetAsync(request, cancellationToken);
        return MapResult(result, r => JsonSerializer.Serialize(r.Window, JsonOptions));
    }

    [McpServerTool(Name = "usage_get_project_context", Title = "Get Project Context")]
    [Description("Read the caller's currently active project context window(s).")]
    public async Task<string> GetProjectContext(CancellationToken cancellationToken = default)
    {
        var active = await _context.GetActiveAsync(cancellationToken);
        return JsonSerializer.Serialize(active, JsonOptions);
    }

    [McpServerTool(Name = "usage_clear_project_context", Title = "Clear Project Context")]
    [Description("Close the active project context window for the given project key.")]
    public async Task<string> ClearProjectContext(
        [Description("Project key to clear.")] string projectKey,
        CancellationToken cancellationToken = default)
    {
        var result = await _context.ClearAsync(projectKey, cancellationToken);
        return MapResult(result, _ => "{\"status\":\"cleared\"}");
    }

    [McpServerTool(Name = "usage_list_recent_projects", Title = "List Recent Projects")]
    [Description("List the caller's recent project context windows.")]
    public async Task<string> ListRecentProjects(
        [Description("Maximum number of results (default 20).")] int? limit = null,
        CancellationToken cancellationToken = default)
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
