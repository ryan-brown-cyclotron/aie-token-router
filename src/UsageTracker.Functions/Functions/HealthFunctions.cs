using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace UsageTracker.Functions;

/// <summary>Basic health/status endpoint for the Function App.</summary>
public sealed class HealthFunctions
{
    [Function("Health")]
    public IActionResult Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req) =>
        new OkObjectResult(new { status = "ok", service = "usage-tracker-functions" });
}
