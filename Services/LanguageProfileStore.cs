using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Herald.Models;

namespace Herald.Services;

/// <summary>
/// How to detect each language, persisted to language-profiles.json. Seeded with a
/// Swedish example template that starts switched off.
/// </summary>
public class LanguageProfileStore
{
    private readonly string _filePath;
    private volatile IReadOnlyList<CompiledLanguageProfile> _snapshot = [];

    public ObservableCollection<LanguageProfile> Profiles { get; } = [];

    /// <summary>Enabled profiles, frozen for use from the speech threads.</summary>
    public IReadOnlyList<CompiledLanguageProfile> Snapshot => _snapshot;

    public LanguageProfileStore(string filePath)
    {
        _filePath = filePath;

        if (!Load())
        {
            // An example template: off until the user ticks it.
            Profiles.Add(new LanguageProfile
            {
                Enabled = false,
                Name = "Swedish",
                Markers = "och att det är som en ett på för med jag inte har de av till den kan om så vi men ska vara också när från eller hur här där då nu bara efter å ä ö",
                MinMatches = 3
            });
        }

        foreach (var profile in Profiles) profile.PropertyChanged += OnProfileChanged;
        Profiles.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (LanguageProfile p in e.NewItems) p.PropertyChanged += OnProfileChanged;
            if (e.OldItems != null) foreach (LanguageProfile p in e.OldItems) p.PropertyChanged -= OnProfileChanged;
            Changed();
        };

        Changed();
    }

    private void OnProfileChanged(object? sender, PropertyChangedEventArgs e) => Changed();

    private void Changed()
    {
        _snapshot = [.. Profiles.Where(p => p.Enabled && p.Name.Length > 0).Select(CompiledLanguageProfile.From)];
        Save();
    }

    /// <summary>False when there's no usable file yet.</summary>
    private bool Load()
    {
        if (SafeFile.ReadJson<List<ProfileDto>>(_filePath) is not { } dtos) return false;

        foreach (var dto in dtos)
        {
            Profiles.Add(new LanguageProfile
            {
                Enabled = dto.Enabled,
                Name = dto.Name,
                Markers = dto.Markers,
                MinMatches = dto.MinMatches
            });
        }
        return true;
    }

    private void Save() =>
        SafeFile.WriteJson(_filePath, Profiles.Select(p => new ProfileDto(p.Enabled, p.Name, p.Markers, p.MinMatches)).ToList());

    private record ProfileDto(bool Enabled, string Name, string Markers, int MinMatches);
}
