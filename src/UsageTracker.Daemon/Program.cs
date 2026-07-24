using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UsageTracker;
using UsageTracker.Contracts;
using UsageTracker.Daemon;
using UsageTracker.Daemon.Auth;
using UsageTracker.Daemon.Compression;
using UsageTracker.Daemon.Configuration;
using UsageTracker.Daemon.Mcp;

var builder = WebApplication.CreateBuilder(args);

// Bootstrap config read (loopback port must be known before Kestrel is configured).
var bootstrapConfig = LoadBootstrapConfig();
var mcpEnabled = bootstrapConfig.McpEnabled;
var mcpPort = bootstrapConfig.McpPort > 0 ? bootstrapConfig.McpPort : CompressionModes.DefaultMcpPort;

// Local IPC transport: named pipe on Windows, Unix domain socket elsewhere. Both are per-user, so the
// OS access control is the primary local trust boundary; the X-Local-Token below is defense-in-depth.
builder.WebHost.ConfigureKestrel(options =>
{
    if (OperatingSystem.IsWindows())
    {
        options.ListenNamedPipe(DaemonPaths.PipeName);
    }
    else
    {
        var socketPath = DaemonPaths.SocketPath;
        if (File.Exists(socketPath)) File.Delete(socketPath); // clear a stale socket from a prior run
        options.ListenUnixSocket(socketPath);
    }

    // Optional loopback listener for HTTP-only hook hosts (e.g. GitHub Copilot) that cannot invoke a command.
    if (bootstrapConfig.LoopbackHttpPort > 0)
        options.ListenLocalhost(bootstrapConfig.LoopbackHttpPort);

    // Loopback listener for the MCP endpoint. 127.0.0.1/::1 only - never bind a routable address; the
    // MCP surface is unauthenticated (IDEs can't send our X-Local-Token) and relies on loopback isolation.
    if (mcpEnabled && mcpPort != bootstrapConfig.LoopbackHttpPort)
        options.ListenLocalhost(mcpPort);
});

builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default));

// Daemon services.
builder.Services.AddSingleton<DaemonConfigStore>();
builder.Services.AddSingleton<EntraTokenService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EntraTokenService>());
builder.Services.AddScoped<CommandProcessor>();

// Reuse the host-agnostic ingestion + compaction pipeline, supplying the daemon's own token-backed identity.
builder.Services.AddSingleton<IUserContext, DaemonUserContext>();
builder.Services.AddUsageTrackerLibrary(builder.Configuration);

// Compression mode is resolved dynamically per hook (not baked in at startup), so `set-compression`
// takes effect on the next command without a daemon restart - matching how RemoteEndpoint is resolved
// per-call. ModeAwareToolOutputCompressor reads the current mode from config each call and dispatches:
// "remote" (default) forwards to the backend (reusing RemoteEndpoint + the Entra bearer client), which
// optionally forwards to the Headroom service; "local" compacts in-process with no backend round-trip;
// "off" leaves the output unchanged.
builder.Services.RemoveAll<IToolOutputCompressor>();
builder.Services.AddSingleton<DeterministicToolOutputCompressor>();
builder.Services.AddSingleton<RemoteToolOutputCompressor>();
builder.Services.AddSingleton<IToolOutputCompressor, ModeAwareToolOutputCompressor>();

// Local MCP endpoint (project-context tools) over loopback HTTP. Thin wrappers delegate to the Library.
if (mcpEnabled)
{
    builder.Services
        .AddMcpServer()
        .WithHttpTransport(o => o.Stateless = true)
        .WithTools<ProjectContextMcpTools>();
}

// Outbound backend client carries the Entra Bearer token; Easy Auth on the Container App validates it.
// The base address is resolved per-call from current config (so `set-remote` takes effect without a restart).
builder.Services.AddTransient<BackendBearerHandler>();
builder.Services.AddHttpClient(CommandProcessor.BackendClientName, client => client.Timeout = TimeSpan.FromSeconds(8))
    .AddHttpMessageHandler<BackendBearerHandler>();

var app = builder.Build();

var expectedLocalToken = app.Services.GetRequiredService<DaemonConfigStore>().GetOrCreateLocalToken();

// Local-token guard on every endpoint except the unauthenticated liveness probe and the MCP endpoint
// (IDEs/MCP clients can't attach our X-Local-Token; /mcp is protected by loopback-only binding instead).
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/mcp"))
    {
        await next();
        return;
    }

    var provided = context.Request.Headers["X-Local-Token"].ToString();
    if (!CryptographicEquals(provided, expectedLocalToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Text("ok"));

app.MapGet("/status", (EntraTokenService tokens, DaemonConfigStore store) =>
{
    var config = store.Load();
    var status = tokens.Status();
    return Results.Json(new
    {
        daemon = "running",
        transport = OperatingSystem.IsWindows() ? $"pipe:{DaemonPaths.PipeName}" : $"unix:{DaemonPaths.SocketPath}",
        remote = config.RemoteEndpoint ?? "not configured",
        auth = status.State,
        user = status.UserEmail,
        expiresOn = status.ExpiresOn,
        deviceCode = status.PendingDeviceCodeMessage,
        compression = string.IsNullOrWhiteSpace(config.CompressionMode) ? CompressionModes.Remote : config.CompressionMode,
        mcp = config.McpEnabled ? $"http://127.0.0.1:{(config.McpPort > 0 ? config.McpPort : CompressionModes.DefaultMcpPort)}/mcp" : "disabled",
    });
});

app.MapPost("/command", HandleCommandAsync);
app.MapPost("/trace", HandleCommandAsync);

// Raw-payload entry point for HTTP-only hook hosts on the loopback listener.
app.MapPost("/ingest/{platform}", async (string platform, HttpRequest request, CommandProcessor processor, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var stdin = await reader.ReadToEndAsync(ct);
    var envelope = new CommandEnvelope(CommandEnvelope.KindHook, platform, [], stdin, Trace: false);
    var response = await processor.ProcessAsync(envelope, ct);
    return Results.Text(response.Stdout, "application/json");
});

// Project-context MCP tools on the loopback listener (see ProjectContextMcpTools). Exempt from the
// local-token guard above; reachable only on 127.0.0.1 when McpEnabled.
if (mcpEnabled)
    app.MapMcp("/mcp");

app.Run();
return;

static async Task<IResult> HandleCommandAsync(HttpRequest request, CommandProcessor processor, CancellationToken ct)
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync(ct);
    var envelope = JsonSerializer.Deserialize(body, ContractsJsonContext.Default.CommandEnvelope);
    if (envelope is null)
        return Results.Json(CommandResponse.Empty("empty or invalid envelope"), ContractsJsonContext.Default.CommandResponse);

    var response = await processor.ProcessAsync(envelope, ct);
    return Results.Json(response, ContractsJsonContext.Default.CommandResponse);
}

static DaemonConfig LoadBootstrapConfig()
{
    try
    {
        if (File.Exists(DaemonPaths.ConfigFilePath))
        {
            var json = File.ReadAllText(DaemonPaths.ConfigFilePath);
            return JsonSerializer.Deserialize(json, ContractsJsonContext.Default.DaemonConfig) ?? new DaemonConfig();
        }
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        // Fall through to defaults; the daemon still starts on its pipe/socket.
    }

    return new DaemonConfig();
}

static bool CryptographicEquals(string a, string b)
{
    if (a.Length != b.Length) return false;
    var diff = 0;
    for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
    return diff == 0;
}
