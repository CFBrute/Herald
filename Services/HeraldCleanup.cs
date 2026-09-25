using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Herald.Services.Engines;

namespace Herald.Services;

public record CleanupOptions(
    bool ClaudeHook,
    bool ClaudeBackup,
    bool StartWithWindows,
    bool History,
    bool Engines,
    bool Settings,
    bool Logs);

/// <summary>
/// Removes what Herald has created on this PC: its data folder (settings, history,
/// downloaded engines, hook script, logs), its hook in Claude Code's settings, and its
/// start-with-Windows entry. Herald must be closed right after, or it would recreate
/// its settings files.
/// </summary>
public static class HeraldCleanup
{
    public static string[] SettingsFiles => ["engine-settings.json", "sender-settings.json", "hotkeys.json", "language-profiles.json"];
    public static string[] LogFiles => ["playback.log", "playback.old.log", "crash.log"];

    public static long FolderSize(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static string Describe(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes"
    };

    /// <summary>
    /// Performs the cleanup. Stops speech, the hook server and the engines first so no
    /// file is still open. Returns one line per thing removed (or per failure).
    /// </summary>
    public static List<string> Run(CleanupOptions options, SpeechEngine engine, EngineRegistry engines,
                                   HookServer hookServer, ClaudeCodeIntegration claude)
    {
        var log = new List<string>();
        var data = engine.AppDataDir;

        // Release everything that holds files: playback, the Kokoro server, incoming text.
        engine.Enabled = false;
        hookServer.Stop();
        engine.Dispose();
        engines.Dispose();
        Thread.Sleep(500);

        if (options.ClaudeHook)
        {
            try
            {
                claude.Disconnect();
                log.Add("Removed Herald's hook from Claude Code's settings.");
            }
            catch (Exception ex)
            {
                log.Add("Couldn't update Claude Code's settings: " + ex.Message);
            }
            DeleteDirectory(Path.Combine(data, "integrations"), "Herald's hook script", log);
        }

        if (options.ClaudeBackup) DeleteFile(claude.BackupPath, "Claude Code settings backup", log);

        if (options.StartWithWindows)
        {
            try
            {
                StartupRegistration.Disable();
                log.Add("Removed the start-with-Windows entry.");
            }
            catch (Exception ex)
            {
                log.Add("Couldn't remove the start-with-Windows entry: " + ex.Message);
            }
        }

        if (options.History)
        {
            DeleteDirectory(Path.Combine(data, "history"), "History audio", log);
            DeleteFile(Path.Combine(data, "history.json"), "History list", log);
        }

        if (options.Engines) DeleteDirectory(Path.Combine(data, "engines"), "Downloaded engines (Kokoro, Piper)", log);

        if (options.Settings)
        {
            foreach (var file in SettingsFiles) DeleteFile(Path.Combine(data, file), file, log);
        }

        if (options.Logs)
        {
            foreach (var file in LogFiles) DeleteFile(Path.Combine(data, file), file, log);
        }

        // The data folder itself goes once nothing is left in it.
        try
        {
            if (Directory.Exists(data) && !Directory.EnumerateFileSystemEntries(data).Any())
            {
                Directory.Delete(data);
                log.Add($"Removed the empty data folder {data}.");
            }
        }
        catch (Exception ex)
        {
            log.Add("Couldn't remove the data folder: " + ex.Message);
        }

        return log;
    }

    private static void DeleteFile(string path, string label, List<string> log)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
            log.Add($"Removed {label}.");
        }
        catch (Exception ex)
        {
            log.Add($"Couldn't remove {label}: {ex.Message}");
        }
    }

    private static void DeleteDirectory(string path, string label, List<string> log)
    {
        if (!Directory.Exists(path)) return;
        var size = Describe(FolderSize(path));

        // A file may still be closing (audio just stopped, a server just killed): retry once.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                log.Add($"Removed {label} ({size}).");
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _ = ex;
                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                log.Add($"Couldn't fully remove {label}: {ex.Message}");
                return;
            }
        }
    }
}
