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
public class SenderSettings : ObservableObject
{
    public string Sender { get; }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set => SetField(ref _muted, value);
    }

    private bool _announceSender;
    /// <summary>
    /// When true, the sender tag is spoken aloud before the message (e.g. "claude: ...").
    /// Off by default - the tag is otherwise only shown as a badge in the GUI.
    /// </summary>
    public bool AnnounceSender
    {
        get => _announceSender;
        set => SetField(ref _announceSender, value);
    }

    private bool _showAsMarkdown;
    /// <summary>
    /// When true, this sender's messages are shown formatted as Markdown in the lists.
    /// On by default only for "claude", whose replies are written in Markdown.
    /// </summary>
    public bool ShowAsMarkdown
    {
        get => _showAsMarkdown;
        set => SetField(ref _showAsMarkdown, value);
    }

    private string _engineId;
    /// <summary>Which speech engine speaks this sender's messages, e.g. "windows" or "kokoro".</summary>
    public string EngineId
    {
        get => _engineId;
        set => SetField(ref _engineId, value ?? DefaultEngineId);
    }

    private string _voiceId;
    /// <summary>Voice within the engine. Empty means the engine's default voice.</summary>
    public string VoiceId
    {
        get => _voiceId;
        set => SetField(ref _voiceId, value ?? String.Empty);
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
        set => SetField(ref _languageRules, value ?? []);
    }

    private string _filterCharacters;
    public string FilterCharacters
    {
        get => _filterCharacters;
        set => SetField(ref _filterCharacters, value ?? String.Empty);
    }

    public static readonly string DefaultFilterCharacters = "*-_/\\\"" + (char)0x2013 + (char)0x2014;

    /// <summary>
    /// Applied before character stripping. Edited from the UI thread while the speech
    /// engine reads from other threads, so readers use <see cref="ReplacementSnapshot"/>.
    /// </summary>
    public ObservableCollection<ReplacementRule> Replacements { get; } = [];

    private volatile ReplacementRule[] _replacementSnapshot = [];
    public IReadOnlyList<ReplacementRule> ReplacementSnapshot => _replacementSnapshot;

    /// <summary>Claude's replies are written in Markdown; other senders' text is shown as it is.</summary>
    public static bool DefaultShowAsMarkdown(string sender) => sender.Equals("claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the text rules - characters to strip, replacements and Markdown display - back to
    /// Herald's defaults, e.g. to pick up a default that changed after this sender was made.
    /// The voice, language rules, mute and "speak sender" switches are kept.
    /// </summary>
    public void ResetTextRules()
    {
        FilterCharacters = DefaultFilterCharacters;
        ShowAsMarkdown = DefaultShowAsMarkdown(Sender);
        // One by one, so each removed rule is unhooked (Clear() doesn't say which were removed).
        while (Replacements.Count > 0) Replacements.RemoveAt(Replacements.Count - 1);
        foreach (var rule in DefaultReplacements()) Replacements.Add(rule);
    }

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
        _showAsMarkdown = DefaultShowAsMarkdown(sender);
        _filterCharacters = filterCharacters ?? DefaultFilterCharacters;
        _engineId = engineId ?? DefaultEngineId;
        _voiceId = voiceId ?? String.Empty;

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
        _replacementSnapshot = [.. Replacements.Select(r => new ReplacementRule(r.Find, r.Replace))];
}
