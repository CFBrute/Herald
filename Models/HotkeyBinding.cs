using System.Windows.Input;

namespace Herald.Models;

public enum HotkeyAction
{
    Toggle,
    /// <summary>Skips only the part currently playing.</summary>
    Stop,
    SkipMessage,
    SpeakClipboard,
    /// <summary>One step faster (see <c>SpeechEngine.SpeedStep</c>).</summary>
    SpeedUp,
    /// <summary>One step slower.</summary>
    SpeedDown
}

/// <summary>
/// A single global hotkey, registered via Win32 RegisterHotKey (not a keyboard hook -
/// Windows only tells Herald when this exact combo fires, nothing else is observed).
/// Id must stay stable across renames since it's the Win32 registration handle.
/// </summary>
public class HotkeyBinding : ObservableObject
{
    public int Id { get; }
    public string Label { get; }
    public HotkeyAction Action { get; }

    private ModifierKeys _modifiers;
    public ModifierKeys Modifiers
    {
        get => _modifiers;
        set
        {
            if (SetField(ref _modifiers, value)) OnPropertyChanged(nameof(Display));
        }
    }

    private Key _key;
    public Key Key
    {
        get => _key;
        set
        {
            if (SetField(ref _key, value)) OnPropertyChanged(nameof(Display));
        }
    }

    private string? _problem;
    /// <summary>Why the hotkey doesn't (fully) work right now, e.g. another program uses it; null when it works.</summary>
    public string? Problem
    {
        get => _problem;
        set
        {
            if (SetField(ref _problem, value)) OnPropertyChanged(nameof(Status));
        }
    }

    /// <summary>Shown in Settings next to the keys.</summary>
    public string Status => Problem ?? "Works";

    public string Display => _modifiers == ModifierKeys.None
        ? KeyName(_key)
        : $"{_modifiers}+{KeyName(_key)}".Replace(", ", "+");

    // The + and - keys are "OemPlus" and "OemMinus" to WPF.
    private static string KeyName(Key key) => key switch
    {
        Key.OemPlus => "Plus",
        Key.OemMinus => "Minus",
        _ => key.ToString()
    };

    public HotkeyBinding(int id, string label, HotkeyAction action, ModifierKeys modifiers, Key key)
    {
        Id = id;
        Label = label;
        Action = action;
        _modifiers = modifiers;
        _key = key;
    }
}
