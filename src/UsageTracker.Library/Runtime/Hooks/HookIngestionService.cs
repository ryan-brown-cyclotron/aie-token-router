using System.Text.Json;

namespace UsageTracker;

/// <summary>
/// Outcome of ingesting a hook payload. Hook ingestion is observational and fail-open,
/// so <see cref="StatusCode"/> is effectively always 200; <see cref="ResponsePayload"/>
/// carries a platform-specific body (e.g. a compressed <c>modifiedResult</c>) when one applies.
/// </summary>
public sealed record HookIngestionResult(int StatusCode, object ResponsePayload)
{
    public static HookIngestionResult Ok(object? payload = null) => new(200, payload ?? new { });
}

public interface IHookIngestionService
{
    Task<HookIngestionResult> IngestAsync(string platform, JsonElement root, CancellationToken cancellationToken = default);
}

/// <summary>
/// Host-agnostic port of the former HooksController.Handle pipeline:
/// normalize -> overlay caller identity -> read token usage -> resolve attribution ->
/// record (in-memory + durable) -> return an observational response. Never throws to the
/// caller: attribution and persistence are best-effort with short timeouts.
/// </summary>
public sealed class HookIngestionService : IHookIngestionService
{
    private static readonly TimeSpan BestEffortTimeout = TimeSpan.FromSeconds(2);

    private readonly UsageStore _store;
    private readonly IUsageRepository _repository;
    private readonly IProjectAttributionService _attribution;
    private readonly TranscriptTokenReader _transcripts;
    private readonly ToolOutputCompressionService _compression;
    private readonly IUserContext _userContext;
    private readonly UsageTrackerMetrics _metrics;
    private readonly ILogger<HookIngestionService> _logger;

    public HookIngestionService(
        UsageStore store,
        IUsageRepository repository,
        IProjectAttributionService attribution,
        TranscriptTokenReader transcripts,
        ToolOutputCompressionService compression,
        IUserContext userContext,
        UsageTrackerMetrics metrics,
        ILogger<HookIngestionService> logger)
    {
        _store = store;
        _repository = repository;
        _attribution = attribution;
        _transcripts = transcripts;
        _compression = compression;
        _userContext = userContext;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<HookIngestionResult> IngestAsync(string platform, JsonElement root, CancellationToken cancellationToken = default)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var evt = HookEvent.FromJson(platform, root);

        // Prefer the authenticated/dev-header user over anything in the payload.
        var httpUser = _userContext.TryGetCurrentUser();
        if (httpUser is not null)
            evt = evt with { UserEmail = httpUser.UserEmail, UserId = httpUser.UserId, UserName = httpUser.UserName };

        var (usage, modelFromTranscript) = evt.TranscriptPath is not null
            ? _transcripts.ReadNewUsage(evt.TranscriptPath)
            : (TokenUsage.FromPayload(root), null);

        var model = evt.Model ?? modelFromTranscript ?? "unknown";
        var attribution = await TryResolveAttribution(evt, receivedAt, cancellationToken);

        // Raw + normalized events are stored BEFORE any compression, and never overwritten.
        _store.RecordEvent(evt, usage, modelFromTranscript, attribution);
        await TryRecordMetric(NormalizedUsageEvent.From(evt, usage, model, attribution, receivedAt), cancellationToken);
        _metrics.RecordTokens(platform, model, evt.UserKey, usage);

        // Optional in-path compression of large tool outputs. Fail-open: any failure leaves the
        // response observational (plain 200, original output).
        var compression = await TryCompress(evt, root, model, cancellationToken);
        if (compression is not null)
            _metrics.RecordCompression(platform, model, evt.UserKey, compression);

        if (compression is { Compressed: true })
        {
            _logger.LogInformation(
                "Compressed {Platform} tool output tokens {Before}->{After} (saved {Saved})",
                platform, compression.TokensBefore, compression.TokensAfter, compression.TokensSaved);
        }

        _logger.LogInformation(
            "{Platform} {Event} session={Session} project={Project} confidence={Confidence} tool={Tool} +tokens(in={In},out={Out})",
            platform, evt.EventName, evt.SessionId, attribution.ProjectKey, attribution.Confidence, evt.ToolName, usage.InputTokens, usage.OutputTokens);
        _metrics.RecordHookEvent(platform, evt.EventName, model, attribution.Confidence, evt.UserKey);

        return HookIngestionResult.Ok(ToolOutputCompressionService.BuildResponse(platform, compression));
    }

    private async Task<ToolOutputCompression?> TryCompress(HookEvent evt, JsonElement root, string? model, CancellationToken cancellationToken)
    {
        try
        {
            return await _compression.TryCompressAsync(evt, root, model, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Tool output compression threw for session={Session}; leaving output unchanged", evt.SessionId);
            return null;
        }
    }

    private async Task<ProjectAttribution> TryResolveAttribution(HookEvent evt, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BestEffortTimeout);
            return await _attribution.ResolveAsync(evt, receivedAt, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not resolve project attribution for session={Session}; using unknown", evt.SessionId);
            return ProjectAttribution.Unknown;
        }
    }

    private async Task TryRecordMetric(NormalizedUsageEvent usageEvent, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BestEffortTimeout);
            await _repository.RecordEventAsync(usageEvent, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not persist normalized usage event for session={Session}", usageEvent.SessionId);
        }
    }
}
