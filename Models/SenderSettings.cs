using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Herald.Models;

/// <summary>
/// Per-sender rules: a sender tag is just a self-declared label sent alongside a
/// message (e.g. "claude", "herald") - not verified. Each tag gets its own mute
/// switch and its own set of characters to strip before synthesis.
/// </summary>
public class SenderSettings : INotifyPropertyChanged
{
    public string Sender { get; }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            OnPropertyChanged(nameof(Muted));
        }
    }

    private bool _announceSender;
    /// <summary>
    /// When true, the sender tag is spoken aloud before the message (e.g. "claude: ...").
    /// Off by default - the tag is otherwise only shown as a badge in the GUI.
    /// </summary>
    public bool AnnounceSender
    {
        get => _announceSender;
        set
        {
            if (_announceSender == value) return;
            _announceSender = value;
            OnPropertyChanged(nameof(AnnounceSender));
        }
    }

    private string _engineId;
    /// <summary>Which speech engine speaks this sender's messages, e.g. "windows" or "kokoro".</summary>
    public string EngineId
    {
        get => _engineId;
        set
        {
            var v = value ?? DefaultEngineId;
            if (_engineId == v) return;
            _engineId = v;
            OnPropertyChanged(nameof(EngineId));
        }
    }

    private string _voiceId;
    /// <summary>Voice within the engine. Empty means the engine's default voice.</summary>
    public string VoiceId
    {
        get => _voiceId;
        set
        {
            var v = value ?? string.Empty;
            if (_voiceId == v) return;
            _voiceId = v;
            OnPropertyChanged(nameof(VoiceId));
        }
    }

    public const string DefaultEngineId = "windows";

    private IReadOnlyList<LanguageVoiceRule> _languageRules = [];
    /// <summary>
    /// "If this language is detected, use this voice" rules. Empty means this sender
    /// always uses its default voice. Replaced as a whole (immutable), so the speech
    /// threads can read it without locking.
    /// </summary>
    public IReadOnlyList<LanguageVoiceRule> LanguageRules
    {
        get => _languageRules;
        set
        {
            _languageRules = value ?? [];
            OnPropertyChanged(nameof(LanguageRules));
        }
    }

    private string _filterCharacters;
    public string FilterCharacters
    {
        get => _filterCharacters;
        set
        {
            var v = value ?? string.Empty;
            if (_filterCharacters == v) return;
            _filterCharacters = v;
            OnPropertyChanged(nameof(FilterCharacters));
        }
    }

    public static readonly string DefaultFilterCharacters = "-_/\\\"" + (char)0x2013 + (char)0x2014;

    /// <summary>
    /// Applied before character stripping. Edited from the UI thread while the speech
    /// engine reads from other threads, so readers use <see cref="ReplacementSnapshot"/>.
    /// </summary>
    public ObservableCollection<ReplacementRule> Replacements { get; } = new();

    private volatile ReplacementRule[] _replacementSnapshot = Array.Empty<ReplacementRule>();
    public IReadOnlyList<ReplacementRule> ReplacementSnapshot => _replacementSnapshot;

    public static ReplacementRule[] DefaultReplacements() =>
    [
        new ReplacementRule(((char)0x2014).ToString(), ", ")
    ];

    public SenderSettings(string sender, bool muted = false, string? filterCharacters = null,
                          IEnumerable<ReplacementRule>? replacements = null,
                          string? engineId = null, string? voiceId = null)
    {
        Sender = sender;
        _muted = muted;
        _filterCharacters = filterCharacters ?? DefaultFilterCharacters;
        _engineId = engineId ?? DefaultEngineId;
        _voiceId = voiceId ?? string.Empty;

        foreach (var rule in replacements ?? DefaultReplacements())
        {
            rule.PropertyChanged += OnRuleChanged;
            Replacements.Add(rule);
        }
        RefreshSnapshot();

        Replacements.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (ReplacementRule rule in e.NewItems) rule.PropertyChanged += OnRuleChanged;
            }
            if (e.OldItems != null)
            {
                foreach (ReplacementRule rule in e.OldItems) rule.PropertyChanged -= OnRuleChanged;
            }
            RefreshSnapshot();
            OnPropertyChanged(nameof(Replacements));
        };
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshSnapshot();
        OnPropertyChanged(nameof(Replacements));
    }

    private void RefreshSnapshot() =>
        _replacementSnapshot = Replacements.Select(r => new ReplacementRule(r.Find, r.Replace)).ToArray();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
