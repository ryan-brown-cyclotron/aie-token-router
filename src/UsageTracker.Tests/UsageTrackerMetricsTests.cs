using System.Diagnostics.Metrics;

namespace UsageTracker.Tests;

public class UsageTrackerMetricsTests
{
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener;
        public readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> Measurements = new();

        public Recorder(UsageTrackerMetrics metrics)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (ReferenceEquals(instrument.Meter, metrics.Meter))
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Add((instrument.Name, value, tags.ToArray())));
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void RecordTokens_emits_one_measurement_per_nonzero_token_type()
    {
        using var metrics = new UsageTrackerMetrics();
        using var recorder = new Recorder(metrics);

        metrics.RecordTokens("claude-code", "claude-opus-4-8", "user@example.com",
            new TokenUsage(InputTokens: 10, OutputTokens: 5, CacheCreationTokens: 0, CacheReadTokens: 2, TurnsCounted: 1));

        var tokenMeasurements = recorder.Measurements.Where(m => m.Instrument == "usagetracker.tokens").ToList();
        Assert.Equal(3, tokenMeasurements.Count); // input, output, cache_read - cache_creation is zero and skipped
        Assert.Contains(tokenMeasurements, m => m.Value == 10 && m.Tags.Any(t => t is { Key: "token_type", Value: "input" }));
        Assert.Contains(tokenMeasurements, m => m.Value == 5 && m.Tags.Any(t => t is { Key: "token_type", Value: "output" }));
        Assert.Contains(tokenMeasurements, m => m.Value == 2 && m.Tags.Any(t => t is { Key: "token_type", Value: "cache_read" }));
    }

    [Fact]
    public void RecordHookEvent_tags_platform_event_model_confidence_and_user()
    {
        using var metrics = new UsageTrackerMetrics();
        using var recorder = new Recorder(metrics);

        metrics.RecordHookEvent("claude-code", "PostToolUse", "claude-opus-4-8", "session", "user@example.com");

        var measurement = Assert.Single(recorder.Measurements, m => m.Instrument == "usagetracker.hook.events");
        Assert.Equal(1, measurement.Value);
        Assert.Contains(measurement.Tags, t => t is { Key: "platform", Value: "claude-code" });
        Assert.Contains(measurement.Tags, t => t is { Key: "event_name", Value: "PostToolUse" });
        Assert.Contains(measurement.Tags, t => t is { Key: "attribution_confidence", Value: "session" });
        Assert.Contains(measurement.Tags, t => t is { Key: "user", Value: "user@example.com" });
    }

    [Fact]
    public void RecordCompression_only_records_tokens_saved_when_compressed()
    {
        using var metrics = new UsageTrackerMetrics();
        using var recorder = new Recorder(metrics);

        metrics.RecordCompression("github-copilot", "gpt-4o", "user@example.com", new ToolOutputCompression(true, "short", 100, 20));
        metrics.RecordCompression("github-copilot", "gpt-4o", "user@example.com", ToolOutputCompression.Unchanged("original"));

        var events = recorder.Measurements.Where(m => m.Instrument == "usagetracker.compression.events").ToList();
        Assert.Equal(2, events.Count);
        Assert.Single(events, m => m.Tags.Any(t => t is { Key: "compressed", Value: true }));
        Assert.Single(events, m => m.Tags.Any(t => t is { Key: "compressed", Value: false }));

        var saved = Assert.Single(recorder.Measurements, m => m.Instrument == "usagetracker.compression.tokens_saved");
        Assert.Equal(80, saved.Value);
        Assert.Contains(saved.Tags, t => t is { Key: "user", Value: "user@example.com" });

        var savedTotal = Assert.Single(recorder.Measurements, m => m.Instrument == "usagetracker.compression.tokens_saved_total");
        Assert.Equal(80, savedTotal.Value);
    }

    [Fact]
    public void RecordCompression_skips_the_saved_counters_when_nothing_was_saved()
    {
        using var metrics = new UsageTrackerMetrics();
        using var recorder = new Recorder(metrics);

        // Compressed=true with TokensSaved=0 shouldn't happen in practice (Compress() only accepts
        // strictly-smaller candidates), but the guard keeps a degenerate zero-delta measurement out
        // of the "how much did we save" series.
        metrics.RecordCompression("claude-code", "claude-opus-4-8", "user@example.com", new ToolOutputCompression(true, "same size", 50, 50));

        Assert.DoesNotContain(recorder.Measurements, m => m.Instrument is "usagetracker.compression.tokens_saved" or "usagetracker.compression.tokens_saved_total");
    }
}
