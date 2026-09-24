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

    private readonly SpeechEngine _engine;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public event Action<string>? CommandReceived;

    /// <summary>Another Herald was started; it asks this one to bring its window forward.</summary>
    public event Action? ShowRequested;

    public HookServer(SpeechEngine engine)
    {
        _engine = engine;
        _listener = new TcpListener(IPAddress.Loopback, Port);
    }

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
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var line = await reader.ReadLineAsync(ct);
            if (line == null) return;

            var command = JsonSerializer.Deserialize<Command>(line, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (command == null)
            {
                await writer.WriteLineAsync("{\"status\":\"error\"}");
                return;
            }

            CommandReceived?.Invoke(command.Type ?? "unknown");
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
                    _engine.Enabled = !_engine.Enabled;
                    _engine.Announce(_engine.Enabled ? "Activated" : "Off");
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
                    if (command.Value.HasValue)
                    {
                        _engine.SpeedPercent = command.Value.Value;
                        _engine.EnqueueText($"Speed {_engine.SpeedPercent}", "herald");
                    }
                    break;
            }

            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                status = "ok",
                enabled = _engine.Enabled,
                speed = _engine.SpeedPercent
            }));
        }
        catch
        {
            // client disconnected or sent garbage - ignore
        }
    }
}
