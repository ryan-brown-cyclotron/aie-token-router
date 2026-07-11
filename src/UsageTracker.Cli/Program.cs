using System.Text.Json;
using UsageTracker.Cli;
using UsageTracker.Contracts;

// Thin client: parse argv + stdin into a CommandEnvelope, round-trip to the local daemon, print the
// result. Hook-path commands (command/trace) FAIL OPEN — any failure prints nothing and exits 0 so a
// coding agent is never blocked.

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var token = cts.Token;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var verb = args[0].ToLowerInvariant();
var rest = args[1..];

try
{
    return verb switch
    {
        "init" => await InitAsync(rest, token),
        "setup" => SetupCommand.Run(rest),
        "set-remote" => SetRemote(rest),
        "set-compression" => SetCompression(rest),
        "mcp" => Mcp(rest),
        "command" => await RunCommandAsync(rest, trace: false, token),
        "trace" => await RunCommandAsync(rest, trace: true, token),
        "status" => await StatusAsync(token),
        "-h" or "--help" or "help" => Help(),
        _ => Unknown(verb),
    };
}
catch (OperationCanceledException)
{
    // Timed out — fail open for hook paths, error otherwise.
    return verb is "command" or "trace" ? 0 : 1;
}

static async Task<int> RunCommandAsync(string[] args, bool trace, CancellationToken token)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: usagetracker command <name> [args...] [--stdin]");
        return trace ? 1 : 0; // fail open for the command (hook) path
    }

    var useStdin = RemoveFlag(ref args, "--stdin");
    var name = args[0];
    var positional = args[1..];
    var stdin = useStdin ? await Console.In.ReadToEndAsync(token) : null;

    var envelope = new CommandEnvelope(
        Kind: trace ? CommandEnvelope.KindTrace : CommandEnvelope.KindCommand,
        Name: name,
        Args: positional,
        Stdin: stdin,
        Trace: trace);

    using var client = new DaemonClient();
    var response = await client.SendAsync(envelope, token);

    if (response is null)
        return 0; // daemon unreachable → fail open, emit nothing

    if (!string.IsNullOrEmpty(response.Stdout))
        Console.Out.Write(response.Stdout);
    if (trace && !string.IsNullOrEmpty(response.Diagnostics))
        Console.Error.Write(response.Diagnostics);

    return response.ExitCode;
}

static async Task<int> StatusAsync(CancellationToken token)
{
    using var client = new DaemonClient();
    var status = await client.GetStatusAsync(token);
    if (status is null)
    {
        Console.WriteLine("Daemon: not running");
        Console.WriteLine($"Config: {DaemonPaths.ConfigFilePath}");
        return 1;
    }

    using var doc = JsonDocument.Parse(status);
    var root = doc.RootElement;
    Console.WriteLine($"Daemon:  {GetString(root, "daemon")}");
    Console.WriteLine($"Transport: {GetString(root, "transport")}");
    Console.WriteLine($"Remote:  {GetString(root, "remote")}");
    Console.WriteLine($"Auth:    {GetString(root, "auth")}");
    Console.WriteLine($"User:    {GetString(root, "user") ?? "(none)"}");
    Console.WriteLine($"Compress: {GetString(root, "compression") ?? "local"}");
    Console.WriteLine($"MCP:     {GetString(root, "mcp") ?? "disabled"}");
    var deviceCode = GetString(root, "deviceCode");
    if (!string.IsNullOrWhiteSpace(deviceCode))
        Console.WriteLine($"\nDevice sign-in required:\n{deviceCode}");
    return 0;
}

static int SetRemote(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: usagetracker set-remote <endpoint>");
        return 1;
    }

    if (!Uri.TryCreate(args[0], UriKind.Absolute, out _))
    {
        Console.Error.WriteLine($"'{args[0]}' is not an absolute URL");
        return 1;
    }

    var config = LoadConfig();
    config.RemoteEndpoint = args[0];
    SaveConfig(config);
    Console.WriteLine($"Remote set to {args[0]}");
    Console.WriteLine("Restart the daemon or wait for it to reload on the next command.");
    return 0;
}

static int SetCompression(string[] args)
{
    if (args.Length == 0 || !CompressionModes.IsValid(args[0]))
    {
        Console.Error.WriteLine("usage: usagetracker set-compression <local|off>");
        Console.Error.WriteLine("  local  the daemon compacts large tool output locally (default, no backend round-trip)");
        Console.Error.WriteLine("  off    ingest/mirror only; no compaction");
        return 1;
    }

    var config = LoadConfig();
    config.CompressionMode = args[0].ToLowerInvariant();
    SaveConfig(config);
    Console.WriteLine($"Compression mode: {config.CompressionMode}");
    Console.WriteLine("Restart the daemon for the change to take effect.");
    return 0;
}

static int Mcp(string[] args)
{
    var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
    var config = LoadConfig();

    switch (sub)
    {
        case "enable":
        case "disable":
            config.McpEnabled = sub == "enable";
            if (config.McpEnabled && int.TryParse(OptionValue(args, "--port"), out var port) && port > 0)
                config.McpPort = port;
            SaveConfig(config);
            Console.WriteLine($"MCP endpoint {(config.McpEnabled ? "enabled" : "disabled")}.");
            if (config.McpEnabled)
                Console.WriteLine($"Endpoint: {McpUrl(config)}");
            Console.WriteLine("Restart the daemon for the change to take effect.");
            return 0;

        case "show":
            if (!config.McpEnabled)
            {
                Console.WriteLine("MCP endpoint: disabled");
                Console.WriteLine("Enable it with: usagetracker mcp enable [--port <n>]");
                return 0;
            }
            Console.WriteLine($"MCP endpoint: {McpUrl(config)}");
            Console.WriteLine("Add it to your IDE's MCP config as an HTTP server pointing at that URL.");
            return 0;

        default:
            Console.Error.WriteLine("usage: usagetracker mcp [enable [--port <n>] | disable]");
            return 1;
    }
}

static string McpUrl(DaemonConfig config) =>
    $"http://127.0.0.1:{(config.McpPort > 0 ? config.McpPort : CompressionModes.DefaultMcpPort)}/mcp";

static async Task<int> InitAsync(string[] args, CancellationToken token)
{
    var config = LoadConfig();
    config.RemoteEndpoint = OptionValue(args, "--remote") ?? config.RemoteEndpoint;
    config.TenantId = OptionValue(args, "--tenant") ?? config.TenantId;
    config.ClientId = OptionValue(args, "--client") ?? config.ClientId;
    config.Scope = OptionValue(args, "--scope") ?? config.Scope;
    config.DaemonExecutablePath = OptionValue(args, "--daemon-path") ?? config.DaemonExecutablePath ?? DiscoverDaemon();
    if (int.TryParse(OptionValue(args, "--loopback-port"), out var port))
        config.LoopbackHttpPort = port;

    SaveConfig(config);
    LocalSecrets.GetOrCreateLocalToken();

    Console.WriteLine("Initialized usagetracker");
    Console.WriteLine($"Config: {DaemonPaths.ConfigFilePath}");
    Console.WriteLine($"Daemon: {config.DaemonExecutablePath ?? "(not found — pass --daemon-path)"}");
    Console.WriteLine($"Remote: {config.RemoteEndpoint ?? "not configured (use set-remote)"}");

    using var client = new DaemonClient();
    var healthy = await client.IsHealthyAsync(token);
    if (!healthy)
    {
        // Auto-start attempt happens inside GetStatus/Send; nudge it with a status call.
        await client.GetStatusAsync(token);
        healthy = await client.IsHealthyAsync(token);
    }
    Console.WriteLine($"Health: {(healthy ? "running" : "not running (start the daemon or check --daemon-path)")}");
    return 0;
}

static string? DiscoverDaemon()
{
    var baseDir = AppContext.BaseDirectory;
    string[] candidates =
    [
        Path.Combine(baseDir, "usagetracker-daemon.exe"),
        Path.Combine(baseDir, "usagetracker-daemon.dll"),
        Path.Combine(baseDir, "..", "UsageTracker.Daemon", "usagetracker-daemon.exe"),
        Path.Combine(baseDir, "..", "UsageTracker.Daemon", "usagetracker-daemon.dll"),
    ];
    return candidates.FirstOrDefault(File.Exists) is { } found ? Path.GetFullPath(found) : null;
}

static DaemonConfig LoadConfig()
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
        // Fall through to defaults.
    }
    return new DaemonConfig();
}

static void SaveConfig(DaemonConfig config)
{
    Directory.CreateDirectory(DaemonPaths.ConfigDirectory);
    var json = JsonSerializer.Serialize(config, ContractsJsonContext.Default.DaemonConfig);
    File.WriteAllText(DaemonPaths.ConfigFilePath, json);
}

static bool RemoveFlag(ref string[] args, string flag)
{
    var index = Array.IndexOf(args, flag);
    if (index < 0) return false;
    args = args.Where((_, i) => i != index).ToArray();
    return true;
}

static string? OptionValue(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string? GetString(JsonElement root, string name) =>
    root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

static int Help() { PrintUsage(); return 0; }
static int Unknown(string verb) { Console.Error.WriteLine($"unknown command: {verb}"); PrintUsage(); return 1; }

static void PrintUsage()
{
    Console.Error.WriteLine(
        """
        usagetracker — thin client for the local UsageTracker daemon

          init [--remote <url>] [--tenant <id>] [--client <id>] [--scope <s>] [--daemon-path <p>] [--loopback-port <n>]
          setup <claude|github>
          set-remote <endpoint>
          set-compression <local|off>
          mcp [enable [--port <n>] | disable]
          command <name> [args...] [--stdin]
          trace <name> [args...] [--stdin]
          status
        """);
}
