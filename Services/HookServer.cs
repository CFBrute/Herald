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

public class HookServer
{
    public const int Port = 8766;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly SpeechEngine _engine;
    private readonly AppSettings _settings;
    private readonly AppLog? _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Another Herald was started; it asks this one to bring its window forward.</summary>
    public event Action? ShowRequested;

    /// <param name="port">Herald's fixed <see cref="Port"/> unless given; 0 picks any free port.</param>
    public HookServer(SpeechEngine engine, AppSettings settings, int port = Port, AppLog? log = null)
    {
        _log = log;
        _engine = engine;
        _settings = settings;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>The port actually listened on, once started.</summary>
    public int ListeningPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
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
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

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
