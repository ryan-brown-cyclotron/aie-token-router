# V2 Tool Output Compression

> **Status: implemented as an extension point.** Compression is optional and off by
> default. `IToolOutputCompressor` (`src/UsageTracker.Library/Infrastructure/Compression/IToolOutputCompressor.cs`)
> is a pure interface — no implementation ships in the repo. `AddUsageTrackerLibrary`
> registers no `IToolOutputCompressor`, so `ToolOutputCompressionService` resolves it as
> `null` and hooks just ingest and log normally with no compression attempted. A host
> that wants compression registers its own `IToolOutputCompressor` implementation.

Tool output compression is not a running sidecar or container. It is a single
interface plus a gating options type; a host opts in by registering an implementation
in its own composition root.

## Extension point

- `IToolOutputCompressor.CompressAsync(string toolOutput, string? model, CancellationToken)`
  returns a `ToolOutputCompression`. Implementations should always fail open: on any
  error, return `ToolOutputCompression.Unchanged` with the original text.
- `ToolOutputCompressionOptions` (bound from the `ToolOutputCompression` config
  section) is a placeholder for future configuration. There is no base URL or enabled flag; there is nothing to point at until a
  host registers a compressor.
- `ToolOutputCompressionService` calls the registered compressor, if any. Its
  constructor takes `IToolOutputCompressor? compressor = null`; when nothing is
  registered, `TryCompressAsync` returns `null` immediately.

## Flow

```
PostToolUse hook
  -> UsageTracker.Functions
  -> UsageTracker.Library runtime
  -> raw event stored
  -> large model-bound tool output extracted
  -> if a host registered an IToolOutputCompressor, it compresses the output
  -> Function returns platform-specific compressed result
```

Raw storage always happens before compression is attempted. Raw events are never
overwritten. With no compressor registered (the default), ingestion proceeds and logs
normally with no compression attempted at all.

## Compression scope

Compress only:

- `PostToolUse` / `postToolUse`
- Large shell output
- Test output
- Build logs
- File reads
- Search results
- Transcript slices before model summarization

Do not compress:

- Raw hook payloads before storage
- Session metadata
- Project/user context records
- Normalized usage events
- Small payloads below the configured threshold

## Fail open

If no compressor is registered, if a registered compressor is unavailable, or if
compression fails for any reason, the original tool output is returned unchanged.
Compression is an optimization, never a gate on the agent turn. No-compressor-registered
is the default, out-of-the-box behavior.

## Platform response shapes

- **GitHub Copilot** (`github-copilot/copilot`) — returns `{ modifiedResult }` with the
  compressed output when a compressor is registered and produces a change. This is the
  only in-path replacement wired today.
- **Claude Code** and **Cursor** — observe-only. Output replacement is not yet validated
  for these platforms, so the original output is preserved.
