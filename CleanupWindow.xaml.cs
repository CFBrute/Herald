using System;
using System.IO;
using System.Linq;
using System.Windows;
using Herald.Services;

namespace Herald;

public partial class CleanupWindow : Window
{
    private readonly AppServices _services;
    private readonly Action _exitHerald;

    public CleanupWindow(AppServices services, Action exitHerald)
    {
        InitializeComponent();

        _services = services;
        _exitHerald = exitHerald;

        var paths = services.Paths;
        var claude = services.Claude;
        var hookState = claude.GetStatus().State;
        var hookConnected = hookState is ClaudeConnectionState.Connected or ClaudeConnectionState.ConnectedThroughOtherScript;

        ClaudeHookBox.Content = "Herald's hook in Claude Code's settings" + (hookConnected ? " (currently connected)" : " (not connected now)");
        ClaudeHookBox.IsEnabled = hookConnected || Directory.Exists(paths.IntegrationsDir);
        ClaudeHookBox.IsChecked = ClaudeHookBox.IsEnabled;

        ClaudeBackupBox.Content = "The backup of Claude Code's settings (settings.json.herald-backup)";
        ClaudeBackupBox.IsEnabled = File.Exists(claude.BackupPath);
        ClaudeBackupBox.IsChecked = ClaudeBackupBox.IsEnabled;

        StartupBox.Content = "Start-with-Windows entry" + (StartupRegistration.IsEnabled ? " (currently on)" : " (currently off)");
        StartupBox.IsEnabled = StartupRegistration.IsEnabled;
        StartupBox.IsChecked = StartupBox.IsEnabled;

        var historySize = HeraldCleanup.FolderSize(paths.HistoryDir);
        HistoryBox.Content = $"History: {services.Speech.History.Count} items, {HeraldCleanup.Describe(historySize)} of audio";

        var enginesSize = HeraldCleanup.FolderSize(paths.EnginesDir);
        EnginesBox.Content = $"Downloaded engines (Kokoro, Piper): {HeraldCleanup.Describe(enginesSize)}";
        EnginesBox.IsEnabled = enginesSize > 0;
        EnginesBox.IsChecked = EnginesBox.IsEnabled;

        SettingsBox.Content = "Settings: senders and voices, language profiles, hotkeys, volume and other options";
        LogsBox.Content = "Logs: playback log and crash log";

        DataFolderText.Text = $"Herald's data folder: {paths.DataDir}\nNot touched: the Herald program itself, and the old scripts in .claude\\scripts from before Herald existed.";
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
        var log = HeraldCleanup.Run(options, _services);

        MessageBox.Show(this,
            (log.Count > 0 ? String.Join(Environment.NewLine, log) : "Nothing was selected.") +
            Environment.NewLine + Environment.NewLine + "Herald will now close.",
            "Cleanup finished", MessageBoxButton.OK,
            log.Any(l => l.StartsWith("Couldn't")) ? MessageBoxImage.Warning : MessageBoxImage.Information);

        DialogResult = true;
        _exitHerald();
    }
}
