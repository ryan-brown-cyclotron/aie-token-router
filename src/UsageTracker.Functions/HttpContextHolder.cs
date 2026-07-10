using Microsoft.AspNetCore.Http;

namespace UsageTracker.Functions;

/// <summary>
/// Per-invocation holder for the current <see cref="HttpContext"/>. In the isolated worker,
/// <c>IHttpContextAccessor</c>'s AsyncLocal is not reliably populated for function invocations,
/// so a worker middleware captures the context (via <c>FunctionContext.GetHttpContext()</c>) into
/// this scoped holder and <see cref="FunctionsUserContext"/> reads it from here.
/// </summary>
public sealed class HttpContextHolder
{
    public HttpContext? Current { get; set; }
}
