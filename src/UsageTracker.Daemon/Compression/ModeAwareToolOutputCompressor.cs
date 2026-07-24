using UsageTracker.Contracts;
using UsageTracker.Daemon.Configuration;

namespace UsageTracker.Daemon.Compression;

/// <summary>
/// The daemon's <see cref="IToolOutputCompressor"/>. Resolves the compression mode from current config
/// on <em>every</em> call (not baked in at startup) so <c>set-compression</c> takes effect on the next
/// hook without a daemon restart - the same per-call resolution used for <see cref="DaemonConfig.RemoteEndpoint"/>.
/// Dispatches to: <c>remote</c> (the default) → forward to the backend; <c>local</c> → in-process
/// deterministic compaction; <c>off</c> → leave the output unchanged.
/// </summary>
public sealed class ModeAwareToolOutputCompressor : IToolOutputCompressor
{
    private readonly DeterministicToolOutputCompressor _local;
    private readonly RemoteToolOutputCompressor _remote;
    private readonly DaemonConfigStore _configStore;

    public ModeAwareToolOutputCompressor(
        DeterministicToolOutputCompressor local,
        RemoteToolOutputCompressor remote,
        DaemonConfigStore configStore)
    {
        _local = local;
        _remote = remote;
        _configStore = configStore;
    }

    public Task<ToolOutputCompression> CompressAsync(string toolOutput, string? model, CancellationToken cancellationToken = default)
    {
        var mode = _configStore.Load().CompressionMode;

        if (string.Equals(mode, CompressionModes.Off, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolOutputCompression.Unchanged(toolOutput));

        if (string.Equals(mode, CompressionModes.Local, StringComparison.OrdinalIgnoreCase))
            return _local.CompressAsync(toolOutput, model, cancellationToken);

        // Default (including "remote" and any unrecognized value): forward to the backend.
        return _remote.CompressAsync(toolOutput, model, cancellationToken);
    }
}
