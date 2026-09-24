using System.ComponentModel;
using System.Windows.Input;

namespace Herald.Models;

public enum HotkeyAction
{
    Toggle,
    /// <summary>Skips only the part currently playing.</summary>
    Stop,
    SetSpeed,
    SkipMessage,
    SpeakClipboard
}

/// <summary>
/// A single global hotkey, registered via Win32 RegisterHotKey (not a keyboard hook -
/// Windows only tells Herald when this exact combo fires, nothing else is observed).
/// Id must stay stable across renames since it's the Win32 registration handle.
/// </summary>
public class HotkeyBinding : INotifyPropertyChanged
{
    public int Id { get; }
    public string Label { get; }
    public HotkeyAction Action { get; }
    public int? SpeedValue { get; }

    private ModifierKeys _modifiers;
    public ModifierKeys Modifiers
    {
        get => _modifiers;
        set
        {
            if (_modifiers == value) return;
            _modifiers = value;
            OnPropertyChanged(nameof(Modifiers));
            OnPropertyChanged(nameof(Display));
        }
    }

    private Key _key;
    public Key Key
    {
        get => _key;
        set
        {
            if (_key == value) return;
            _key = value;
            OnPropertyChanged(nameof(Key));
            OnPropertyChanged(nameof(Display));
        }
    }

    public string Display => _modifiers == ModifierKeys.None
        ? _key.ToString()
        : $"{_modifiers}+{_key}".Replace(", ", "+");

    public HotkeyBinding(int id, string label, HotkeyAction action, ModifierKeys modifiers, Key key, int? speedValue = null)
    {
        Id = id;
        Label = label;
        Action = action;
        SpeedValue = speedValue;
        _modifiers = modifiers;
        _key = key;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
