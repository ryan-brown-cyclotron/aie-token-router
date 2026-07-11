# UsageTracker Documentation

UsageTracker observes coding-agent activity (Claude Code, GitHub Copilot, Cursor) via hooks. Agents fire
hooks into the local `usagetracker` daemon, which attaches a verified Entra identity, compacts large tool
output, and forwards to a backend that normalizes usage, attributes it to projects, and serves a dashboard.

The docs are organized into three areas:

## [onboarding/](onboarding/) — adopt the tool

Start here if you want to point your agent at UsageTracker.

- [Getting started](onboarding/README.md) — install the CLI, run `usagetracker init`, wire your hooks.
- [Hook configuration](onboarding/hooks.md) — the hook model and per-agent settings.
- [claude-code.settings.json](onboarding/claude-code.settings.json) / [copilot.hooks.json](onboarding/copilot.hooks.json) — copy-paste examples.

## [deployment/](deployment/) — run the service

For whoever stands up the backend and local environment.

- [Local setup](deployment/setup-local.md) — Functions host, Aspire, local hook config.
- [Container Apps deployment](deployment/container-app.md) — containerize, deploy, and enable Easy Auth.
- [Hosting](deployment/hosting.md) — Azure Container Apps + Functions hosting model.

## [design/](design/) — how it works

Architecture and component design.

- [Design overview](design/README.md) — the current architecture and index.
- [Daemon + CLI](design/daemon-cli.md) — local daemon, thin CLI, and Entra device auth.
- [Solution structure](design/solution-structure.md), [Library](design/library.md), [Functions](design/functions.md), [Dashboard](design/dashboard.md), [MCP project context](design/mcp-project-context.md), [Tool-output compression](design/tool-output-compression.md).
- [Roadmap](design/roadmap.md) — implementation phases and follow-ups.
- [Architecture (retired V1 notes)](design/architecture.md) — historical context for the deleted `UsageTracker.Api`.

> `src/` is authoritative. Where a doc's endpoint, type name, or sample no longer matches the code, trust the code.
