# V2 Tool Output Compression

> **Status: implemented.** `IToolOutputCompressor`
> (`src/UsageTracker.Library/Infrastructure/Compression/IToolOutputCompressor.cs`) is the
> extension point; `AddUsageTrackerLibrary` registers `DeterministicToolOutputCompressor` as the
> default. Hosts can override it. The daemon does: it registers a mode-aware router
> (`ModeAwareToolOutputCompressor`) that picks the compressor per hook based on the current
> `CompressionMode` config (see **Compression modes** below).

## Compression modes (daemon)

`DaemonConfig.CompressionMode` selects where compaction runs. It is resolved **per hook** by
`ModeAwareToolOutputCompressor`, so `usagetracker set-compression <mode>` takes effect on the next
command without a daemon restart (same per-call resolution as `RemoteEndpoint`).

- **`remote` (default)** — the daemon forwards the tool output to the backend (`UsageTracker.Functions`,
  reusing `RemoteEndpoint` + the Entra bearer client) via `POST api/compress`. The backend
  (`RemoteCompressionForwarder`) forwards to the **Headroom sidecar** when a `CompressionEndpoint` is
  configured (`POST /compress` with a single-message payload), logs compression metrics, and returns the
  compressed text — otherwise it falls back to its local `IToolOutputCompressor`.
- **`local`** — the daemon compacts in-process via `DeterministicToolOutputCompressor`, no backend round-trip.
- **`off`** — no compaction; ingest/mirror only.

### Headroom sidecar

`src/UsageTracker.Compressor.Headroom` is a small, source-agnostic FastAPI service (uvicorn) that wraps
the **`headroom-ai`** library (the bare `headroom` name on PyPI is an unrelated project). It exposes
`POST /compress` (a list of messages in → compressed messages + `tokens_saved`/`tokens_before`/
`tokens_after`/`compression_ratio`) and `GET /health`. It is wired into the AppHost via `AddDockerfile`
and its URL is handed to the backend through the `CompressionEndpoint` setting.

Behavior notes (verified against `headroom-ai`):

- **Aggressive by design.** `HeadroomService` runs `compress` with a `CompressConfig` that disables all
  protection (`compress_user_messages=True`, `protect_recent=0`, `protect_analysis_context=False`).
  Headroom protects `user` and recent messages by default; since the backend forwards a single tool
  output wrapped as one `user` message, those defaults would yield 0 savings — the aggressive config is
  what makes single-output compaction work.
- **JSON compresses for free; plain text needs `[ml]`.** The base `headroom-ai` install compresses
  structured/JSON content (SmartCrusher) with no extra deps (~60% on repetitive JSON arrays). Plain-text
  compaction (Kompress) requires the `[ml]` extra (torch + transformers, ~2GB image + a model download),
  so the base image leaves plain text unchanged. The daemon's local `DeterministicToolOutputCompressor`
  still handles plain text, and the backend falls back to it when the sidecar returns no change.

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
