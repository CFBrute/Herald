using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Herald.Models;

namespace Herald.Services;

/// <summary>
/// Persists the configurable global hotkeys (default: Alt+Shift+S toggle, Alt+Shift+Q
/// skip part, Alt+Shift+W skip message, Alt+Shift+Plus/Minus speed) so rebinds survive a restart.
/// </summary>
public class HotkeySettingsStore
{
    private readonly string _filePath;

    public ObservableCollection<HotkeyBinding> Bindings { get; } = [];

    public HotkeySettingsStore(string filePath)
    {
        _filePath = filePath;

        // The set of actions always comes from the defaults; the file only overrides the
        // keys. That way actions added in newer versions show up for existing users.
        foreach (var binding in DefaultBindings())
        {
            Bindings.Add(binding);
        }
        ApplySavedKeys();
        Save();

        foreach (var b in Bindings)
        {
            b.PropertyChanged += (_, _) => Save();
        }
    }

    private static IEnumerable<HotkeyBinding> DefaultBindings()
    {
        const ModifierKeys mods = ModifierKeys.Alt | ModifierKeys.Shift;

        yield return new HotkeyBinding(1, "Toggle speech", HotkeyAction.Toggle, mods, Key.S);
        yield return new HotkeyBinding(2, "Skip current part", HotkeyAction.Stop, mods, Key.Q);
        yield return new HotkeyBinding(3, "Skip whole message", HotkeyAction.SkipMessage, mods, Key.W);
        yield return new HotkeyBinding(4, "Speak selected text (copies it first)", HotkeyAction.SpeakClipboard, mods, Key.C);
        // Ids 10-19 were the old fixed speeds (Alt+Shift+0-9); saved keys for them are ignored.
        // On + and -, the numpad + and - work too (see HotkeyManager).
        yield return new HotkeyBinding(5, "Faster (10% up)", HotkeyAction.SpeedUp, mods, Key.OemPlus);
        yield return new HotkeyBinding(6, "Slower (10% down)", HotkeyAction.SpeedDown, mods, Key.OemMinus);
    }

    private void ApplySavedKeys()
    {
        // A missing or corrupt file keeps the defaults.
        foreach (var dto in SafeFile.ReadJson<List<HotkeyDto>>(_filePath) ?? [])
        {
            var binding = Bindings.FirstOrDefault(b => b.Id == dto.Id);
            if (binding == null) continue;
            binding.Modifiers = (ModifierKeys)dto.Modifiers;
            binding.Key = (Key)dto.Key;
        }
    }

    private void Save() =>
        SafeFile.WriteJson(_filePath,
                           Bindings.Select(b => new HotkeyDto(b.Id, b.Label, b.Action.ToString(), (int)b.Modifiers, (int)b.Key)).ToList());

    private record HotkeyDto(int Id, string Label, string Action, int Modifiers, int Key);
}
