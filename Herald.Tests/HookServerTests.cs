using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Herald.Services;

namespace Herald.Tests;

/// <summary>Talks to a hook server over TCP on a free port, like Claude Code's hook script does.</summary>
public class HookServerTests : IDisposable
{
    private readonly SpeechHarness _h = new();
    private readonly HookServer _server;

    public HookServerTests()
    {
        _server = new HookServer(_h.Speech, _h.Settings, port: 0, log: new AppLog(_h.Paths.HeraldLog, _h.Paths.HeraldOldLog));
        _server.Start();
    }

    public void Dispose()
    {
        _server.Stop();
        _h.Dispose();
    }

    private async Task<string?> Send(string line)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.ListeningPort!.Value);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task<JsonObject> Command(string json) => JsonNode.Parse((await Send(json))!)!.AsObject();

    [Fact]
    public async Task Speak_queues_the_text_under_the_given_sender()
    {
        var reply = await Command("""{ "type": "speak", "text": "Hi from Claude", "sender": "claude" }""");

        Assert.Equal("ok", (string?)reply["status"]);
        await _h.HistoryCount(1);
        Assert.Equal(("claude", "Hi from Claude."), (_h.History[0].Sender, _h.History[0].SpokenText));
    }

    [Fact]
    public async Task A_message_without_a_sender_is_filed_as_unknown()
    {
        await Command("""{ "type": "speak", "text": "Who am I" }""");

        await _h.HistoryCount(1);
        Assert.Equal("unknown", _h.History[0].Sender);
    }

    [Fact]
    public async Task Property_names_are_case_insensitive()
    {
        await Command("""{ "Type": "speak", "TEXT": "Loud", "Sender": "x" }""");

        await _h.HistoryCount(1);
        Assert.Equal("Loud.", _h.History[0].SpokenText);
    }

    [Fact]
    public async Task Toggle_switches_speech_and_reports_the_state()
    {
        var off = await Command("""{ "type": "toggle" }""");
        var on = await Command("""{ "type": "toggle" }""");

        Assert.False((bool)off["enabled"]!);
        Assert.True((bool)on["enabled"]!);
    }

    [Theory]
    [InlineData(150, 150)]
    [InlineData(999, 300)]
    [InlineData(10, 50)]
    public async Task Set_speed_changes_the_speed_within_range(int requested, int expected)
    {
        var reply = await Command($$"""{ "type": "setspeed", "value": {{requested}} }""");

        Assert.Equal(expected, (int)reply["speed"]!);
        Assert.Equal(expected, _h.Settings.SpeedPercent);
    }

    [Fact]
    public async Task Show_asks_the_window_to_come_forward()
    {
        var shown = new TaskCompletionSource();
        _server.ShowRequested += () => shown.TrySetResult();

        await Command("""{ "type": "show" }""");

        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Skip_commands_are_accepted()
    {
        Assert.Equal("ok", (string?)(await Command("""{ "type": "skip" }"""))["status"]);
        Assert.Equal("ok", (string?)(await Command("""{ "type": "skipmessage" }"""))["status"]);
    }

    [Fact]
    public async Task An_unknown_command_is_answered_but_does_nothing()
    {
        var reply = await Command("""{ "type": "dance" }""");

        Assert.Equal("ok", (string?)reply["status"]);
        Assert.True((bool)reply["enabled"]!);
        Assert.Empty(_h.Queue);
    }

    [Fact]
    public async Task The_reply_is_plain_utf8_without_a_byte_order_mark()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _server.ListeningPort!.Value, CancellationToken.None);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("""{ "type": "skip" }""" + "\n"), CancellationToken.None);

        var first = new byte[1];
        await stream.ReadExactlyAsync(first, CancellationToken.None);

        Assert.Equal((byte)'{', first[0]);
    }

    [Fact]
    public async Task A_null_command_gets_an_error()
    {
        Assert.Equal("""{"status":"error"}""", await Send("null"));
    }

    [Fact]
    public async Task Garbage_gets_an_error_is_logged_and_the_server_keeps_going()
    {
        Assert.Equal("""{"status":"error"}""", await Send("this is not json"));

        Assert.Contains("| hook | Couldn't read a message", await File.ReadAllTextAsync(_h.Paths.HeraldLog, CancellationToken.None));
        Assert.Contains("\"this is not json\"", await File.ReadAllTextAsync(_h.Paths.HeraldLog, CancellationToken.None));
        Assert.Equal("ok", (string?)(await Command("""{ "type": "skip" }"""))["status"]);
    }

    [Fact]
    public async Task An_unknown_command_is_logged()
    {
        await Command("""{ "type": "dance", "sender": "tester" }""");

        Assert.Contains("Unknown command \"dance\" from tester", await File.ReadAllTextAsync(_h.Paths.HeraldLog, CancellationToken.None));
    }

    [Fact]
    public async Task Several_clients_at_once_are_all_served()
    {
        var replies = await Task.WhenAll(Enumerable.Range(1, 5).Select(i =>
            Command($$"""{ "type": "speak", "text": "Message {{i}}", "sender": "parallel" }""")));

        Assert.All(replies, r => Assert.Equal("ok", (string?)r["status"]));
        await _h.HistoryCount(5);
    }
}

/// <summary>The hook server on the port from the settings, as in Herald, rather than a fixed one.</summary>
public class HookServerPortTests : IDisposable
{
    private readonly SpeechHarness _h = new();
    private readonly HookServer _server;

    public HookServerPortTests() =>
        _server = new HookServer(_h.Speech, _h.Settings, log: new AppLog(_h.Paths.HeraldLog, _h.Paths.HeraldOldLog),
                                 endpointFile: _h.Paths.EndpointFile);

    public void Dispose()
    {
        _server.Stop();
        _h.Dispose();
    }

    /// <summary>A port nothing listens on right now.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> Answers(int port)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2));
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("""{ "type": "skip" }""" + "\n"));
            return (await new StreamReader(stream).ReadLineAsync())?.Contains("\"ok\"") == true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            // Windows takes about two seconds to refuse a port nobody listens on.
            return false;
        }
    }

    [Fact]
    public async Task Listens_on_the_port_in_the_settings_and_says_so_in_endpoint_json()
    {
        var port = FreePort();
        _h.Settings.HookPort = port;

        _server.Start();

        Assert.Equal(port, _server.ListeningPort);
        Assert.True(await Answers(port));
        Assert.Equal($"Listening on 127.0.0.1:{port}", _server.StatusText);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(_h.Paths.EndpointFile, CancellationToken.None))!;
        Assert.Equal(("127.0.0.1", port, Environment.ProcessId), ((string?)json["host"], (int)json["port"]!, (int)json["pid"]!));
        Assert.Equal(port, HookServer.ReadEndpointPort(_h.Paths.EndpointFile));

        _server.Stop();

        Assert.False(File.Exists(_h.Paths.EndpointFile));
        Assert.Null(_server.ListeningPort);
    }

    [Fact]
    public async Task A_taken_port_is_reported_instead_of_crashing_and_another_can_be_chosen()
    {
        var other = new TcpListener(IPAddress.Loopback, FreePort());
        other.Start();
        try
        {
            var taken = ((IPEndPoint)other.LocalEndpoint).Port;
            _h.Settings.HookPort = taken;

            _server.Start();

            Assert.Null(_server.ListeningPort);
            Assert.Equal($"Port {taken} is in use by another program; choose another in Settings", _server.StatusText);
            Assert.False(File.Exists(_h.Paths.EndpointFile));
            Assert.Contains($"| hook | Port {taken} is in use by another program", await File.ReadAllTextAsync(_h.Paths.HeraldLog, CancellationToken.None));

            var free = FreePort();
            _h.Settings.HookPort = free;

            Assert.Equal(free, _server.ListeningPort);
            Assert.Null(_server.Problem);
            Assert.True(await Answers(free));
            Assert.Equal(free, HookServer.ReadEndpointPort(_h.Paths.EndpointFile));
        }
        finally
        {
            other.Stop();
        }
    }

    [Fact]
    public async Task Changing_the_port_moves_herald_over_straight_away()
    {
        var first = FreePort();
        _h.Settings.HookPort = first;
        _server.Start();
        var changes = 0;
        _server.StatusChanged += () => changes++;

        var second = FreePort();
        _h.Settings.HookPort = second;

        Assert.True(await Answers(second));
        Assert.False(await Answers(first));
        Assert.Equal(second, HookServer.ReadEndpointPort(_h.Paths.EndpointFile));
        Assert.True(changes > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{ "host": "127.0.0.1" }""")]
    public void A_missing_or_unreadable_endpoint_gives_no_port(string? content)
    {
        if (content != null) File.WriteAllText(_h.Paths.EndpointFile, content);

        Assert.Null(HookServer.ReadEndpointPort(_h.Paths.EndpointFile));
    }
}
