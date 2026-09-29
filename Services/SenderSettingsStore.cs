using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
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

    // The lookup used from any thread. Senders (bound to the settings page) belongs to
    // the UI thread and only ever gains entries, after they're added here.
    private readonly Dictionary<string, SenderSettings> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public ObservableCollection<SenderSettings> Senders { get; } = [];

    public SenderSettingsStore(string filePath)
    {
        _filePath = filePath;

        foreach (var loaded in Load()) Add(loaded);
        if (!_byName.ContainsKey("claude")) Add(new SenderSettings("claude"));
        if (!_byName.ContainsKey("herald")) Add(new SenderSettings("herald"));

        Save();
    }

    private void Add(SenderSettings settings)
    {
        _byName[settings.Sender] = settings;
        settings.PropertyChanged += OnSenderChanged;
        Senders.Add(settings);
    }

    private void OnSenderChanged(object? sender, PropertyChangedEventArgs e) => Save();

    /// <summary>
    /// Looks up a sender's settings (case-insensitive), creating a new default
    /// entry - visible on the settings page - the first time an unseen tag shows up.
    /// </summary>
    public SenderSettings GetOrCreate(string sender)
    {
        if (String.IsNullOrWhiteSpace(sender)) sender = "unknown";

        SenderSettings created;
        lock (_lock)
        {
            if (_byName.TryGetValue(sender, out var existing)) return existing;

            created = new SenderSettings(sender);
            _byName[sender] = created;
        }

        created.PropertyChanged += OnSenderChanged;
        Save();

        // Never wait for the UI thread here: the caller may be a background thread while
        // the UI thread is itself waiting for the lock above.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Senders.Add(created);
        else dispatcher.BeginInvoke(() => Senders.Add(created));

        return created;
    }

    private IEnumerable<SenderSettings> Load()
    {
        foreach (var dto in SafeFile.ReadJson<List<SenderSettingsDto>>(_filePath) ?? [])
        {
            if (String.IsNullOrWhiteSpace(dto.Sender)) continue;
            var replacements = dto.Replacements?.Select(r => new ReplacementRule(r.Find, r.Replace));

            // Entries saved before engines were selectable were all spoken by Kokoro.
            var engineId = dto.EngineId ?? "kokoro";
            var voiceId = dto.EngineId == null ? "am_michael" : dto.VoiceId;

            var settings = new SenderSettings(dto.Sender, dto.Muted, dto.FilterCharacters, replacements, engineId, voiceId)
            {
                AnnounceSender = dto.AnnounceSender,
                LanguageRules = dto.LanguageRules?.Select(r => new LanguageVoiceRule(r.LanguageName, r.EngineId, r.VoiceId)).ToList() ?? []
            };
            // Saved before the option existed: keep the default (on for claude only).
            if (dto.ShowAsMarkdown is { } markdown) settings.ShowAsMarkdown = markdown;
            yield return settings;
        }
    }

    private void Save()
    {
        List<SenderSettings> senders;
        lock (_lock) senders = [.. _byName.Values];

        SafeFile.WriteJson(_filePath, senders.Select(s => new SenderSettingsDto(
            s.Sender, s.Muted, s.FilterCharacters, s.AnnounceSender,
            [.. s.ReplacementSnapshot.Select(r => new ReplacementDto(r.Find, r.Replace))],
            s.EngineId, s.VoiceId,
            [.. s.LanguageRules.Select(r => new LanguageRuleDto(r.LanguageName, r.EngineId, r.VoiceId))],
            s.ShowAsMarkdown)).ToList());
    }

    private record SenderSettingsDto(string Sender, bool Muted, string FilterCharacters, bool AnnounceSender = false,
                                     List<ReplacementDto>? Replacements = null,
                                     string? EngineId = null, string? VoiceId = null,
                                     List<LanguageRuleDto>? LanguageRules = null,
                                     bool? ShowAsMarkdown = null);

    private record LanguageRuleDto(string LanguageName, string EngineId, string VoiceId);

    private record ReplacementDto(string Find, string Replace);
}
