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
    private readonly AppServices _services;
    private readonly SpeechEngine _engine;
    private readonly HotkeyManager _hotkeyManager;
    private readonly TrayIcon _tray;
    private SettingsWindow? _settingsWindow;

    // Set when Herald should really quit (Exit in the tray menu, or Windows signing out);
    // otherwise closing the window only hides it to the tray.
    private bool _exiting;

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        // Once the window handle exists its DPI is known, so the clamp to the screen's
        // work area is right on high-scaling displays too.
        SourceInitialized += (_, _) => this.FitToScreen();

        _services = services;
        _engine = services.Speech;
        DataContext = _engine;
        StatusText.Text = $"Listening on 127.0.0.1:{HookServer.Port}";
        ContentRendered += (_, _) => OfferClaudeConnection();

        _hotkeyManager = new HotkeyManager(this, _engine, services.Hotkeys);
        var clipboardWatcher = new ClipboardWatcher(this, _engine, services.Settings);

        services.HookServer.ShowRequested += () => Dispatcher.BeginInvoke(BringToFront);
        // Enabled can also change via a global hotkey or the hook server - keep the GUI
        // in sync regardless of which path fired.
        _engine.PropertyChanged += OnEnginePropertyChanged;
        this.FollowUiScale(Root, services.Settings);

        _tray = new TrayIcon();
        _tray.OpenRequested += BringToFront;
        _tray.ToggleSpeechRequested += _engine.ToggleEnabled;
        _tray.SettingsRequested += () =>
        {
            BringToFront();
            OpenSettings();
        };
        _tray.ExitRequested += ExitHerald;
        UpdateEnabledButton();

        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            Hide();
            if (!services.Settings.TrayHintShown)
            {
                _tray.ShowStillRunningHint();
                services.Settings.TrayHintShown = true;
            }
        };
        // Never block Windows from signing out or shutting down.
        Application.Current.SessionEnding += (_, _) => _exiting = true;

        Closed += (_, _) =>
        {
            _engine.PropertyChanged -= OnEnginePropertyChanged;
            _hotkeyManager.Dispose();
            clipboardWatcher.Dispose();
            _tray.Dispose();
        };
    }

    /// <summary>Really quits Herald (the tray menu's Exit).</summary>
    private void ExitHerald()
    {
        _exiting = true;
        Application.Current.Shutdown();
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_services, _hotkeyManager, ExitHerald) { Owner = this };
        try
        {
            _settingsWindow.ShowDialog();
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    /// <summary>
    /// At startup: keep Herald's hook script current, and if Claude Code isn't using it
    /// (not wired at all, or wired through some other script), ask once.
    /// </summary>
    private void OfferClaudeConnection()
    {
        var claude = _services.Claude;
        claude.UpdateHookScriptIfConnected();
        if (!_services.Settings.AskToConnectClaude) return;

        var status = claude.GetStatus();
        if (status.State is not (ClaudeConnectionState.NotConnected or ClaudeConnectionState.ConnectedThroughOtherScript)) return;

        var dialog = new ClaudeConnectDialog(status.Summary, status.Detail) { Owner = this };
        dialog.ShowDialog();

        switch (dialog.Choice)
        {
            case ClaudeConnectChoice.Connect:
                ClaudeConnectDialog.Connect(this, claude);
                break;
            case ClaudeConnectChoice.DontAskAgain:
                _services.Settings.AskToConnectClaude = false;
                break;
        }
    }

    /// <summary>Shows the window again, e.g. when Herald is started a second time.</summary>
    private void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpeechEngine.Enabled)) Dispatcher.BeginInvoke(UpdateEnabledButton);
    }

    private void EnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        _engine.SetEnabled(EnabledToggle.IsChecked == true);
        UpdateEnabledButton();
    }

    /// <summary>Shows the current on/off state on the toggle button and in the tray menu.</summary>
    private void UpdateEnabledButton()
    {
        EnabledToggle.IsChecked = _engine.Enabled;
        EnabledToggle.Content = _engine.Enabled ? "Speech: ON" : "Speech: OFF";
        _tray.SetSpeechEnabled(_engine.Enabled);
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.Skip();
    }

    private void SkipMessageButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.SkipMessage();
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();

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
