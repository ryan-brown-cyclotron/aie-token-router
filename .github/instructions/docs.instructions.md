---
applyTo: "docs/**"
description: "Use when writing documentation for the UsageTracker hook observability solution."
---

# Documentation Instructions

Documentation should be practical and implementation-oriented for this repository.

## Directory Map

```
docs/
├── README.md                     # Documentation entry point / index
├── onboarding/                   # Adopt the tool (user-facing)
│   ├── README.md                 # Getting started (install, init, setup)
│   ├── hooks.md                  # Hook configuration for Claude Code and GitHub Copilot
│   ├── claude-code.settings.json # Canonical Claude Code hook example
│   └── copilot.hooks.json        # Canonical Copilot hook example
├── deployment/                   # Run the service (operator-facing)
│   ├── setup-local.md            # Local Functions host, Aspire, and hook configuration
│   ├── container-app.md          # Container and Azure Container Apps notes + Easy Auth
│   └── hosting.md                # Azure Container Apps + Functions hosting
└── design/                       # How it works (architecture)
    ├── README.md                 # Design overview and index
    ├── architecture.md           # Retired V1 (UsageTracker.Api) historical notes
    ├── solution-structure.md     # Project tree, file movement, dependency rules
    ├── functions.md              # Function App HTTP boundary and endpoints
    ├── library.md                # Domain/Infrastructure/Runtime layering and DI
    ├── mcp-project-context.md    # MCP tools and project attribution model
    ├── dashboard.md              # Blazor dashboard read experience
    ├── tool-output-compression.md # Compression extension point, scope, and default behavior
    ├── daemon-cli.md             # Local daemon + thin CLI + Entra device auth
    └── roadmap.md                # Implementation phases and acceptance criteria
```

## Conventions

- Be explicit about what is implemented today versus planned later.
- Keep vendor-specific caveats visible, especially around user identity and session continuity.
- Prefer runnable commands over prose-only setup instructions.
- Do not copy older project taxonomies unless they fit this repo.