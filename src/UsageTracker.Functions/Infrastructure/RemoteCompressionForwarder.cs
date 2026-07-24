using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace UsageTracker.Functions;

/// <summary>
/// Backend-side compression forwarder for the daemon's <c>remote</c> mode. If a Headroom endpoint is
/// configured (<c>CompressionEndpoint</c>), it POSTs the tool output — wrapped as a single user
/// message — to the generic context-optimization service, maps the result back to a single string,
/// and logs telemetry. If no endpoint is configured, or the call fails, it falls back to the locally
/// registered <see cref="IToolOutputCompressor"/>. Always fail-open per the compressor contract.
/// </summary>
public sealed class RemoteCompressionForwarder
{
    public const string HttpClientName = "headroom";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IToolOutputCompressor? _fallback;
    private readonly UsageTrackerMetrics _metrics;
    private readonly ILogger<RemoteCompressionForwarder> _logger;

    public RemoteCompressionForwarder(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        UsageTrackerMetrics metrics,
        ILogger<RemoteCompressionForwarder> logger,
        IToolOutputCompressor? fallback = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _metrics = metrics;
        _logger = logger;
        _fallback = fallback;
    }

    public async Task<ToolOutputCompression> CompressAsync(string toolOutput, string? model, CancellationToken cancellationToken = default)
    {
        var endpoint = _configuration["CompressionEndpoint"];

        // No Headroom endpoint referenced → use whatever local compressor this host has (deterministic
        // by default). This is the "Function uses Headroom only if the endpoint is referenced" behavior.
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
            return await CompressLocallyAsync(toolOutput, model, cancellationToken);

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var payload = new HeadroomRequest([new HeadroomMessage("user", toolOutput)], model);

            using var response = await client.PostAsJsonAsync(new Uri(baseUri, "compress"), payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<HeadroomResponse>(cancellationToken: cancellationToken);
            var compressedText = result?.Messages?.FirstOrDefault()?.Content;

            if (result is null || string.IsNullOrEmpty(compressedText))
                return ToolOutputCompression.Unchanged(toolOutput);

            // Prefer Headroom's real before/after counts; fall back to the reported savings (as
            // before→0) so ToolOutputCompression.TokensSaved (feeding the metrics histogram) is still
            // populated if a build of the service only returns tokens_saved.
            var tokensBefore = result.TokensBefore ?? result.TokensSaved ?? 0;
            var tokensAfter = result.TokensAfter ?? 0;
            var compression = new ToolOutputCompression(Compressed: true, Output: compressedText, TokensBefore: tokensBefore, TokensAfter: tokensAfter);

            _metrics.RecordCompression("remote", model ?? "unknown", "backend", compression);
            return compression;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Fail-open: a compression outage must never break the caller's turn.
            _logger.LogWarning(ex, "Headroom compression forward failed; falling back to local compression.");
            return await CompressLocallyAsync(toolOutput, model, cancellationToken);
        }
    }

    private async Task<ToolOutputCompression> CompressLocallyAsync(string toolOutput, string? model, CancellationToken cancellationToken)
    {
        if (_fallback is null)
            return ToolOutputCompression.Unchanged(toolOutput);

        return await _fallback.CompressAsync(toolOutput, model, cancellationToken);
    }

    private sealed record HeadroomRequest(
        [property: JsonPropertyName("messages")] IReadOnlyList<HeadroomMessage> Messages,
        [property: JsonPropertyName("model")] string? Model);

    private sealed record HeadroomMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record HeadroomResponse(
        [property: JsonPropertyName("messages")] IReadOnlyList<HeadroomMessage>? Messages,
        [property: JsonPropertyName("tokens_saved")] long? TokensSaved,
        [property: JsonPropertyName("tokens_before")] long? TokensBefore,
        [property: JsonPropertyName("tokens_after")] long? TokensAfter,
        [property: JsonPropertyName("compression_ratio")] double? CompressionRatio);
}
