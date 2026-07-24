using System.Net.Http.Json;
using System.Text.Json;
using UsageTracker.Contracts;
using UsageTracker.Daemon.Configuration;

namespace UsageTracker.Daemon.Compression;

/// <summary>
/// <c>remote</c>-mode <see cref="IToolOutputCompressor"/>: instead of compressing locally, forwards
/// the tool output to the backend Functions app (reusing the Entra-authenticated
/// <see cref="CommandProcessor.BackendClientName"/> client and the configured
/// <see cref="DaemonConfig.RemoteEndpoint"/>). The backend optionally forwards to the Headroom service
/// and logs metrics. Always fail-open per the interface contract: any error, timeout, or missing
/// endpoint returns <see cref="ToolOutputCompression.Unchanged"/>.
/// </summary>
public sealed class RemoteToolOutputCompressor : IToolOutputCompressor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DaemonConfigStore _configStore;
    private readonly ILogger<RemoteToolOutputCompressor> _logger;

    public RemoteToolOutputCompressor(
        IHttpClientFactory httpClientFactory,
        DaemonConfigStore configStore,
        ILogger<RemoteToolOutputCompressor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<ToolOutputCompression> CompressAsync(string toolOutput, string? model, CancellationToken cancellationToken = default)
    {
        // Base address is resolved per-call from current config (so `set-remote` takes effect without a
        // daemon restart), mirroring CommandProcessor.MirrorToBackendAsync.
        var config = _configStore.Load();
        if (string.IsNullOrWhiteSpace(config.RemoteEndpoint) || !Uri.TryCreate(config.RemoteEndpoint, UriKind.Absolute, out var baseUri))
        {
            _logger.LogWarning("Remote compression requested but RemoteEndpoint is not configured; returning output unchanged.");
            return ToolOutputCompression.Unchanged(toolOutput);
        }

        try
        {
            var client = _httpClientFactory.CreateClient(CommandProcessor.BackendClientName);
            var requestUri = new Uri(baseUri, "api/compress");
            var request = new CompressRequest(toolOutput, model);

            using var response = await client.PostAsJsonAsync(requestUri, request, ContractsJsonContext.Default.CompressRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Remote compression returned {Status}; returning output unchanged.", (int)response.StatusCode);
                return ToolOutputCompression.Unchanged(toolOutput);
            }

            var result = await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.CompressResponse, cancellationToken);
            if (result is null || !result.Compressed || string.IsNullOrEmpty(result.Text))
                return ToolOutputCompression.Unchanged(toolOutput);

            return new ToolOutputCompression(true, result.Text, result.TokensBefore, result.TokensAfter);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Fail-open: remote compression must never block the agent's turn.
            _logger.LogWarning(ex, "Remote compression call failed; returning output unchanged.");
            return ToolOutputCompression.Unchanged(toolOutput);
        }
    }
}
