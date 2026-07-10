# Getting Started

Point your coding agent at UsageTracker in three steps.

## 1. Install the CLI

Publish `usagetracker` (single-file, self-contained) and put it on your `PATH`:

```bash
dotnet publish src/UsageTracker.Cli -c Release -r win-x64 \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true --self-contained
# copy the produced 'usagetracker' onto your PATH
```

## 2. Initialize

`init` writes your config, creates the local IPC token, records the daemon path for auto-start, and
verifies health:

```bash
usagetracker init \
  --remote https://<container-app-hostname> \
  --tenant <tenant-id> \
  --client <daemon-client-id> \
  --scope  api://<backend-app-guid>/access_as_user \
  --daemon-path <path-to>/usagetracker-daemon.exe

usagetracker status      # expect: Auth: acquired, User: you@company.com
```

Identity comes from your enrolled device via the daemon (WAM broker) — you do **not** set `USER_EMAIL`.

## 3. Wire your hooks

Let the CLI generate the hook files for you:

```bash
usagetracker setup claude     # writes .claude/settings.json in the current repo
usagetracker setup github     # writes .github/hooks/usage-tracking.json (Copilot)
```

`setup claude` merges the command-hook block into `.claude/settings.json` (creating it if absent; existing
unrelated settings are preserved). `setup github` writes `.github/hooks/usage-tracking.json` with Copilot
command hooks (camelCase events, `bash`/`powershell` keys) that pipe stdin to `usagetracker command copilot
--stdin` — the same path as Claude Code, so no loopback listener or extra environment variables are needed.

Prefer to copy by hand? The canonical examples are here:

- [claude-code.settings.json](claude-code.settings.json)
- [copilot.hooks.json](copilot.hooks.json)

See [hooks.md](hooks.md) for the full hook model and per-event coverage.

---

### Contributing to UsageTracker itself?

This repository's own `.claude/settings.json` targets the **built CLI in `src/UsageTracker.Cli/bin`
directly** (via `$CLAUDE_PROJECT_DIR`), so you don't need `usagetracker` on your `PATH` to work on the
repo — a `dotnet build` is enough, and the hooks fail open if the daemon isn't running. The examples in
this folder are the PATH-based configuration for end users.
