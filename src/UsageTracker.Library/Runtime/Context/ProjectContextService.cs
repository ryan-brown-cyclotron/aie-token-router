namespace UsageTracker;

/// <summary>
/// Runtime service behind the project-context Function endpoints and the MCP tools.
/// Owns the set/clear/read/list behavior formerly in ProjectContextController, expressed
/// as a result object so the HTTP boundary just maps status codes.
/// </summary>
public interface IProjectContextService
{
    Task<ProjectContextResult> SetAsync(ProjectContextRequest request, CancellationToken cancellationToken = default);
    Task<ProjectContextResult> ClearAsync(string projectKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ProjectContextWindow>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ProjectContextWindow>> ListRecentAsync(int limit = 20, CancellationToken cancellationToken = default);
}

/// <summary>
/// Discriminated outcome for context writes. <see cref="Unauthorized"/> => no caller identity;
/// <see cref="BadRequest"/> => missing required fields; otherwise success with the resulting window
/// (null for clear, which is a no-content success).
/// </summary>
public sealed record ProjectContextResult(bool Unauthorized, string? BadRequest, ProjectContextWindow? Window)
{
    public static ProjectContextResult NoIdentity { get; } = new(true, null, null);
    public static ProjectContextResult Invalid(string message) => new(false, message, null);
    public static ProjectContextResult Success(ProjectContextWindow? window) => new(false, null, window);
}

public sealed class ProjectContextService : IProjectContextService
{
    private const int DefaultExpiryMinutes = 240;
    private static readonly TimeSpan BestEffortTimeout = TimeSpan.FromSeconds(2);

    private readonly IUsageRepository _repository;
    private readonly IUserContext _userContext;
    private readonly UsageStore _store;
    private readonly ILogger<ProjectContextService> _logger;

    public ProjectContextService(IUsageRepository repository, IUserContext userContext, UsageStore store, ILogger<ProjectContextService> logger)
    {
        _repository = repository;
        _userContext = userContext;
        _store = store;
        _logger = logger;
    }

    public async Task<ProjectContextResult> SetAsync(ProjectContextRequest request, CancellationToken cancellationToken = default)
    {
        var user = _userContext.TryGetCurrentUser();
        if (user is null) return ProjectContextResult.NoIdentity;

        if (string.IsNullOrWhiteSpace(request.ProjectKey) || string.IsNullOrWhiteSpace(request.ProjectName))
            return ProjectContextResult.Invalid("projectKey and projectName are required.");

        var now = DateTimeOffset.UtcNow;
        var context = new ProjectContextWindow(
            Id: Guid.NewGuid().ToString("N"),
            User: user.UserKey,
            ProjectKey: request.ProjectKey.Trim(),
            ProjectName: request.ProjectName.Trim(),
            Platform: Normalize(request.Platform),
            SessionId: Normalize(request.SessionId),
            Cwd: Normalize(request.Cwd),
            Source: "manual",
            StartedAt: now,
            ExpiresAt: now.AddMinutes(request.ExpiresInMinutes is > 0 ? request.ExpiresInMinutes.Value : DefaultExpiryMinutes),
            EndedAt: null);

        await _repository.UpsertProjectContextAsync(context, cancellationToken);

        if (context.SessionId is not null)
            await TryBackfillSessionAttribution(context, cancellationToken);

        return ProjectContextResult.Success(context);
    }

    /// <summary>
    /// Retroactively fixes already-ingested events for this session that predate the context
    /// window (see <see cref="IUsageRepository.BackfillSessionAttributionAsync"/>). Fail-open,
    /// same convention as <c>HookIngestionService.TryResolveAttribution</c>/<c>TryRecordMetric</c>:
    /// a backfill failure never blocks the SetAsync response.
    /// </summary>
    private async Task TryBackfillSessionAttribution(ProjectContextWindow context, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BestEffortTimeout);

            var attribution = new ProjectAttribution(context.ProjectKey, context.ProjectName, "session-backfill");
            var changed = await _repository
                .BackfillSessionAttributionAsync(context.User, context.SessionId!, attribution, timeout.Token)
                .WaitAsync(timeout.Token);

            if (changed > 0)
                _store.ForceSessionAttribution(context.SessionId!, attribution);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not backfill session attribution for session={Session}", context.SessionId);
        }
    }

    public async Task<ProjectContextResult> ClearAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        var user = _userContext.TryGetCurrentUser();
        if (user is null) return ProjectContextResult.NoIdentity;
        if (string.IsNullOrWhiteSpace(projectKey)) return ProjectContextResult.Invalid("projectKey is required.");

        await _repository.EndActiveProjectContextAsync(user.UserKey, projectKey.Trim(), DateTimeOffset.UtcNow, cancellationToken);
        return ProjectContextResult.Success(null);
    }

    public async Task<IReadOnlyCollection<ProjectContextWindow>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var user = _userContext.TryGetCurrentUser();
        if (user is null) return Array.Empty<ProjectContextWindow>();

        return await _repository.ActiveProjectContextsAsync(user.UserKey, DateTimeOffset.UtcNow, cancellationToken);
    }

    public async Task<IReadOnlyCollection<ProjectContextWindow>> ListRecentAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        var user = _userContext.TryGetCurrentUser();
        if (user is null) return Array.Empty<ProjectContextWindow>();

        return await _repository.RecentProjectContextsAsync(user.UserKey, limit, cancellationToken);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
