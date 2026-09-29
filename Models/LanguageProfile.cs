using System;

namespace Herald.Models;

/// <summary>
/// How to recognise a language: a part counts as this language when enough of its
/// marker words (or words containing its marker letters, like "å") appear in it.
/// Which voice to use then is decided per sender.
/// </summary>
public class LanguageProfile : ObservableObject
{
    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    private string _name = "New language";
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value ?? string.Empty);
    }

    private string _markers = string.Empty;
    /// <summary>Space- or comma-separated words; single letters match any word containing them.</summary>
    public string Markers
    {
        get => _markers;
        set => SetField(ref _markers, value ?? string.Empty);
    }

    private int _minMatches = 3;
    public int MinMatches
    {
        get => _minMatches;
        set => SetField(ref _minMatches, Math.Max(1, value));
    }
}
