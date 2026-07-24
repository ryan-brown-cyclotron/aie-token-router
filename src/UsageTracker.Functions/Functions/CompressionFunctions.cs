using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using UsageTracker.Contracts;

namespace UsageTracker.Functions;

/// <summary>
/// Thin HTTP boundary for the daemon's <c>remote</c> compression mode. Accepts a single tool output,
/// delegates to <see cref="RemoteCompressionForwarder"/> (which forwards to Headroom when configured,
/// else compresses locally), and returns the compressed text plus token telemetry. Always fail-open.
/// </summary>
public sealed class CompressionFunctions
{
    private readonly RemoteCompressionForwarder _forwarder;

    public CompressionFunctions(RemoteCompressionForwarder forwarder) => _forwarder = forwarder;

    [Function("CompressToolOutput")]
    public async Task<IActionResult> Compress(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "compress")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        CompressRequest? request;
        try
        {
            request = await System.Text.Json.JsonSerializer.DeserializeAsync(
                req.Body, ContractsJsonContext.Default.CompressRequest, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return new BadRequestObjectResult(new { error = "invalid or missing JSON body" });
        }

        if (request is null || string.IsNullOrEmpty(request.ToolOutput))
            return new BadRequestObjectResult(new { error = "'toolOutput' is required" });

        var result = await _forwarder.CompressAsync(request.ToolOutput, request.Model, cancellationToken);

        var response = new CompressResponse(result.Compressed, result.Output, result.TokensBefore, result.TokensAfter);
        return new OkObjectResult(response);
    }
}
