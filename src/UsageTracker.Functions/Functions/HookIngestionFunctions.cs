using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace UsageTracker.Functions;

/// <summary>
/// Thin HTTP boundary for hook ingestion. Platform is taken from the route, never the payload.
/// The trigger only parses the body and delegates to <see cref="IHookIngestionService"/>; all
/// normalization, attribution, and storage live in the library. Always fail-open (200).
/// </summary>
public sealed class HookIngestionFunctions
{
    private readonly IHookIngestionService _hooks;

    public HookIngestionFunctions(IHookIngestionService hooks) => _hooks = hooks;

    [Function("IngestHook")]
    public async Task<IActionResult> Ingest(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "hooks/{platform}")] HttpRequest req,
        string platform,
        CancellationToken cancellationToken)
    {
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // Malformed body is non-blocking: never blow up the agent's turn over it.
            return new OkObjectResult(new { });
        }

        using (doc)
        {
            var result = await _hooks.IngestAsync(platform, doc.RootElement, cancellationToken);
            return new ObjectResult(result.ResponsePayload) { StatusCode = result.StatusCode };
        }
    }
}
