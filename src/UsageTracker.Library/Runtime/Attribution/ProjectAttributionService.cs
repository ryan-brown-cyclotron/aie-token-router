namespace UsageTracker;

public interface IProjectAttributionService
{
    Task<ProjectAttribution> ResolveAsync(HookEvent evt, DateTimeOffset at, CancellationToken cancellationToken = default);
}

public sealed class ProjectAttributionService : IProjectAttributionService
{
    private readonly IUsageRepository _repository;

    public ProjectAttributionService(IUsageRepository repository) => _repository = repository;

    public async Task<ProjectAttribution> ResolveAsync(HookEvent evt, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        if (evt.UserKey == "unknown") return ProjectAttribution.Unknown;

        var contexts = await _repository.ActiveProjectContextsAsync(evt.UserKey, at, cancellationToken);
        if (contexts.Count == 0) return ProjectAttribution.Unknown;

        var sessionMatches = contexts
            .Where(context => PlatformMatches(context.Platform, evt.Platform) && Matches(context.SessionId, evt.SessionId))
            .ToList();
        if (sessionMatches.Count == 1) return ToAttribution(sessionMatches[0], "session");
        if (sessionMatches.Count > 1) return ProjectAttribution.Ambiguous;

        var workspaceMatches = contexts
            .Where(context => PlatformMatches(context.Platform, evt.Platform) && Matches(context.Cwd, evt.Cwd))
            .ToList();
        if (workspaceMatches.Count == 1) return ToAttribution(workspaceMatches[0], "workspace");
        if (workspaceMatches.Count > 1) return ProjectAttribution.Ambiguous;

        var userWindowMatches = contexts
            .Where(context => string.IsNullOrWhiteSpace(context.Platform) || context.Platform.Equals(evt.Platform, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (userWindowMatches.Count == 1) return ToAttribution(userWindowMatches[0], "user-window");

        return userWindowMatches.Count > 1 ? ProjectAttribution.Ambiguous : ProjectAttribution.Unknown;
    }

    private static ProjectAttribution ToAttribution(ProjectContextWindow context, string confidence) =>
        new(context.ProjectKey, context.ProjectName, confidence);

    private static bool PlatformMatches(string? expected, string actual) =>
        string.IsNullOrWhiteSpace(expected) || expected.Equals(actual, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(expected) &&
        !string.IsNullOrWhiteSpace(actual) &&
        expected.Equals(actual, StringComparison.OrdinalIgnoreCase);
}
