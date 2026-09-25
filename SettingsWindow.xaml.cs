using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Herald.Models;
using Herald.Services;
using Herald.Services.Engines;

namespace Herald;

public partial class SettingsWindow : Window
{
    private readonly SpeechEngine _engine;
    private readonly SenderSettingsStore _senderSettings;
    private readonly HotkeySettingsStore _hotkeySettings;
    private readonly HotkeyManager _hotkeyManager;
    private readonly ClaudeCodeIntegration _claude;
    private readonly HookServer _hookServer;
    private readonly Action _exitHerald;

    // Set while the pickers are filled from code, so their SelectionChanged handlers
    // don't write half-updated values back to the sender.
    private bool _updatingPickers;

    public SettingsWindow(SpeechEngine engine, HotkeySettingsStore hotkeySettings, HotkeyManager hotkeyManager,
                          ClaudeCodeIntegration claude, HookServer hookServer, Action exitHerald)
    {
        InitializeComponent();

        _claude = claude;
        _hookServer = hookServer;
        _exitHerald = exitHerald;
        StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled;
        ThemeCombo.SelectedItem = ThemeCombo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == engine.Theme.ToString());
        ClaudeTab.DataContext = engine;
        RefreshClaudeStatus();

        _engine = engine;
        RefreshFiles();
        _senderSettings = engine.SenderSettings;
        _hotkeySettings = hotkeySettings;
        _hotkeyManager = hotkeyManager;

        DataContext = _senderSettings;
        GeneralTab.DataContext = engine;
        HotkeysGrid.ItemsSource = _hotkeySettings.Bindings;
        EngineCombo.ItemsSource = engine.Engines.All;

        WindowsDescription.Text = engine.Engines.Windows.Description;
        KokoroDescription.Text = engine.Engines.Kokoro.Description;
        PiperDescription.Text = engine.Engines.Piper.Description;
        RefreshEngineStatus();

        LanguagesGrid.ItemsSource = engine.LanguageProfiles.Profiles;
        RuleEngineCombo.ItemsSource = engine.Engines.All;
        RuleLanguageCombo.DropDownOpened += (_, _) => RefreshRuleLanguageChoices();
        RefreshRuleLanguageChoices();

        SendersGrid.SelectedIndex = 0;
    }

    // Per-sender language rules ("if Swedish is detected, use this voice")

    public record LanguageRuleView(LanguageVoiceRule Rule, string Description);

    private void LoadLanguageRules()
    {
        var sender = SelectedSender;
        LanguageRulesList.ItemsSource = sender?.LanguageRules.Select(r => new LanguageRuleView(r, Describe(r))).ToList();
    }

    private string Describe(LanguageVoiceRule rule)
    {
        var engine = _engine.Engines.Find(rule.EngineId);
        var voice = engine?.Voices.FirstOrDefault(v => v.Id == rule.VoiceId)?.DisplayName ?? rule.VoiceId;
        var text = $"If {rule.LanguageName}: {engine?.DisplayName ?? rule.EngineId} - {voice}";

        var profile = _engine.LanguageProfiles.Profiles.FirstOrDefault(p => p.Name == rule.LanguageName);
        if (profile == null) return text + "  (no such language on the Languages tab)";
        if (!profile.Enabled) return text + "  (language switched off on the Languages tab)";
        if (engine is { IsReady: false }) return text + "  (engine not installed, default voice is used)";
        return text;
    }

    private void RefreshRuleLanguageChoices()
    {
        var selected = RuleLanguageCombo.SelectedItem as string;
        var names = _engine.LanguageProfiles.Profiles.Select(p => p.Name).Where(n => n.Length > 0).Distinct().ToList();
        RuleLanguageCombo.ItemsSource = names;
        RuleLanguageCombo.SelectedItem = selected != null && names.Contains(selected) ? selected : names.FirstOrDefault();
    }

    private void LanguagesGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            RefreshRuleLanguageChoices();
            LoadLanguageRules();
        });

    private void RuleEngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var engine = RuleEngineCombo.SelectedItem as ITtsEngine;
        RuleVoiceCombo.ItemsSource = engine?.Voices;
        RuleVoiceCombo.SelectedItem = engine?.Voices.FirstOrDefault(v => v.Id == engine.DefaultVoiceId);
    }

    private void LanguageRuleAdd_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSender is not { } senderSettings) return;
        if (RuleLanguageCombo.SelectedItem is not string language
            || RuleEngineCombo.SelectedItem is not ITtsEngine engine
            || RuleVoiceCombo.SelectedItem is not VoiceInfo voice)
        {
            LanguageRuleHint.Text = "Pick a language, an engine and a voice first.";
            return;
        }

        // One rule per language: adding again replaces the old voice.
        senderSettings.LanguageRules = senderSettings.LanguageRules
            .Where(r => r.LanguageName != language)
            .Append(new LanguageVoiceRule(language, engine.Id, voice.Id))
            .ToList();
        LanguageRuleHint.Text = "Languages and how they're detected are set up on the Languages tab.";
        LoadLanguageRules();
    }

    private void LanguageRuleRemove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSender is not { } senderSettings || sender is not FrameworkElement { Tag: LanguageRuleView view }) return;

        senderSettings.LanguageRules = senderSettings.LanguageRules.Where(r => r != view.Rule).ToList();
        LoadLanguageRules();
    }

    private async void LanguageRuleTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LanguageRuleView view } button) return;

        var engine = _engine.Engines.Find(view.Rule.EngineId);
        var voice = engine?.Voices.FirstOrDefault(v => v.Id == view.Rule.VoiceId);
        if (engine is not { IsReady: true } || voice == null) return;

        button.IsEnabled = false;
        try
        {
            await _engine.PreviewAsync(engine, voice.Id, SampleTextFor(voice.Language));
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void DetectionTestBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = DetectionTestBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            DetectionResultText.Text = string.Empty;
            return;
        }

        var all = _engine.LanguageProfiles.Profiles.Where(p => p.Name.Length > 0).ToList();
        var scores = LanguageDetector.Score(text, all.Select(CompiledLanguageProfile.From));
        var lines = scores.Select(s =>
        {
            var enabled = all.First(p => p.Name == s.Profile.Name).Enabled;
            var verdict = !enabled ? "switched off" : s.Matches >= s.Profile.MinMatches ? "match" : "too few";
            return $"{s.Profile.Name}: {s.Matches} of {s.Profile.MinMatches} needed ({verdict})";
        });

        var winner = LanguageDetector.Detect(text, _engine.LanguageProfiles.Snapshot);
        DetectionResultText.Text = string.Join("   ", lines) + Environment.NewLine +
            (winner != null ? $"Result: {winner.Name}" : "Result: no profile matched, so the sender's normal voice is used");
    }

    private SenderSettings? SelectedSender => SendersGrid.SelectedItem as SenderSettings;

    private void SendersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadVoicePickers();

    private void LoadVoicePickers()
    {
        LoadLanguageRules();

        _updatingPickers = true;
        try
        {
            var selected = SelectedSender;
            if (selected == null)
            {
                EngineCombo.SelectedItem = null;
                VoiceCombo.ItemsSource = null;
                return;
            }

            var engine = _engine.Engines.Find(selected.EngineId) ?? _engine.Engines.Windows;
            EngineCombo.SelectedItem = engine;
            VoiceCombo.ItemsSource = engine.Voices;
            VoiceCombo.SelectedItem = engine.Voices.FirstOrDefault(v => v.Id == selected.VoiceId)
                                      ?? engine.Voices.FirstOrDefault(v => v.Id == engine.DefaultVoiceId);
        }
        finally
        {
            _updatingPickers = false;
        }

        UpdateEngineHint();
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPickers || SelectedSender is not { } selected || EngineCombo.SelectedItem is not ITtsEngine engine) return;

        selected.EngineId = engine.Id;
        selected.VoiceId = engine.DefaultVoiceId;
        LoadVoicePickers();
    }

    private void VoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPickers || SelectedSender is not { } selected || VoiceCombo.SelectedItem is not VoiceInfo voice) return;

        selected.VoiceId = voice.Id;
    }

    private void UpdateEngineHint()
    {
        EngineHintText.Text = EngineCombo.SelectedItem is ITtsEngine { IsReady: false } engine
            ? $"{engine.DisplayName} isn't installed yet, so the default Windows voice is used for now. See the Engines tab."
            : string.Empty;
    }

    private async void TestVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (EngineCombo.SelectedItem is not ITtsEngine engine || VoiceCombo.SelectedItem is not VoiceInfo voice) return;

        var (actualEngine, actualVoice) = engine.IsReady ? (engine, voice.Id) : _engine.Engines.Resolve(engine.Id, voice.Id);
        TestVoiceButton.IsEnabled = false;
        try
        {
            var ok = await _engine.PreviewAsync(actualEngine, actualVoice, SampleTextFor(voice.Language));
            if (!ok) EngineHintText.Text = "The test failed - the engine couldn't produce audio.";
        }
        finally
        {
            TestVoiceButton.IsEnabled = true;
        }
    }

    private static string SampleTextFor(string language) => language.ToLowerInvariant() switch
    {
        var l when l.StartsWith("sv") => "Hej! Så här låter den här rösten.",
        var l when l.StartsWith("es") => "Hola, así suena esta voz.",
        var l when l.StartsWith("fr") => "Bonjour, voici comment sonne cette voix.",
        var l when l.StartsWith("it") => "Ciao, ecco come suona questa voce.",
        var l when l.StartsWith("pt") => "Olá, é assim que esta voz soa.",
        var l when l.StartsWith("de") => "Hallo, so klingt diese Stimme.",
        var l when l.StartsWith("hi") => "नमस्ते, यह आवाज़ ऐसी सुनाई देती है।",
        var l when l.StartsWith("ja") => "こんにちは、この声はこのように聞こえます。",
        var l when l.StartsWith("cmn") || l.StartsWith("zh") => "你好，这个声音听起来是这样的。",
        _ => "Hello! This is how this voice sounds."
    };

    private void RefreshEngineStatus()
    {
        var windows = _engine.Engines.Windows;
        WindowsStatus.Text = windows.IsReady
            ? $"Ready - {windows.Voices.Count} voice(s): " + string.Join(", ", windows.Voices.Select(v => v.DisplayName))
            : "No Windows voices found. Add some in Windows Settings.";

        var kokoro = _engine.Engines.Kokoro;
        KokoroStatus.Text = kokoro.IsReady
            ? $"Ready - {kokoro.Voices.Count} voices."
            : "Not installed. Missing: " + string.Join("; ", kokoro.MissingParts);
        KokoroInstallButton.Content = kokoro.IsReady ? "Reinstall / update" : "Install";
        KokoroInstallButton.IsEnabled = !kokoro.IsInstalling;

        var piper = _engine.Engines.Piper;
        PiperStatus.Text = piper.IsReady
            ? $"Ready - {piper.Voices.Count} voice(s) installed."
            : "Not ready. Missing: " + string.Join("; ", piper.MissingParts) + ". Download a voice below to install it.";
        PiperInstalledCombo.ItemsSource = piper.Voices;
        PiperInstalledCombo.SelectedIndex = piper.Voices.Count > 0 ? 0 : -1;

        UpdateEngineHint();
        LoadLanguageRules();
    }

    private Progress<string> ShowLog(TextBox logBox)
    {
        logBox.Visibility = Visibility.Visible;
        logBox.Clear();
        return new Progress<string>(line =>
        {
            logBox.AppendText(line + Environment.NewLine);
            logBox.ScrollToEnd();
        });
    }

    private async void PiperLoadCatalog_Click(object sender, RoutedEventArgs e)
    {
        PiperLoadCatalogButton.IsEnabled = false;
        var catalog = await _engine.Engines.Piper.GetCatalogAsync(ShowLog(PiperLog), CancellationToken.None);
        PiperLoadCatalogButton.IsEnabled = true;
        if (catalog.Count == 0) return;

        PiperLog.Visibility = Visibility.Collapsed;
        PiperCatalogCombo.ItemsSource = catalog;
        PiperCatalogCombo.IsEnabled = true;
        PiperDownloadButton.IsEnabled = true;

        // Start on a voice in the Windows display language, preferring medium quality.
        var culture = CultureInfo.CurrentUICulture.Name.Replace('-', '_');
        PiperCatalogCombo.SelectedItem =
            catalog.FirstOrDefault(v => v.Key.StartsWith(culture + "-") && v.Key.EndsWith("-medium"))
            ?? catalog.FirstOrDefault(v => v.Key.StartsWith(culture + "-"))
            ?? catalog.FirstOrDefault(v => v.Key == "en_US-lessac-medium")
            ?? catalog[0];
    }

    private async void PiperDownload_Click(object sender, RoutedEventArgs e)
    {
        if (PiperCatalogCombo.SelectedItem is not PiperCatalogVoice voice) return;

        var piper = _engine.Engines.Piper;
        var programNote = piper.IsProgramInstalled ? "" : "\n\nThe Piper program (about 20 MB) is downloaded first.";
        var confirm = MessageBox.Show(this,
            $"Download the voice \"{voice.Display}\" into Herald's folder?{programNote}",
            "Download Piper voice", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        PiperDownloadButton.IsEnabled = false;
        await piper.InstallVoiceAsync(voice, ShowLog(PiperLog), CancellationToken.None);
        PiperDownloadButton.IsEnabled = true;

        RefreshEngineStatus();
        LoadVoicePickers();
    }

    private void PiperRemove_Click(object sender, RoutedEventArgs e)
    {
        if (PiperInstalledCombo.SelectedItem is not VoiceInfo voice) return;

        var confirm = MessageBox.Show(this, $"Remove the Piper voice \"{voice.DisplayName}\"?",
            "Remove voice", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _engine.Engines.Piper.RemoveVoice(voice.Id);
        RefreshEngineStatus();
        LoadVoicePickers();
    }

    private void RefreshEngines_Click(object sender, RoutedEventArgs e)
    {
        foreach (var engine in _engine.Engines.All) engine.Refresh();
        RefreshEngineStatus();
        LoadVoicePickers();
    }

    private void OpenWindowsSpeechSettings_Click(object sender, RoutedEventArgs e) =>
        WindowsTtsEngine.OpenWindowsSpeechSettings();

    private async void KokoroInstall_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "This installs Kokoro into Herald's own folder:\n\n" +
            "- a private Python environment with kokoro-onnx (about 150 MB, needs Python 3.10+ already installed)\n" +
            "- the Kokoro model and voice files (about 350 MB, downloaded from GitHub unless found locally)\n\n" +
            "Continue?",
            "Install Kokoro", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        KokoroInstallButton.IsEnabled = false;
        await _engine.Engines.Kokoro.InstallAsync(ShowLog(KokoroLog), CancellationToken.None);

        RefreshEngineStatus();
        LoadVoicePickers();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires once while the window is being built, before _engine is set.
        if (_engine == null || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

        var choice = Enum.Parse<ThemeChoice>(tag);
        if (choice == _engine.Theme) return;
        _engine.Theme = choice;
        ThemeManager.Apply(choice);
    }

    // Checked/Unchecked (not Click) so keyboard and accessibility tools change the setting too.
    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        var wanted = StartWithWindowsBox.IsChecked == true;
        if (wanted == StartupRegistration.IsEnabled) return;

        try
        {
            if (wanted) StartupRegistration.Enable();
            else StartupRegistration.Disable();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't change the startup setting:\n\n" + ex.Message,
                "Start with Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
            StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled;
        }
    }

    public record FileEntry(string Label, string Path, bool IsFolder)
    {
        public bool Exists => IsFolder ? Directory.Exists(Path) : File.Exists(Path);
        public string Status => Exists ? Path : "Not created yet";
    }

    private void RefreshFiles()
    {
        var data = _engine.AppDataDir;
        FilesList.ItemsSource = new List<FileEntry>
        {
            new("Claude Code settings", _claude.SettingsPath, false),
            new("Claude Code settings backup", _claude.BackupPath, false),
            new("Herald's Claude hook script", _claude.HookScriptPath, false),
            new("Herald settings", Path.Combine(data, "engine-settings.json"), false),
            new("Sender rules", Path.Combine(data, "sender-settings.json"), false),
            new("Hotkeys", Path.Combine(data, "hotkeys.json"), false),
            new("History list", Path.Combine(data, "history.json"), false),
            new("Crash log", Path.Combine(data, "crash.log"), false),
            new("Playback log", Path.Combine(data, "playback.log"), false),
            new("Herald data folder", data, true),
            new("History audio folder", _engine.HistoryDir, true),
            new("Engines folder (Kokoro, Piper)", Path.Combine(data, "engines"), true)
        };
    }

    private void RefreshFiles_Click(object sender, RoutedEventArgs e) => RefreshFiles();

    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CleanupWindow(_engine, _hookServer, _claude, _exitHerald) { Owner = this };
        dialog.ShowDialog();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FileEntry entry }) OpenPath(entry.Path, entry.IsFolder);
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FileEntry entry }) return;

        if (entry.Exists)
        {
            StartProcess("explorer.exe", $"/select,\"{entry.Path}\"");
        }
        else if (System.IO.Path.GetDirectoryName(entry.Path) is { } parent && Directory.Exists(parent))
        {
            StartProcess("explorer.exe", $"\"{parent}\"");
        }
    }

    private void ClaudeOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_claude.SettingsPath)) OpenPath(_claude.SettingsPath, isFolder: false);
    }

    /// <summary>
    /// Opens a file for review. Scripts go to Notepad explicitly, so a double-click-style
    /// open can never execute them; other files use their default app, or Notepad if none.
    /// </summary>
    private static void OpenPath(string path, bool isFolder)
    {
        if (isFolder)
        {
            StartProcess("explorer.exe", $"\"{path}\"");
            return;
        }

        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".ps1" or ".py" or ".cmd" or ".bat" or ".js" or ".log" or ".herald-backup")
        {
            StartProcess("notepad.exe", $"\"{path}\"");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            StartProcess("notepad.exe", $"\"{path}\"");
        }
    }

    private static void StartProcess(string exe, string arguments)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments) { UseShellExecute = true });
        }
        catch { }
    }

    private void RefreshClaudeStatus()
    {
        var status = _claude.GetStatus();
        ClaudeSummary.Text = status.Summary;
        // A resource reference (not a fixed brush) so the colour follows light/dark switches.
        ClaudeSummary.SetResourceReference(TextBlock.ForegroundProperty,
            status.State == ClaudeConnectionState.Connected ? "SuccessText" : "WarningText");
        ClaudeDetail.Text = status.Detail;

        ClaudeConnectButton.IsEnabled = status.State is ClaudeConnectionState.NotConnected
            or ClaudeConnectionState.ConnectedThroughOtherScript or ClaudeConnectionState.Connected;
        ClaudeConnectButton.Content = status.State == ClaudeConnectionState.Connected ? "Reconnect" : "Connect";
        ClaudeDisconnectButton.IsEnabled = status.State is ClaudeConnectionState.Connected
            or ClaudeConnectionState.ConnectedThroughOtherScript;
    }

    private void ClaudeRefresh_Click(object sender, RoutedEventArgs e) => RefreshClaudeStatus();

    private void ClaudeConnect_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.ConnectClaude(this, _claude);
        RefreshClaudeStatus();
        RefreshFiles();
    }

    private void ClaudeDisconnect_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Remove the hook, so Claude Code stops sending its replies to Herald?",
            "Disconnect Claude Code", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var backup = _claude.Disconnect();
            MessageBox.Show(this, $"Disconnected. The previous settings were saved as:\n{backup}",
                "Disconnected", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't update Claude Code's settings:\n\n" + ex.Message,
                "Disconnect failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshClaudeStatus();
        RefreshFiles();
    }

    private void AddSenderButton_Click(object sender, RoutedEventArgs e)
    {
        AddSenderFromBox();
    }

    private void NewSenderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        AddSenderFromBox();
        e.Handled = true;
    }

    private void AddSenderFromBox()
    {
        var tag = NewSenderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(tag)) return;

        _senderSettings.GetOrCreate(tag);
        NewSenderBox.Clear();
    }

    private void RebindButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotkeyBinding binding }) return;

        var capture = new HotkeyCaptureWindow { Owner = this };
        if (capture.ShowDialog() != true) return;

        var ok = _hotkeyManager.Rebind(binding, capture.CapturedModifiers, capture.CapturedKey);
        if (!ok)
        {
            MessageBox.Show(this,
                "That combination is already in use (by Herald or another app) - keeping the previous binding.",
                "Hotkey unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
