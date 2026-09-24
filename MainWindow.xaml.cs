using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Herald.Models;
using Herald.Services;

namespace Herald;

public partial class MainWindow : Window
{
    private readonly SpeechEngine _engine;
    private readonly HookServer _hookServer;
    private readonly HotkeySettingsStore _hotkeySettings;
    private readonly HotkeyManager _hotkeyManager;
    private readonly ClaudeCodeIntegration _claude;

    public MainWindow(SpeechEngine engine, HookServer hookServer, HotkeySettingsStore hotkeySettings, ClaudeCodeIntegration claude)
    {
        InitializeComponent();

        _engine = engine;
        _hookServer = hookServer;
        _hotkeySettings = hotkeySettings;
        _claude = claude;
        DataContext = _engine;
        ContentRendered += (_, _) => OfferClaudeConnection();

        SpeedSlider.Value = _engine.SpeedPercent;
        EnabledToggle.IsChecked = _engine.Enabled;
        UpdateEnabledButton();

        _hotkeyManager = new HotkeyManager(this, _engine, _hotkeySettings);
        var clipboardWatcher = new ClipboardWatcher(this, _engine);

        _hookServer.CommandReceived += OnCommandReceived;
        _hookServer.ShowRequested += () => Dispatcher.BeginInvoke(BringToFront);
        // Enabled/SpeedPercent can also change via a global hotkey, which never goes
        // through HookServer - keep the GUI in sync regardless of which path fired.
        _engine.PropertyChanged += OnEnginePropertyChanged;
        Closed += (_, _) =>
        {
            _hotkeyManager.Dispose();
            clipboardWatcher.Dispose();
        };
    }

    /// <summary>
    /// At startup: keep Herald's hook script current, and if Claude Code isn't using it
    /// (not wired at all, or wired through some other script), ask once.
    /// </summary>
    private void OfferClaudeConnection()
    {
        _claude.UpdateHookScriptIfConnected();
        if (!_engine.AskToConnectClaude) return;

        var status = _claude.GetStatus();
        if (status.State is not (ClaudeConnectionState.NotConnected or ClaudeConnectionState.ConnectedThroughOtherScript)) return;

        var dialog = new ClaudeConnectDialog(status.Summary, status.Detail) { Owner = this };
        dialog.ShowDialog();

        switch (dialog.Choice)
        {
            case ClaudeConnectChoice.Connect:
                ConnectClaude(this, _claude);
                break;
            case ClaudeConnectChoice.DontAskAgain:
                _engine.AskToConnectClaude = false;
                break;
        }
    }

    /// <summary>Connects Claude Code and reports the result; shared with the settings page.</summary>
    public static bool ConnectClaude(Window owner, ClaudeCodeIntegration claude)
    {
        try
        {
            var backup = claude.Connect();
            MessageBox.Show(owner,
                "Claude Code is now connected to Herald.\n\n" +
                "New Claude Code sessions pick this up right away; an open session may need a restart.\n\n" +
                $"The previous settings were saved as:\n{backup}",
                "Connected", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Couldn't update Claude Code's settings:\n\n" + ex.Message,
                "Connect failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>Shows the window again, e.g. when Herald is started a second time.</summary>
    private void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnCommandReceived(string type)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LastCommandText.Text = $"Last command: {type} at {DateTime.Now:T}";
        });
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SpeechEngine.Enabled) && e.PropertyName != nameof(SpeechEngine.SpeedPercent)) return;

        Dispatcher.BeginInvoke(() =>
        {
            EnabledToggle.IsChecked = _engine.Enabled;
            SpeedSlider.Value = _engine.SpeedPercent;
            SpeedLabel.Text = $"{_engine.SpeedPercent}%";
            UpdateEnabledButton();
        });
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        _engine.Enabled = EnabledToggle.IsChecked == true;
        _engine.Announce(_engine.Enabled ? "Activated" : "Off");
        UpdateEnabledButton();
    }

    private void UpdateEnabledButton()
    {
        EnabledToggle.Content = _engine.Enabled ? "Speech: ON" : "Speech: OFF";
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.Skip();
    }

    private void SkipMessageButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.SkipMessage();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Fires during InitializeComponent() (Slider min/max coercion) before the constructor
        // has assigned _engine - ignore those spurious early events.
        if (_engine == null) return;

        _engine.SpeedPercent = (int)e.NewValue;
        SpeedLabel.Text = $"{_engine.SpeedPercent}%";
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // Only double-clicks on an item count (not on the scrollbar), and not while that
        // item is in text-selection mode, where double-click selects a word instead.
        var container = ItemsControl.ContainerFromElement(HistoryList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (container?.DataContext is not QueueItem item || item.IsSelectingText) return;

        _engine.Requeue(item);
    }

    private void ItemList_KeyDown(object sender, KeyEventArgs e)
    {
        // In text-selection mode the text box handles Ctrl+C itself (copying just the
        // selection) and marks the key handled, so this only runs for a selected item.
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (sender is not ListBox { SelectedItem: QueueItem item } || item.IsSelectingText) return;

        Clipboard.SetText(item.Text);
        e.Handled = true;
    }

    private static QueueItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as QueueItem;

    private void PlayAgain_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _engine.Requeue(item);
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Clipboard.SetText(item.Text);
    }

    private void SelectText_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;

        item.IsSelectingText = true;

        // The text box only becomes visible after the next layout pass; focus it then.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var container = QueueList.ItemContainerGenerator.ContainerFromItem(item) as DependencyObject
                            ?? HistoryList.ItemContainerGenerator.ContainerFromItem(item) as DependencyObject;
            if (container != null && FindVisualChild<TextBox>(container, tb => tb.IsVisible) is { } textBox)
            {
                textBox.Focus();
            }
        });
    }

    private void SelectableText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Stay in selection mode while the text box's own right-click menu (Copy) is open,
        // or while another app has focus (e.g. pasting into Notepad).
        if (e.NewFocus is null or System.Windows.Controls.ContextMenu or MenuItem) return;

        if (ItemOf(sender) is { } item) item.IsSelectingText = false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && match(typed)) return typed;
            if (FindVisualChild(child, match) is { } found) return found;
        }
        return null;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_engine, _hotkeySettings, _hotkeyManager, _claude) { Owner = this };
        settingsWindow.ShowDialog();
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.ClearHistory();
    }

    private void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        EnqueueManualText();
    }

    private void ManualSpeakBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;

        EnqueueManualText();
        e.Handled = true;
    }

    private void EnqueueManualText()
    {
        var text = ManualSpeakBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        _engine.EnqueueText(text, "herald");
        ManualSpeakBox.Clear();
    }
}
