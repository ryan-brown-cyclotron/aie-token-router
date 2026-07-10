---
applyTo: "docs/**"
description: "Use when writing documentation for the UsageTracker hook observability solution."
---

# Documentation Instructions

Documentation should be practical and implementation-oriented for this repository.

## Directory Map

```
docs/
├── README.md                     # Documentation entry point
├── architecture.md               # Current architecture and data flow
├── hooks.md                      # Hook configuration for Claude Code and GitHub Copilot
├── setup-local.md                # Local API, Aspire, and hook configuration
├── deployment-container-app.md   # Container and Azure Container Apps notes
└── v2/                           # Planned V2 restructure (Functions + Library + Dashboard)
    ├── README.md                 # V2 overview and index
    ├── solution-structure.md     # Project tree, file movement, dependency rules
    ├── functions.md              # Function App HTTP boundary and endpoints
    ├── library.md                # Domain/Infrastructure/Runtime layering and DI
    ├── mcp-project-context.md    # MCP tools and project attribution model
    ├── dashboard.md              # Blazor dashboard read experience
    ├── tool-output-compression.md # Compression extension point, scope, and default no-op behavior
    ├── hosting.md                # Azure Container Apps + Functions hosting
    └── roadmap.md                # Implementation phases and acceptance criteria
```

## Conventions

- Be explicit about what is implemented today versus planned later.
- Keep vendor-specific caveats visible, especially around user identity and session continuity.
- Prefer runnable commands over prose-only setup instructions.
- Do not copy older project taxonomies unless they fit this repo.