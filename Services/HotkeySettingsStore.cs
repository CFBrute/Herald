using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using Herald.Models;

namespace Herald.Services;

/// <summary>
/// Persists the configurable global hotkeys (default: Alt+Shift+S toggle, Alt+Shift+Q
/// skip part, Alt+Shift+W skip message, Alt+Shift+0-9 speed) so rebinds survive a restart.
/// </summary>
public class HotkeySettingsStore
{
    private readonly string _filePath;

    public ObservableCollection<HotkeyBinding> Bindings { get; } = new();

    public HotkeySettingsStore(string settingsDir)
    {
        Directory.CreateDirectory(settingsDir);
        _filePath = Path.Combine(settingsDir, "hotkeys.json");

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

        var speedKeys = new[] { Key.D0, Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9 };
        for (var i = 0; i < speedKeys.Length; i++)
        {
            var speed = 100 + i * 10;
            yield return new HotkeyBinding(10 + i, $"Speed {speed}%", HotkeyAction.SetSpeed, mods, speedKeys[i], speed);
        }
    }

    private void ApplySavedKeys()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var dtos = JsonSerializer.Deserialize<List<HotkeyDto>>(json);
            if (dtos == null) return;

            foreach (var dto in dtos)
            {
                var binding = Bindings.FirstOrDefault(b => b.Id == dto.Id);
                if (binding == null) continue;
                binding.Modifiers = (ModifierKeys)dto.Modifiers;
                binding.Key = (Key)dto.Key;
            }
        }
        catch
        {
            // corrupt or unreadable file - keep the defaults
        }
    }

    private void Save()
    {
        try
        {
            var dtos = Bindings.Select(b => new HotkeyDto(b.Id, b.Label, b.Action.ToString(), b.SpeedValue, (int)b.Modifiers, (int)b.Key)).ToList();
            var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // best-effort persistence - a failed save shouldn't crash the app
        }
    }

    private record HotkeyDto(int Id, string Label, string Action, int? SpeedValue, int Modifiers, int Key);
}
