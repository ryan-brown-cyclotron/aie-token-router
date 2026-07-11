using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using UsageTracker.Contracts;

namespace UsageTracker.Cli;

/// <summary>
/// The CLI's transport to the resident daemon. Speaks HTTP/1.1 over the per-user named pipe (Windows) or
/// Unix domain socket, injecting the local IPC token. If the daemon isn't running it auto-starts it and
/// retries, then fails open so a hook is never blocked.
/// </summary>
public sealed class DaemonClient : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(8);
    private readonly HttpClient _http;
    private readonly string? _localToken;

    public DaemonClient()
    {
        var handler = new SocketsHttpHandler { ConnectCallback = ConnectAsync };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(12) };
        _localToken = LocalSecrets.TryReadLocalToken();
        if (!string.IsNullOrWhiteSpace(_localToken))
            _http.DefaultRequestHeaders.Add("X-Local-Token", _localToken);
    }

    /// <summary>Sends an envelope, auto-starting the daemon if needed. Returns null on unrecoverable failure.</summary>
    public async Task<CommandResponse?> SendAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var path = envelope.Trace ? "/trace" : "/command";
        var body = JsonSerializer.Serialize(envelope, ContractsJsonContext.Default.CommandEnvelope);

        if (!await EnsureDaemonRunningAsync(cancellationToken))
            return null;

        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(path, content, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(json)
                ? CommandResponse.Empty()
                : JsonSerializer.Deserialize(json, ContractsJsonContext.Default.CommandResponse);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<string?> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!await EnsureDaemonRunningAsync(cancellationToken))
            return null;
        try
        {
            return await _http.GetStringAsync("/status", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> EnsureDaemonRunningAsync(CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync(cancellationToken))
            return true;

        if (!TryStartDaemon())
            return false;

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsHealthyAsync(cancellationToken))
                return true;
            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    private static bool TryStartDaemon()
    {
        try
        {
            var config = LoadConfig();
            var exePath = config.DaemonExecutablePath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return false;

            var startInfo = new ProcessStartInfo { FileName = exePath };
            // .dll executables are launched via the dotnet host.
            if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = "dotnet";
                startInfo.ArgumentList.Add(exePath);
            }

            // Detach the daemon from the caller's console so it does NOT inherit (and hold open) the
            // hook's stdout/stderr. If it did, the agent's hook reader would block on EOF until its
            // timeout on the very first invocation (the one that auto-starts the daemon).
            if (OperatingSystem.IsWindows())
            {
                // ShellExecute launches without passing the parent's std handles; Hidden keeps it windowless.
                startInfo.UseShellExecute = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            }
            else
            {
                // Redirect the inherited descriptors to pipes we immediately drop, so the daemon's stdout
                // is not tied to the caller's; the daemon logs to its own file, not these streams.
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.RedirectStandardInput = true;
            }

            using var proc = Process.Start(startInfo);
            if (proc is not null && startInfo.RedirectStandardOutput)
            {
                // Drain-and-discard so a chatty daemon never blocks on a full pipe buffer.
                _ = proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                _ = proc.StandardError.BaseStream.CopyToAsync(Stream.Null);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _, CancellationToken cancellationToken)
        => OperatingSystem.IsWindows() ? ConnectPipeAsync(cancellationToken) : ConnectSocketAsync(cancellationToken);

    private static async ValueTask<Stream> ConnectPipeAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", DaemonPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cancellationToken);
        return pipe;
    }

    private static async ValueTask<Stream> ConnectSocketAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(DaemonPaths.SocketPath), cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }

    private static DaemonConfig LoadConfig()
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

    public void Dispose() => _http.Dispose();
}
