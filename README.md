# UsageTracker

UsageTracker observes coding-agent activity — Claude Code, GitHub Copilot, and Cursor — and turns it into
per-user, per-project token and tool-usage insight. Agents fire lifecycle hooks into a local daemon that
attaches a **verified Entra identity** (via your enrolled device), compacts large tool output, and forwards
to a backend that normalizes usage, attributes it to projects, and serves a dashboard.

```
agent hook ─▶ usagetracker CLI ─▶ local daemon ─▶ Azure Container App (Easy Auth) ─▶ Cosmos + dashboard
                (thin, fails open)   (identity · compaction · routing)
```

- **The daemon** is the local control plane: it acquires an Entra user token silently through the WAM
  broker on an Entra-joined device, reads local transcript files, compacts output, and calls the backend
  with a Bearer token.
- **The CLI** (`usagetracker`) is the thin, portable surface hooks/scripts invoke. It never blocks an agent
  — if the daemon is down, hooks fail open (exit 0, no output).
- **The backend** (`UsageTracker.Functions`, deployed to Azure Container Apps) validates the token via
  built-in **Easy Auth**, then ingests, persists, and serves reads.

## Quick start

```bash
# Build everything
dotnet build src/UsageTracker.sln

# Publish the CLI + daemon (single-file, self-contained) and put usagetracker on PATH
dotnet publish src/UsageTracker.Cli    -c Release -r win-x64 -p:PublishSingleFile=true --self-contained
dotnet publish src/UsageTracker.Daemon -c Release -r win-x64 -p:PublishSingleFile=true --self-contained

# Configure and wire your agent
usagetracker init --remote https://<host> --tenant <id> --client <id> \
                  --scope api://<backend>/access_as_user --daemon-path <path>/usagetracker-daemon.exe
usagetracker setup claude     # writes .claude/settings.json
usagetracker setup github     # writes .github/hooks/usage-tracking.json
usagetracker status           # Auth: acquired, User: you@company.com
```

New here? Start with [docs/onboarding/](docs/onboarding/README.md).

## CLI

| Command | Purpose |
|---|---|
| `usagetracker init [...]` | Write config, create the local token, register the daemon, verify health. |
| `usagetracker setup <claude\|github>` | Generate the hook file for an agent (merges, preserving other settings). |
| `usagetracker set-remote <url>` | Point the daemon at a backend. |
| `usagetracker command <name> --stdin` | The hook entry point: forward an event to the daemon. |
| `usagetracker trace <name> --stdin` | Same, with `[trace]` diagnostics on stderr (clean stdout). |
| `usagetracker status` | Daemon health, auth state, remote endpoint. |

## Solution layout

| Project | Role |
|---|---|
| `UsageTracker.Cli` | Thin per-invocation client (`usagetracker`). |
| `UsageTracker.Daemon` | Resident local control plane (`usagetracker-daemon`): Entra auth, compaction, routing. |
| `UsageTracker.Contracts` | Shared CLI↔daemon envelope, paths, and config. |
| `UsageTracker.Library` | Host-agnostic domain/infrastructure/runtime (ingestion, compaction, attribution). |
| `UsageTracker.Functions` | Azure Functions backend: hook ingestion, dashboard reads, MCP tools. |
| `UsageTracker.AppHost` / `UsageTracker.ServiceDefaults` | .NET Aspire local orchestration + defaults. |

## Documentation

- [Onboarding](docs/onboarding/README.md) — adopt the tool: install, `init`, wire hooks.
- [Deployment](docs/deployment/container-app.md) — stand up the backend, enable Easy Auth, run locally.
- [Design](docs/design/README.md) — architecture, the [daemon + CLI + auth model](docs/design/daemon-cli.md), and the [roadmap](docs/design/roadmap.md).

## Releases

Tagging `v*.*.*` triggers [`.github/workflows/release.yml`](.github/workflows/release.yml): a cross-platform
matrix build publishes `usagetracker` + `usagetracker-daemon` and packages archives (with install scripts
and SHA-256 checksums) for Windows, macOS (x64/arm64), and Linux.

## Requirements

.NET 8 SDK. Entra-joined/enrolled device for silent auth (device-code fallback otherwise). Windows is the
primary target for the WAM broker; macOS/Linux/WSL use the device-code flow.
