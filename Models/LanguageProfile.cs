using System.ComponentModel;

namespace Herald.Models;

/// <summary>
/// How to recognise a language: a part counts as this language when enough of its
/// marker words (or words containing its marker letters, like "å") appear in it.
/// Which voice to use then is decided per sender.
/// </summary>
public class LanguageProfile : INotifyPropertyChanged
{
    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled != value) { _enabled = value; OnPropertyChanged(nameof(Enabled)); } }
    }

    private string _name = "New language";
    public string Name
    {
        get => _name;
        set { var v = value ?? string.Empty; if (_name != v) { _name = v; OnPropertyChanged(nameof(Name)); } }
    }

    private string _markers = string.Empty;
    /// <summary>Space- or comma-separated words; single letters match any word containing them.</summary>
    public string Markers
    {
        get => _markers;
        set { var v = value ?? string.Empty; if (_markers != v) { _markers = v; OnPropertyChanged(nameof(Markers)); } }
    }

    private int _minMatches = 3;
    public int MinMatches
    {
        get => _minMatches;
        set
        {
            var v = value < 1 ? 1 : value;
            if (_minMatches != v) { _minMatches = v; OnPropertyChanged(nameof(MinMatches)); }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
