namespace UsageTracker;

/// <summary>
/// Host-agnostic representation of the caller identity resolved for a hook or request.
/// The concrete <see cref="IUserContext"/> implementation is host-specific (e.g. the
/// Function App reads claims / dev headers from the incoming request).
/// </summary>
public sealed record CurrentUser(string UserId, string UserName, string UserEmail)
{
    public string UserKey => !string.IsNullOrWhiteSpace(UserEmail) ? UserEmail : UserId;
}

/// <summary>
/// Resolves the current caller identity for the active request, or <c>null</c> when the
/// request is anonymous. Implemented per host (ASP.NET Core, Azure Functions isolated worker).
/// </summary>
public interface IUserContext
{
    CurrentUser? TryGetCurrentUser();
}
