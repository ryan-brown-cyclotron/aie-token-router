# Local Daemon + Thin CLI

## Why

Coding-agent hooks used to POST straight to the backend with an unverified `X-User-Email` header on
anonymous routes — there was no real user identity, and anyone who could reach the endpoint could post
as anyone. This design closes that gap using the fact that our machines are **Entra-joined / enrolled**:
a local process running as the signed-in user can silently obtain a real Entra ID **user token** via the
WAM broker and present it to the backend, where **Azure Container Apps Easy Auth** validates it.

A native HTTP hook can't acquire that token; a **command hook → thin CLI → resident daemon** can.

```
Bash/PowerShell hook · IDE · script · user
        ↓  usagetracker command claude-code --stdin
  usagetracker CLI                (thin, per-invocation, fails open)
        ↓  named pipe / Unix socket  (+ X-Local-Token)
  UsageTracker.Daemon             (resident; identity, auth, compaction, routing)
        ↓  HTTPS + Bearer (Entra user token via WAM)
  Azure Container App (UsageTracker.Functions)   (Easy Auth validates the token)
```

- **Daemon** = remember / authorize / compact / route / observe.
- **CLI** = execute / trace / configure. It holds no daemon logic.

## Projects

| Project | Role |
|---|---|
| `src/UsageTracker.Contracts` | Shared, dependency-free envelope/response records, per-OS path resolver (`DaemonPaths`), local-token store (`LocalSecrets`), and `System.Text.Json` source-gen context. |
| `src/UsageTracker.Daemon` | Resident ASP.NET Core host. Kestrel over pipe/UDS, `EntraTokenService` (WAM), reuses `AddUsageTrackerLibrary` for local ingestion/compaction, mirrors to the backend with a Bearer token. |
| `src/UsageTracker.Cli` | `usagetracker` — spawned per hook. `init`, `set-remote`, `command`, `trace`, `status`. Publish single-file / self-contained / ReadyToRun. |

## Request flow

The CLI fills a `CommandEnvelope { Kind, Name, Args, Stdin, Trace }` and POSTs it to the daemon over the
per-user pipe/socket with the `X-Local-Token` header. The daemon:

1. Parses the hook JSON from `Stdin`.
2. Runs `IHookIngestionService.IngestAsync(platform, root)` **locally** — this reads the transcript file
   on local disk (which the cloud backend cannot see), compacts large tool output, and produces the hook
   response including any `modifiedResult`.
3. If a remote endpoint is configured, mirrors the raw payload to `POST {remote}/api/hooks/{platform}`
   with the Entra Bearer token for durable, user-attributed storage.
4. Returns a `CommandResponse`; the CLI writes `Stdout` verbatim (byte-clean for the hook), and — for
   `trace` — `Diagnostics` to **stderr**.

Everything is **fail-open**: an unreachable daemon, a malformed payload, or a backend error never blocks
the agent. The CLI exits 0 with empty stdout on any hook-path failure.

## Transport & local trust boundary

- Windows: **named pipe** `UsageTracker.<user-hash>`.
- macOS/Linux/WSL: **Unix domain socket** `$XDG_RUNTIME_DIR/usagetracker-<user-hash>.sock`.

Both are per-user, so the OS access control is the primary boundary. A per-install random `X-Local-Token`
(in the access-restricted `secrets.json`) is required on every request as defense-in-depth. `/health` is
the only unauthenticated endpoint (liveness only).

## Entra authentication (device SSO)

`EntraTokenService` builds one MSAL `PublicClientApplication` with the WAM broker on Windows and:

1. `AcquireTokenSilent` (using the signed-in account / `OperatingSystemAccount`) — headless SSO on an
   Entra-joined device, no prompt.
2. On `MsalUiRequiredException` → interactive broker (Windows) — system account picker, no password.
3. No broker (WSL/Linux/macOS) → **device code**; the code+URL is surfaced by `usagetracker status`. After
   first sign-in the refresh token persists in the MSAL cache (DPAPI / Keychain / libsecret).

A background loop refreshes ~5 minutes before expiry so a valid token is always ready when a hook fires.
The token's identity (`oid`, `preferred_username`, `name`) also backs the daemon's `IUserContext`, so
local ingestion records the real user.

### App registrations (one-time, in scope)

- **Backend API app** — represents the Container App. Expose scope `access_as_user`; App ID URI
  `api://<backend-app-guid>`. This is the audience Easy Auth validates.
- **Public client app** — the daemon. `AllowPublicClient=true`; broker redirect URI
  `ms-appx-web://microsoft.aad.brokerplugin/<client-id>`; delegated permission to
  `api://<backend>/access_as_user`; grant **admin consent** so silent acquisition never prompts.

Put the resulting `tenantId`, client `clientId`, and `scope` (`api://<backend>/access_as_user`) into the
daemon config via `usagetracker init`.

## Backend: Container Apps Easy Auth

Enable built-in authentication on the Container App with Microsoft as the provider:

- `unauthenticatedClientAction: Return401` (API mode — no login redirect).
- `allowedAudiences: ["api://<backend-app-guid>"]`.

Easy Auth injects the validated principal as `X-MS-CLIENT-PRINCIPAL` (base64 JSON claims) plus
`X-MS-CLIENT-PRINCIPAL-ID` / `-NAME`. `FunctionsUserContext` reads these **first**; `X-User-Email` is now
**Development-only**. See [../deployment/container-app.md](../deployment/container-app.md) for the exact CLI/Bicep.

> Easy Auth gates **all** ingress, so the in-worker MCP/SSE endpoints are gated too. That's fine while the
> token-bearing daemon is the only backend caller; revisit if an IDE must reach MCP directly.

## Install / rollout

```bash
# 1. Build & publish the CLI (single-file) and the daemon.
dotnet publish src/UsageTracker.Cli -c Release -r win-x64 \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true --self-contained
dotnet publish src/UsageTracker.Daemon -c Release -r win-x64 --self-contained

# 2. Put usagetracker on PATH, then initialize.
usagetracker init \
  --remote https://<container-app-hostname> \
  --tenant <tenant-id> \
  --client <daemon-client-id> \
  --scope  api://<backend-app-guid>/access_as_user \
  --daemon-path <path-to>/usagetracker-daemon.exe

usagetracker status          # confirm: Auth: acquired, User: you@company.com
```

- **Claude Code**: `usagetracker setup claude` (or merge `claude-code.settings.json`). Command hooks call
  `usagetracker command claude-code --stdin`; no `USER_EMAIL` — identity comes from the daemon.
- **Copilot**: `usagetracker setup github` (or merge `copilot.hooks.json`). Copilot delivers events on
  stdin, so it uses the same command-hook path (`usagetracker command copilot --stdin`) via `bash`/
  `powershell` keys and camelCase event names — no loopback listener or local token needed.

### Auto-start

The daemon should run **per-user at logon** (not a machine service) so WAM SSO and per-user socket ACLs
work: Windows Scheduled Task at logon / macOS `launchd` LaunchAgent / Linux `systemd --user`. The CLI also
auto-starts the daemon on demand (via `DaemonExecutablePath`) and fails open if it can't.

## Verification

- `usagetracker status` → `Auth: acquired` with your email, silently, on an enrolled device.
- `echo '{"hook_event_name":"PostToolUse"}' | usagetracker trace claude-code --stdin` → clean stdout,
  `[trace]` lines on stderr (daemon, user, remote, backend status code).
- Backend records the event under the verified `oid`/`preferred_username`; a call without a token → 401.
- Stop the daemon mid-session → hooks still return promptly (exit 0); the agent is not blocked.
