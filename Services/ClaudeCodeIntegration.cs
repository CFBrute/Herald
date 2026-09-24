using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Herald.Services;

public enum ClaudeConnectionState
{
    NotInstalled,
    NotConnected,
    /// <summary>Something sends to Herald, but not Herald's own hook script.</summary>
    ConnectedThroughOtherScript,
    Connected,
    SettingsUnreadable
}

public record ClaudeStatus(ClaudeConnectionState State, string Summary, string Detail);

/// <summary>
/// Finds Claude Code on this PC and wires its MessageDisplay hook to Herald, so each
/// block of Claude's reply is sent here as it appears. Only edits Claude's user
/// settings when asked, and keeps a backup of the previous file.
/// </summary>
public class ClaudeCodeIntegration
{
    private static readonly Regex ScriptPathInCommand = new(@"""([^""]+\.(?:ps1|py|cmd|bat|js))""|(\S+\.(?:ps1|py|cmd|bat|js))",
                                                            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _claudeDir;
    private readonly string _hookScriptPath;

    public string SettingsPath => Path.Combine(_claudeDir, "settings.json");
    public string BackupPath => SettingsPath + ".herald-backup";
    public string HookScriptPath => _hookScriptPath;
    private string HookCommand => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{_hookScriptPath}\"";

    public ClaudeCodeIntegration(string appDataDir)
    {
        _claudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _hookScriptPath = Path.Combine(appDataDir, "integrations", "claude-hook.ps1");
    }

    public ClaudeStatus GetStatus()
    {
        var onPath = FindOnPath("claude.exe") ?? FindOnPath("claude.cmd");
        if (!Directory.Exists(_claudeDir) && onPath == null)
        {
            return new ClaudeStatus(ClaudeConnectionState.NotInstalled, "Claude Code was not found on this PC.",
                "Herald looks for Claude Code's settings folder (%USERPROFILE%\\.claude) and the 'claude' command. Install Claude Code, start it once, then click Refresh.");
        }

        JsonObject root;
        try
        {
            root = ReadSettings();
        }
        catch (Exception ex)
        {
            return new ClaudeStatus(ClaudeConnectionState.SettingsUnreadable, "Claude Code's settings file couldn't be read.",
                $"{SettingsPath}: {ex.Message}. Herald won't change a file it can't parse.");
        }

        var messageDisplay = HeraldCommands(root, "MessageDisplay");
        var legacyStop = HeraldCommands(root, "Stop");

        var notes = new List<string>();
        if (legacyStop.Count > 0)
        {
            notes.Add("A Stop hook also sends to Herald, so replies may be spoken twice. Connecting again removes it.");
        }

        var own = messageDisplay.Where(IsOwnHook).ToList();
        var others = messageDisplay.Except(own).ToList();
        if (others.Count > 0 && own.Count > 0)
        {
            notes.Add("Another hook also sends to Herald (" + string.Join(", ", others.Select(ScriptPathOf)) +
                      "), so replies may be spoken twice. Reconnect removes it.");
        }

        if (own.Count > 0)
        {
            if (!File.Exists(_hookScriptPath))
            {
                return new ClaudeStatus(ClaudeConnectionState.NotConnected, "Claude Code is not connected to Herald.",
                    $"Claude Code points to Herald's hook script, but the file is missing ({_hookScriptPath}). Click Connect to restore it.");
            }

            return new ClaudeStatus(ClaudeConnectionState.Connected, "Claude Code is connected to Herald.",
                string.Join(" ", new[] { $"Using Herald's own hook: {_hookScriptPath}" }.Concat(notes)));
        }

        if (others.Count > 0)
        {
            return new ClaudeStatus(ClaudeConnectionState.ConnectedThroughOtherScript,
                "Claude Code sends to Herald through a script Herald doesn't manage.",
                string.Join(" ", new[]
                {
                    "Current hook: " + string.Join(", ", others.Select(ScriptPathOf)) + ".",
                    "It works, but Herald can't keep it up to date. Click Connect to switch to Herald's own hook."
                }.Concat(notes)));
        }

        var detail = "Claude Code is installed, but its replies aren't sent to Herald yet.";
        if (legacyStop.Count > 0) detail = "Only the older Stop hook is wired, which speaks the whole reply at the end of a turn.";
        return new ClaudeStatus(ClaudeConnectionState.NotConnected, "Claude Code is not connected to Herald.",
            string.Join(" ", new[] { detail }.Concat(notes)));
    }

    private bool IsOwnHook(string command) => command.Contains(_hookScriptPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When Claude Code uses Herald's own hook, rewrites the script so it always matches
    /// this version of Herald. Never touches Claude Code's settings.
    /// </summary>
    public void UpdateHookScriptIfConnected()
    {
        try
        {
            MoveHookFromLegacyPath();
            if (HeraldCommands(ReadSettings(), "MessageDisplay").Any(IsOwnHook)) WriteHookScript();
        }
        catch
        {
            // unreadable settings - nothing to update
        }
    }

    /// <summary>
    /// Herald's hook used to live under its old name's data folder. If Claude Code still
    /// points there, repoint that same hook to the current script (backing up the settings).
    /// </summary>
    private void MoveHookFromLegacyPath()
    {
        var legacyScript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        App.LegacyName, "integrations", "claude-hook.ps1");
        if (string.Equals(legacyScript, _hookScriptPath, StringComparison.OrdinalIgnoreCase)) return;

        var root = ReadSettings();
        var legacyHooks = (root["hooks"]?["MessageDisplay"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .SelectMany(group => group["hooks"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(hook => hook["command"]?.GetValue<string>() is { } c && c.Contains(legacyScript, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (legacyHooks.Count == 0) return;

        Backup();
        foreach (var hook in legacyHooks) hook["command"] = HookCommand;
        WriteSettings(root);
        WriteHookScript();
    }

    /// <summary>
    /// Points Claude Code's MessageDisplay hook at Herald's hook script, replacing any other
    /// hooks that send to Herald (so nothing is spoken twice). Returns the backup path.
    /// </summary>
    public string Connect()
    {
        WriteHookScript();

        var root = ReadSettings();
        var backup = Backup();

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        RemoveHeraldHooks(hooks, "MessageDisplay");
        RemoveHeraldHooks(hooks, "Stop");

        var messageDisplay = hooks["MessageDisplay"] as JsonArray ?? new JsonArray();
        hooks["MessageDisplay"] = messageDisplay;
        messageDisplay.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = HookCommand,
                    ["async"] = true,
                    ["timeout"] = 10
                }
            }
        });

        WriteSettings(root);
        return backup;
    }

    /// <summary>Removes every Claude Code hook that sends to Herald. Returns the backup path.</summary>
    public string Disconnect()
    {
        var root = ReadSettings();
        var backup = Backup();

        if (root["hooks"] is JsonObject hooks)
        {
            RemoveHeraldHooks(hooks, "MessageDisplay");
            RemoveHeraldHooks(hooks, "Stop");
            if (hooks.Count == 0) root.Remove("hooks");
        }

        WriteSettings(root);
        return backup;
    }

    private JsonObject ReadSettings()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();

        var text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return node as JsonObject ?? throw new InvalidDataException("the top level is not a JSON object");
    }

    private void WriteSettings(JsonObject root)
    {
        Directory.CreateDirectory(_claudeDir);
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private string Backup()
    {
        if (File.Exists(SettingsPath)) File.Copy(SettingsPath, BackupPath, overwrite: true);
        return BackupPath;
    }

    /// <summary>Commands under an event that send to Herald: Herald's own hook, or any script mentioning its port.</summary>
    private List<string> HeraldCommands(JsonObject root, string eventName) =>
        (root["hooks"]?[eventName] as JsonArray ?? [])
            .OfType<JsonObject>()
            .SelectMany(group => group["hooks"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(hook => hook["command"]?.GetValue<string>())
            .OfType<string>()
            .Where(IsHeraldCommand)
            .ToList();

    private void RemoveHeraldHooks(JsonObject hooks, string eventName)
    {
        if (hooks[eventName] is not JsonArray groups) return;

        foreach (var group in groups.OfType<JsonObject>().ToList())
        {
            if (group["hooks"] is not JsonArray list) continue;

            foreach (var hook in list.OfType<JsonObject>().ToList())
            {
                if (hook["command"]?.GetValue<string>() is { } command && IsHeraldCommand(command))
                {
                    list.Remove(hook);
                }
            }

            if (list.Count == 0) groups.Remove(group);
        }

        if (groups.Count == 0) hooks.Remove(eventName);
    }

    private bool IsHeraldCommand(string command)
    {
        if (IsOwnHook(command)) return true;

        // A hand-made hook (like the original stream-response.ps1) counts if its script talks to Herald's port.
        var script = ScriptPathOf(command);
        try
        {
            return File.Exists(script) && File.ReadAllText(script).Contains(HookServer.Port.ToString());
        }
        catch
        {
            return false;
        }
    }

    private static string ScriptPathOf(string command)
    {
        var match = ScriptPathInCommand.Match(command);
        if (!match.Success) return string.Empty;
        var path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return Environment.ExpandEnvironmentVariables(path.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), exe))
            .FirstOrDefault(File.Exists);

    /// <summary>Herald owns this script and rewrites it on every connect, so it stays in sync.</summary>
    private void WriteHookScript()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_hookScriptPath)!);
        var script = $$"""
            # Herald hook for Claude Code (MessageDisplay). Written by Herald; changes are overwritten.
            # Forwards each block of Claude's reply to Herald on 127.0.0.1:{{HookServer.Port}}, tagged "claude".
            $ErrorActionPreference = "SilentlyContinue"

            # PowerShell 5.1 reads stdin with the OEM codepage by default, which mangles non-ASCII text.
            [Console]::InputEncoding = [System.Text.Encoding]::UTF8

            $stdin = [Console]::In.ReadToEnd()
            if (-not $stdin) { exit 0 }
            try { $hookInput = $stdin | ConvertFrom-Json } catch { exit 0 }

            $delta = $hookInput.delta
            if (-not $delta -or -not $delta.Trim()) { exit 0 }

            # Must not write to stdout: MessageDisplay treats hook output as replacement display text.
            try {
                $client = New-Object System.Net.Sockets.TcpClient
                $connect = $client.ConnectAsync("127.0.0.1", {{HookServer.Port}})
                if ($connect.Wait(500) -and $client.Connected) {
                    $stream = $client.GetStream()
                    $stream.ReadTimeout = 3000
                    $json = ([ordered]@{ type = "speak"; text = $delta; sender = "claude" } | ConvertTo-Json -Compress) + "`n"
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
                    $stream.Write($bytes, 0, $bytes.Length)
                    (New-Object System.IO.StreamReader($stream)).ReadLine() | Out-Null
                }
                $client.Close()
            } catch {}

            exit 0
            """;
        File.WriteAllText(_hookScriptPath, script, new UTF8Encoding(false));
    }
}
