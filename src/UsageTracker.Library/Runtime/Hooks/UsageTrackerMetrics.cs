using System.Diagnostics.Metrics;

namespace UsageTracker;

/// <summary>
/// Custom OpenTelemetry instruments for hook ingestion, alongside the existing structured logs.
/// Tags are kept low-cardinality: <c>project_key</c>/<c>session_id</c> stay log-only (unbounded),
/// while <c>platform</c>/<c>model</c>/<c>attribution_confidence</c>/<c>user</c> are bounded for a
/// small internal team and safe to tag. A host opts in by calling
/// <c>.WithMetrics(m => m.AddMeter(UsageTrackerMetrics.MeterName))</c> - see
/// <c>UsageTracker.Functions/Program.cs</c>.
/// </summary>
public sealed class UsageTrackerMetrics : IDisposable
{
    public const string MeterName = "UsageTracker";

    private readonly Meter _meter;
    private readonly Counter<long> _hookEvents;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _compressionEvents;
    private readonly Histogram<long> _compressionTokensSaved;
    private readonly Counter<long> _compressionTokensSavedTotal;

    public UsageTrackerMetrics()
    {
        _meter = new Meter(MeterName);
        _hookEvents = _meter.CreateCounter<long>("usagetracker.hook.events", unit: "{event}");
        _tokens = _meter.CreateCounter<long>("usagetracker.tokens", unit: "{token}");
        _compressionEvents = _meter.CreateCounter<long>("usagetracker.compression.events", unit: "{event}");
        // Histogram gives the distribution of savings per compressed event; the counter gives a
        // running total so a dashboard can graph cumulative tokens saved over time (the Histogram's
        // own sum isn't surfaced as a separately-queryable series in every backend/dashboard).
        _compressionTokensSaved = _meter.CreateHistogram<long>("usagetracker.compression.tokens_saved", unit: "{token}");
        _compressionTokensSavedTotal = _meter.CreateCounter<long>("usagetracker.compression.tokens_saved_total", unit: "{token}");
    }

    /// <summary>Exposed for tests to filter a <see cref="System.Diagnostics.Metrics.MeterListener"/> to this instance.</summary>
    internal Meter Meter => _meter;

    public void RecordHookEvent(string platform, string eventName, string model, string attributionConfidence, string user)
    {
        _hookEvents.Add(1,
            new KeyValuePair<string, object?>("platform", platform),
            new KeyValuePair<string, object?>("event_name", eventName),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("attribution_confidence", attributionConfidence),
            new KeyValuePair<string, object?>("user", user));
    }

    public void RecordTokens(string platform, string model, string user, TokenUsage usage)
    {
        RecordTokenType(platform, model, user, "input", usage.InputTokens);
        RecordTokenType(platform, model, user, "output", usage.OutputTokens);
        RecordTokenType(platform, model, user, "cache_read", usage.CacheReadTokens);
        RecordTokenType(platform, model, user, "cache_creation", usage.CacheCreationTokens);
    }

    public void RecordCompression(string platform, string model, string user, ToolOutputCompression compression)
    {
        _compressionEvents.Add(1,
            new KeyValuePair<string, object?>("platform", platform),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("compressed", compression.Compressed));

        if (compression.Compressed && compression.TokensSaved > 0)
        {
            var platformTag = new KeyValuePair<string, object?>("platform", platform);
            var modelTag = new KeyValuePair<string, object?>("model", model);
            var userTag = new KeyValuePair<string, object?>("user", user);

            _compressionTokensSaved.Record(compression.TokensSaved, platformTag, modelTag, userTag);
            _compressionTokensSavedTotal.Add(compression.TokensSaved, platformTag, modelTag, userTag);
        }
    }

    private void RecordTokenType(string platform, string model, string user, string tokenType, long value)
    {
        if (value == 0) return;

        _tokens.Add(value,
            new KeyValuePair<string, object?>("platform", platform),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("token_type", tokenType),
            new KeyValuePair<string, object?>("user", user));
    }

    public void Dispose() => _meter.Dispose();
}
