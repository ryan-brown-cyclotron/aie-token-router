using System.Text;
using System.Text.Json;
using UsageTracker.Contracts;
using UsageTracker.Daemon.Auth;
using UsageTracker.Daemon.Configuration;

namespace UsageTracker.Daemon;

/// <summary>
/// The daemon's single request handler. For every CLI call it: runs the local UsageTracker.Library
/// ingestion pipeline (local transcript reading + tool-output compaction, producing the hook response
/// including any <c>modifiedResult</c>), and, when a remote endpoint is configured, mirrors the raw
/// payload to the backend with the Entra Bearer token for durable, user-attributed storage. Everything
/// is fail-open: a hook must never be blocked, so any failure returns an empty successful response.
/// </summary>
public sealed class CommandProcessor
{
    public const string BackendClientName = "backend";

    private readonly IHookIngestionService _ingestion;
    private readonly EntraTokenService _tokenService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DaemonConfigStore _configStore;
    private readonly ILogger<CommandProcessor> _logger;

    public CommandProcessor(
        IHookIngestionService ingestion,
        EntraTokenService tokenService,
        IHttpClientFactory httpClientFactory,
        DaemonConfigStore configStore,
        ILogger<CommandProcessor> logger)
    {
        _ingestion = ingestion;
        _tokenService = tokenService;
        _httpClientFactory = httpClientFactory;
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<CommandResponse> ProcessAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var platform = envelope.Name;
        var trace = envelope.Trace || string.Equals(envelope.Kind, CommandEnvelope.KindTrace, StringComparison.Ordinal);
        var diagnostics = trace ? new StringBuilder() : null;

        Trace(diagnostics, $"command: {envelope.Kind} name: {platform}");
        Trace(diagnostics, $"user: {_tokenService.CurrentIdentity?.UserEmail ?? "(none)"}");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(envelope.Stdin) ? "{}" : envelope.Stdin);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed stdin payload for platform {Platform}; ingesting empty", platform);
            Trace(diagnostics, "stdin: malformed JSON — ingested as empty");
            return CommandResponse.Empty(diagnostics?.ToString());
        }

        object responsePayload;
        try
        {
            var result = await _ingestion.IngestAsync(platform, root, cancellationToken);
            responsePayload = result.ResponsePayload;
            Trace(diagnostics, "compaction: local ingestion applied");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Local ingestion failed for platform {Platform}; failing open", platform);
            Trace(diagnostics, $"ingestion: failed ({ex.GetType().Name}) — failing open");
            responsePayload = new { };
        }

        await MirrorToBackendAsync(platform, envelope.Stdin, diagnostics, cancellationToken);

        var stdout = JsonSerializer.Serialize(responsePayload);
        return new CommandResponse(0, stdout, diagnostics?.ToString());
    }

    private async Task MirrorToBackendAsync(string platform, string? payload, StringBuilder? diagnostics, CancellationToken cancellationToken)
    {
        var config = _configStore.Load();
        if (string.IsNullOrWhiteSpace(config.RemoteEndpoint))
        {
            Trace(diagnostics, "backend: not configured (local-only)");
            return;
        }

        Trace(diagnostics, $"remote: {config.RemoteEndpoint}");
        if (!Uri.TryCreate(config.RemoteEndpoint, UriKind.Absolute, out var baseUri))
        {
            _logger.LogWarning("RemoteEndpoint '{Remote}' is not an absolute URI; skipping backend mirror", config.RemoteEndpoint);
            Trace(diagnostics, "backend: invalid remote endpoint");
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(BackendClientName);
            var requestUri = new Uri(baseUri, $"api/hooks/{platform}");
            var content = new StringContent(string.IsNullOrWhiteSpace(payload) ? "{}" : payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(requestUri, content, cancellationToken);
            Trace(diagnostics, $"backend: {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("Backend mirror for {Platform} returned {Status}", platform, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: local ingestion already succeeded and the hook must not block on the network.
            _logger.LogWarning(ex, "Backend mirror for {Platform} failed", platform);
            Trace(diagnostics, $"backend: error ({ex.GetType().Name})");
        }
    }

    private static void Trace(StringBuilder? diagnostics, string line) => diagnostics?.AppendLine($"[trace] {line}");
}
