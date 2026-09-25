using System;
using System.IO;
using System.Linq;
using System.Windows;
using Herald.Services;
using Herald.Services.Engines;

namespace Herald;

public partial class CleanupWindow : Window
{
    private readonly SpeechEngine _engine;
    private readonly EngineRegistry _engines;
    private readonly HookServer _hookServer;
    private readonly ClaudeCodeIntegration _claude;
    private readonly Action _exitHerald;

    public CleanupWindow(SpeechEngine engine, HookServer hookServer, ClaudeCodeIntegration claude, Action exitHerald)
    {
        InitializeComponent();

        _engine = engine;
        _engines = engine.Engines;
        _hookServer = hookServer;
        _claude = claude;
        _exitHerald = exitHerald;

        var data = engine.AppDataDir;
        var hookState = claude.GetStatus().State;
        var hookConnected = hookState is ClaudeConnectionState.Connected or ClaudeConnectionState.ConnectedThroughOtherScript;

        ClaudeHookBox.Content = "Herald's hook in Claude Code's settings" + (hookConnected ? " (currently connected)" : " (not connected now)");
        ClaudeHookBox.IsEnabled = hookConnected || Directory.Exists(Path.Combine(data, "integrations"));
        ClaudeHookBox.IsChecked = ClaudeHookBox.IsEnabled;

        ClaudeBackupBox.Content = "The backup of Claude Code's settings (settings.json.herald-backup)";
        ClaudeBackupBox.IsEnabled = File.Exists(claude.BackupPath);
        ClaudeBackupBox.IsChecked = ClaudeBackupBox.IsEnabled;

        StartupBox.Content = "Start-with-Windows entry" + (StartupRegistration.IsEnabled ? " (currently on)" : " (currently off)");
        StartupBox.IsEnabled = StartupRegistration.IsEnabled;
        StartupBox.IsChecked = StartupBox.IsEnabled;

        var historySize = HeraldCleanup.FolderSize(Path.Combine(data, "history"));
        HistoryBox.Content = $"History: {engine.History.Count} items, {HeraldCleanup.Describe(historySize)} of audio";

        var enginesSize = HeraldCleanup.FolderSize(Path.Combine(data, "engines"));
        EnginesBox.Content = $"Downloaded engines (Kokoro, Piper): {HeraldCleanup.Describe(enginesSize)}";
        EnginesBox.IsEnabled = enginesSize > 0;
        EnginesBox.IsChecked = EnginesBox.IsEnabled;

        SettingsBox.Content = "Settings: senders and voices, language profiles, hotkeys, volume and other options";
        LogsBox.Content = "Logs: playback log and crash log";

        DataFolderText.Text = $"Herald's data folder: {data}\nNot touched: the Herald program itself, and the old scripts in .claude\\scripts from before Herald existed.";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var options = new CleanupOptions(
            ClaudeHookBox.IsChecked == true && ClaudeHookBox.IsEnabled,
            ClaudeBackupBox.IsChecked == true && ClaudeBackupBox.IsEnabled,
            StartupBox.IsChecked == true && StartupBox.IsEnabled,
            HistoryBox.IsChecked == true,
            EnginesBox.IsChecked == true && EnginesBox.IsEnabled,
            SettingsBox.IsChecked == true,
            LogsBox.IsChecked == true);

        var confirm = MessageBox.Show(this,
            "Remove the selected items now? This cannot be undone. Herald will close afterwards.",
            "Clean up Herald's files", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        RemoveButton.IsEnabled = false;
        var log = HeraldCleanup.Run(options, _engine, _engines, _hookServer, _claude);

        MessageBox.Show(this,
            (log.Count > 0 ? string.Join(Environment.NewLine, log) : "Nothing was selected.") +
            Environment.NewLine + Environment.NewLine + "Herald will now close.",
            "Cleanup finished", MessageBoxButton.OK,
            log.Any(l => l.StartsWith("Couldn't")) ? MessageBoxImage.Warning : MessageBoxImage.Information);

        DialogResult = true;
        _exitHerald();
    }
}
