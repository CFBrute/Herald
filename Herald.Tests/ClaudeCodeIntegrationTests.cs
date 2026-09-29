using System.IO;
using System.Text.Json.Nodes;
using Herald.Services;

namespace Herald.Tests;

/// <summary>Works on a temporary stand-in for ~\.claude, never the real one.</summary>
public class ClaudeCodeIntegrationTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly string _claudeDir;
    private readonly string _hookScript;
    private readonly ClaudeCodeIntegration _claude;

    public ClaudeCodeIntegrationTests()
    {
        _claudeDir = Path.Combine(_dir.Path, ".claude");
        Directory.CreateDirectory(_claudeDir);
        _hookScript = Path.Combine(_dir.Path, "herald", "integrations", "claude-hook.ps1");
        _claude = new ClaudeCodeIntegration(_hookScript, _claudeDir);
    }

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => Path.Combine(_claudeDir, "settings.json");
    private JsonObject Settings => JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();

    private static IEnumerable<string> Commands(JsonObject settings, string eventName) =>
        (settings["hooks"]?[eventName] as JsonArray ?? [])
        .SelectMany(group => group!["hooks"]!.AsArray())
        .Select(hook => (string)hook!["command"]!);

    /// <summary>A hand-made hook script, like the ones people wrote before Herald could connect itself.</summary>
    private string HandMadeScript(bool talksToHerald)
    {
        var path = Path.Combine(_claudeDir, "scripts", talksToHerald ? "stream-response.ps1" : "other.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, talksToHerald ? $"$client.ConnectAsync('127.0.0.1', {HookServer.DefaultPort})" : "Write-Host hi");
        return path;
    }

    private void WriteSettings(string json) => File.WriteAllText(SettingsPath, json);

    [Fact]
    public void Without_settings_claude_code_is_not_connected()
    {
        Assert.Equal(ClaudeConnectionState.NotConnected, _claude.GetStatus().State);
    }

    [Fact]
    public void Connect_adds_an_async_message_display_hook_and_writes_the_script()
    {
        _claude.Connect();

        var hook = Settings["hooks"]!["MessageDisplay"]![0]!["hooks"]![0]!;
        Assert.Equal("command", (string?)hook["type"]);
        Assert.Equal($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{_hookScript}\"", (string?)hook["command"]);
        Assert.True((bool)hook["async"]!);
        Assert.Equal(10, (int)hook["timeout"]!);

        var script = File.ReadAllText(_hookScript);
        // The port comes from endpoint.json, with the usual one if that can't be read.
        Assert.Contains(Path.Combine(_dir.Path, "herald", "endpoint.json"), script);
        Assert.Contains($"$port = {HookServer.DefaultPort}", script);
        Assert.Contains("sender = \"claude\"", script);
        Assert.Equal(ClaudeConnectionState.Connected, _claude.GetStatus().State);
    }

    [Fact]
    public void Connect_keeps_everything_else_and_backs_up_the_old_file()
    {
        var otherScript = HandMadeScript(talksToHerald: false);
        var original = $$"""
            {
              "model": "opus",
              "permissions": { "allow": ["Bash(git status)"] },
              "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo pre" } ] } ],
                "MessageDisplay": [ { "hooks": [ { "type": "command", "command": "powershell -File \"{{otherScript.Replace("\\", "\\\\")}}\"" } ] } ]
              }
            }
            """;
        WriteSettings(original);

        var backup = _claude.Connect();

        var settings = Settings;
        Assert.Equal("opus", (string?)settings["model"]);
        Assert.Equal("Bash(git status)", (string?)settings["permissions"]!["allow"]![0]);
        Assert.Equal(["echo pre"], Commands(settings, "PreToolUse"));
        Assert.Equal(2, Commands(settings, "MessageDisplay").Count());
        Assert.Contains(Commands(settings, "MessageDisplay"), c => c.Contains(otherScript));
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public void Connecting_twice_leaves_a_single_hook()
    {
        _claude.Connect();
        _claude.Connect();

        Assert.Single(Commands(Settings, "MessageDisplay"));
    }

    [Fact]
    public void A_hand_made_script_that_talks_to_herald_is_recognised_and_replaced()
    {
        var script = HandMadeScript(talksToHerald: true).Replace("\\", "\\\\");
        WriteSettings($$"""
            {
              "hooks": {
                "MessageDisplay": [ { "hooks": [ { "type": "command", "command": "powershell -File \"{{script}}\"" } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "powershell -File \"{{script}}\"" } ] } ]
              }
            }
            """);

        var before = _claude.GetStatus();
        Assert.Equal(ClaudeConnectionState.ConnectedThroughOtherScript, before.State);
        Assert.Contains("stream-response.ps1", before.Detail);
        Assert.Contains("Stop hook", before.Detail);

        _claude.Connect();

        var settings = Settings;
        Assert.Null(settings["hooks"]!["Stop"]);
        Assert.Equal([_hookScript], Commands(settings, "MessageDisplay").Select(c => c.Split('"')[1]));
    }

    [Fact]
    public void Disconnect_removes_only_heralds_hooks()
    {
        WriteSettings("""{ "hooks": { "PreToolUse": [ { "hooks": [ { "type": "command", "command": "echo pre" } ] } ] } }""");
        _claude.Connect();

        _claude.Disconnect();

        Assert.Empty(Commands(Settings, "MessageDisplay"));
        Assert.Equal(["echo pre"], Commands(Settings, "PreToolUse"));
        Assert.Equal(ClaudeConnectionState.NotConnected, _claude.GetStatus().State);
    }

    [Fact]
    public void Disconnect_drops_the_hooks_section_when_nothing_is_left()
    {
        WriteSettings("""{ "model": "opus" }""");
        _claude.Connect();

        _claude.Disconnect();

        Assert.Equal(["model"], Settings.Select(p => p.Key));
    }

    [Fact]
    public void Settings_with_comments_and_trailing_commas_are_accepted()
    {
        WriteSettings("""
            {
              // Claude Code tolerates these
              "model": "opus",
            }
            """);

        _claude.Connect();

        Assert.Equal("opus", (string?)Settings["model"]);
    }

    [Theory]
    [InlineData("{ this is broken")]
    [InlineData("[1, 2, 3]")]
    public void A_file_herald_cannot_parse_is_reported_and_never_touched(string content)
    {
        WriteSettings(content);

        Assert.Equal(ClaudeConnectionState.SettingsUnreadable, _claude.GetStatus().State);
        Assert.ThrowsAny<Exception>(() => _claude.Connect());
        Assert.ThrowsAny<Exception>(() => _claude.Disconnect());
        Assert.Equal(content, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_missing_hook_script_means_not_connected()
    {
        _claude.Connect();
        File.Delete(_hookScript);

        var status = _claude.GetStatus();

        Assert.Equal(ClaudeConnectionState.NotConnected, status.State);
        Assert.Contains("missing", status.Detail);
    }

    [Fact]
    public void The_hook_script_is_refreshed_only_when_connected()
    {
        _claude.UpdateHookScriptIfConnected();
        Assert.False(File.Exists(_hookScript));

        _claude.Connect();
        File.WriteAllText(_hookScript, "old version");
        _claude.UpdateHookScriptIfConnected();

        Assert.Contains("MessageDisplay", File.ReadAllText(_hookScript));
    }
}

/// <summary>The real hook script, run by PowerShell as Claude Code runs it, against a Herald on an unusual port.</summary>
public class ClaudeHookScriptTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly SpeechHarness _h = new();

    public void Dispose()
    {
        _h.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task The_hook_script_finds_herald_on_whatever_port_endpoint_json_names()
    {
        var herald = Path.Combine(_dir.Path, "herald");
        var server = new HookServer(_h.Speech, _h.Settings, port: 0, endpointFile: Path.Combine(herald, "endpoint.json"));
        server.Start();
        try
        {
            Assert.NotEqual(HookServer.DefaultPort, server.ListeningPort);
            // Without it the script would fall back to 8766: the Herald actually running on this PC.
            Assert.Equal(server.ListeningPort, HookServer.ReadEndpointPort(Path.Combine(herald, "endpoint.json")));
            var claude = new ClaudeCodeIntegration(Path.Combine(herald, "integrations", "claude-hook.ps1"), Path.Combine(_dir.Path, ".claude"));
            claude.Connect();

            var run = new System.Diagnostics.ProcessStartInfo("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(herald, "integrations", "claude-hook.ps1")}\"")
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var hook = System.Diagnostics.Process.Start(run)!;
            await hook.StandardInput.WriteAsync("""{ "delta": "Hello from the hook" }""");
            hook.StandardInput.Close();
            await hook.WaitForExitAsync(TestContext.Current.CancellationToken);

            await _h.HistoryCount(1);
            Assert.Equal(("claude", "Hello from the hook."), (_h.History[0].Sender, _h.History[0].SpokenText));
        }
        finally
        {
            server.Stop();
        }
    }
}
