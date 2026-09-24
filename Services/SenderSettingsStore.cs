using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Herald.Models;

namespace Herald.Services;

/// <summary>
/// Holds the per-sender rule set (mute + filter characters), seeded with defaults
/// for "claude" and "herald" but open to any sender tag a message shows up with.
/// Persists to a small JSON file so rules survive a restart.
/// </summary>
public class SenderSettingsStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public ObservableCollection<SenderSettings> Senders { get; } = new();

    public SenderSettingsStore(string settingsDir)
    {
        Directory.CreateDirectory(settingsDir);
        _filePath = Path.Combine(settingsDir, "sender-settings.json");

        Load();

        if (!Senders.Any(s => s.Sender.Equals("claude", StringComparison.OrdinalIgnoreCase)))
        {
            Senders.Add(new SenderSettings("claude"));
        }
        if (!Senders.Any(s => s.Sender.Equals("herald", StringComparison.OrdinalIgnoreCase)))
        {
            Senders.Add(new SenderSettings("herald"));
        }

        foreach (var s in Senders)
        {
            s.PropertyChanged += (_, _) => Save();
        }
        Senders.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.Cast<SenderSettings>())
                {
                    item.PropertyChanged += (_, _) => Save();
                }
            }
            Save();
        };

        Save();
    }

    /// <summary>
    /// Looks up a sender's settings (case-insensitive), creating a new default
    /// entry - visible on the settings page - the first time an unseen tag shows up.
    /// </summary>
    public SenderSettings GetOrCreate(string sender)
    {
        if (string.IsNullOrWhiteSpace(sender)) sender = "unknown";

        lock (_lock)
        {
            var existing = Senders.FirstOrDefault(s => s.Sender.Equals(sender, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var created = new SenderSettings(sender);
            created.PropertyChanged += (_, _) => Save();

            var app = System.Windows.Application.Current;
            if (app != null)
            {
                app.Dispatcher.Invoke(() => Senders.Add(created));
            }
            else
            {
                Senders.Add(created);
            }

            return created;
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var dtos = JsonSerializer.Deserialize<List<SenderSettingsDto>>(json);
            if (dtos == null) return;

            foreach (var dto in dtos)
            {
                if (string.IsNullOrWhiteSpace(dto.Sender)) continue;
                var replacements = dto.Replacements?.Select(r => new ReplacementRule(r.Find, r.Replace));

                // Entries saved before engines were selectable were all spoken by Kokoro.
                var engineId = dto.EngineId ?? "kokoro";
                var voiceId = dto.EngineId == null ? "am_michael" : dto.VoiceId;

                Senders.Add(new SenderSettings(dto.Sender, dto.Muted, dto.FilterCharacters, replacements, engineId, voiceId)
                {
                    AnnounceSender = dto.AnnounceSender,
                    LanguageRules = dto.LanguageRules?.Select(r => new LanguageVoiceRule(r.LanguageName, r.EngineId, r.VoiceId)).ToList() ?? []
                });
            }
        }
        catch
        {
            // corrupt or unreadable settings file - fall back to defaults
        }
    }

    private void Save()
    {
        try
        {
            var dtos = Senders.Select(s => new SenderSettingsDto(
                s.Sender, s.Muted, s.FilterCharacters, s.AnnounceSender,
                s.ReplacementSnapshot.Select(r => new ReplacementDto(r.Find, r.Replace)).ToList(),
                s.EngineId, s.VoiceId,
                s.LanguageRules.Select(r => new LanguageRuleDto(r.LanguageName, r.EngineId, r.VoiceId)).ToList())).ToList();
            var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // best-effort persistence - a failed save shouldn't crash the app
        }
    }

    private record SenderSettingsDto(string Sender, bool Muted, string FilterCharacters, bool AnnounceSender = false,
                                     List<ReplacementDto>? Replacements = null,
                                     string? EngineId = null, string? VoiceId = null,
                                     List<LanguageRuleDto>? LanguageRules = null);

    private record LanguageRuleDto(string LanguageName, string EngineId, string VoiceId);

    private record ReplacementDto(string Find, string Replace);
}
