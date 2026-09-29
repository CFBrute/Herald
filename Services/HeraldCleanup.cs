using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

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
    public static List<string> Run(CleanupOptions options, AppServices services)
    {
        var log = new List<string>();
        var paths = services.Paths;
        var claude = services.Claude;

        // Release everything that holds files: playback, the Kokoro server, incoming text.
        services.Speech.Enabled = false;
        services.HookServer.Stop();
        services.Speech.Dispose();
        services.Engines.Dispose();
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
            DeleteDirectory(paths.IntegrationsDir, "Herald's hook script", log);
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
            DeleteDirectory(paths.HistoryDir, "History audio", log);
            DeleteFile(paths.HistoryFile, "History list", log);
        }

        if (options.Engines) DeleteDirectory(paths.EnginesDir, "Downloaded engines (Kokoro, Piper)", log);

        if (options.Settings)
        {
            foreach (var file in paths.SettingsFiles) DeleteFile(file, Path.GetFileName(file), log);
        }

        if (options.Logs)
        {
            foreach (var file in paths.LogFiles) DeleteFile(file, Path.GetFileName(file), log);
        }

        // The data folder itself goes once nothing is left in it.
        try
        {
            if (Directory.Exists(paths.DataDir) && !Directory.EnumerateFileSystemEntries(paths.DataDir).Any())
            {
                Directory.Delete(paths.DataDir);
                log.Add($"Removed the empty data folder {paths.DataDir}.");
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
            catch when (attempt < 3)
            {
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
