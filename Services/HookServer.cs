using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Herald.Services;

file class Command
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public int? Value { get; set; }

    /// <summary>
    /// Self-declared sender tag (e.g. "claude"). Not verified - any client can
    /// claim any tag. Defaults to "unknown" when omitted.
    /// </summary>
    public string? Sender { get; set; }
}

/// <summary>
/// Herald's inbox: a local-only TCP server taking one JSON command per connection, from
/// Claude Code's hook and anything else on this PC. While it listens, Herald's port is in
/// endpoint.json, so senders find it even after the port was changed in Settings.
/// </summary>
public class HookServer
{
    /// <summary>Used until the user picks another port, and by senders that can't read endpoint.json.</summary>
    public const int DefaultPort = AppSettings.DefaultHookPort;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SpeechEngine _engine;
    private readonly AppSettings _settings;
    private readonly AppLog? _log;
    private readonly int? _fixedPort;
    private readonly string? _endpointFile;
    private readonly Lock _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _started;

    /// <summary>Another Herald was started; it asks this one to bring its window forward.</summary>
    public event Action? ShowRequested;

    /// <summary>Listening started, stopped or failed; may be raised on any thread.</summary>
    public event Action? StatusChanged;

    /// <param name="port">A fixed port instead of the one in the settings; 0 picks any free port (tests).</param>
    /// <param name="endpointFile">Where to say which port is listened on; none in tests.</param>
    public HookServer(SpeechEngine engine, AppSettings settings, int? port = null, AppLog? log = null, string? endpointFile = null)
    {
        _log = log;
        _engine = engine;
        _settings = settings;
        _fixedPort = port;
        _endpointFile = endpointFile;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.HookPort) && _fixedPort == null) Restart();
        };
    }

    /// <summary>The port being listened on, or null when not listening.</summary>
    public int? ListeningPort { get; private set; }

    /// <summary>Why Herald isn't listening (e.g. another program has the port), or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>For the main window's status line.</summary>
    public string StatusText => ListeningPort is { } port ? $"Listening on 127.0.0.1:{port}" : Problem ?? "Not listening";

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            Listen();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            StopListening();
        }
        StatusChanged?.Invoke();
    }

    /// <summary>Moves to the port now in the settings.</summary>
    private void Restart()
    {
        lock (_gate)
        {
            if (!_started) return;
            StopListening();
            Listen();
        }
    }

    private void Listen()
    {
        var port = _fixedPort ?? _settings.HookPort;
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            // A port another program has, or one Windows keeps for itself (Hyper-V and the like).
            Problem = ex.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => $"Port {port} is in use by another program; choose another in Settings",
                SocketError.AccessDenied => $"Windows doesn't allow port {port}; choose another in Settings",
                _ => $"Can't listen on port {port} ({ex.Message}); choose another in Settings"
            };
            ListeningPort = null;
            _log?.Write("hook", Problem);
            DeleteEndpoint();
            StatusChanged?.Invoke();
            return;
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Problem = null;
        WriteEndpoint();
        _log?.Write("hook", $"Listening on 127.0.0.1:{ListeningPort}");
        _ = AcceptLoopAsync(listener, _cts.Token);
        StatusChanged?.Invoke();
    }

    private void StopListening()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts = null;
        ListeningPort = null;
        DeleteEndpoint();
    }

    // Property names are part of the file's format for other programs; keep them.
    private record Endpoint(string Host, int Port, int Pid, string Version);

    private void WriteEndpoint()
    {
        if (_endpointFile == null || ListeningPort is not { } port) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_endpointFile))!);
        SafeFile.WriteJson(_endpointFile, new Endpoint("127.0.0.1", port, Environment.ProcessId, BuildInfo.Version), EndpointJson);
    }

    private static readonly JsonSerializerOptions EndpointJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>Only Herald's own: another Herald that has since started may have written its own.</summary>
    private void DeleteEndpoint()
    {
        if (_endpointFile == null) return;
        if (SafeFile.ReadJson<Endpoint>(_endpointFile, EndpointJson) is { } endpoint && endpoint.Pid != Environment.ProcessId) return;
        SafeFile.TryDelete(_endpointFile);
    }

    /// <summary>
    /// The port a running Herald said it listens on, or null if there's no such file (Herald
    /// isn't running, or is older than this file). For senders written in C#; others read
    /// the same JSON: { "host": "127.0.0.1", "port": 8766, "pid": ..., "version": ... }.
    /// </summary>
    public static int? ReadEndpointPort(string endpointFile) =>
        SafeFile.ReadJson<Endpoint>(endpointFile, EndpointJson) is { Port: > 0 } endpoint ? endpoint.Port : null;

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        string? line = null;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // Plain UTF-8: without "new UTF8Encoding(false)" every reply would start with a byte order mark.
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            line = await reader.ReadLineAsync(ct);
            if (line == null) return;

            Command? command;
            try
            {
                command = JsonSerializer.Deserialize<Command>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                _log?.Write("hook", $"Couldn't read a message ({ex.Message}): {AppLog.Excerpt(line, 200)}");
                command = null;
            }

            if (command == null)
            {
                await writer.WriteLineAsync("{\"status\":\"error\"}");
                return;
            }

            var sender = string.IsNullOrWhiteSpace(command.Sender) ? "unknown" : command.Sender;

            switch (command.Type)
            {
                case "speak":
                    if (!string.IsNullOrWhiteSpace(command.Text))
                    {
                        _engine.EnqueueText(command.Text, sender);
                    }
                    break;

                case "toggle":
                    _engine.ToggleEnabled();
                    break;

                case "skip":
                    _engine.Skip();
                    break;

                case "skipmessage":
                    _engine.SkipMessage();
                    break;

                case "show":
                    ShowRequested?.Invoke();
                    break;

                case "setspeed":
                    if (command.Value is { } speed) _engine.SetSpeed(speed);
                    break;

                default:
                    _log?.Write("hook", $"Unknown command \"{command.Type}\" from {sender}");
                    break;
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "ok",
                enabled = _engine.Enabled,
                speed = _settings.SpeedPercent
            }));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The sender hung up before the reply, or Herald is closing - nothing lost.
        }
        catch (Exception ex)
        {
            _log?.Write("hook", $"Something went wrong handling {AppLog.Excerpt(line, 200)}", ex);
        }
    }
}
