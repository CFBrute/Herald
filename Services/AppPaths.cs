using System;
using System.Diagnostics;
using System.IO;

namespace Herald.Services;

/// <summary>
/// Where Herald keeps its files. Everything lives in one data folder under
/// %LOCALAPPDATA%, so the settings page, the cleanup dialog and the services all agree.
/// </summary>
public class AppPaths
{
    /// <summary>Herald's name before it was renamed; its data folder is moved over once.</summary>
    public const string LegacyName = "ClaudeSpeechService";

    public string DataDir { get; }
    public string HistoryDir => Path.Combine(DataDir, "history");
    public string EnginesDir => Path.Combine(DataDir, "engines");
    public string IntegrationsDir => Path.Combine(DataDir, "integrations");
    public string ClaudeHookScript => Path.Combine(IntegrationsDir, "claude-hook.ps1");

    public string AppSettingsFile => Path.Combine(DataDir, "engine-settings.json");
    public string SenderSettingsFile => Path.Combine(DataDir, "sender-settings.json");
    public string HotkeysFile => Path.Combine(DataDir, "hotkeys.json");
    public string LanguageProfilesFile => Path.Combine(DataDir, "language-profiles.json");
    public string HistoryFile => Path.Combine(DataDir, "history.json");
    /// <summary>Where Herald says which port it listens on, while it does (see HookServer).</summary>
    public string EndpointFile => Path.Combine(DataDir, "endpoint.json");

    public string CrashLog => Path.Combine(DataDir, "crash.log");
    public string PlaybackLog => Path.Combine(DataDir, "playback.log");
    public string PlaybackOldLog => Path.Combine(DataDir, "playback.old.log");
    public string HeraldLog => Path.Combine(DataDir, "herald.log");
    public string HeraldOldLog => Path.Combine(DataDir, "herald.old.log");

    public string[] SettingsFiles => [AppSettingsFile, SenderSettingsFile, HotkeysFile, LanguageProfilesFile];
    public string[] LogFiles => [HeraldLog, HeraldOldLog, PlaybackLog, PlaybackOldLog, CrashLog];

    public AppPaths(string dataDir)
    {
        DataDir = dataDir;
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(HistoryDir);
    }

    /// <summary>Herald's data folder, moved over from the legacy name the first time.</summary>
    public static AppPaths ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(UseDataFolder(Path.Combine(localAppData, "Herald"), Path.Combine(localAppData, LegacyName)));
    }

    /// <summary>
    /// Moves the old data folder (settings, history, installed engines) to Herald's folder
    /// the first time. If that fails, keeps using the old folder rather than starting empty.
    /// </summary>
    private static string UseDataFolder(string appDataDir, string legacyDir)
    {
        if (Directory.Exists(appDataDir) || !Directory.Exists(legacyDir)) return appDataDir;

        try
        {
            // A Kokoro server still running from the old folder would keep its files locked.
            foreach (var process in Process.GetProcessesByName("pythonw"))
            {
                try
                {
                    if (process.MainModule?.FileName.StartsWith(legacyDir, StringComparison.OrdinalIgnoreCase) == true) process.Kill();
                }
                catch { }
            }

            Directory.Move(legacyDir, appDataDir);
            return appDataDir;
        }
        catch
        {
            return legacyDir;
        }
    }
}
