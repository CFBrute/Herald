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
        _server = new HookServer(_h.Speech, _h.Settings, port: 0);
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
        await client.ConnectAsync(IPAddress.Loopback, _server.ListeningPort);
        using var stream = client.GetStream();
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
        Assert.Equal(("claude", "Hi from Claude."), (_h.History[0].Sender, _h.History[0].Text));
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
        Assert.Equal("Loud.", _h.History[0].Text);
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
    public async Task A_null_command_gets_an_error()
    {
        Assert.Equal("""{"status":"error"}""", await Send("null"));
    }

    [Fact]
    public async Task Garbage_closes_the_connection_and_the_server_keeps_going()
    {
        Assert.Null(await Send("this is not json"));

        Assert.Equal("ok", (string?)(await Command("""{ "type": "skip" }"""))["status"]);
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
