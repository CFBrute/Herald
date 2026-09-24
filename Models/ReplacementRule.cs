using System.ComponentModel;

namespace Herald.Models;

/// <summary>
/// Literal find/replace applied before synthesis, e.g. an em dash replaced with
/// a comma so the voice pauses instead of skipping over it.
/// </summary>
public class ReplacementRule : INotifyPropertyChanged
{
    private string _find = string.Empty;
    public string Find
    {
        get => _find;
        set
        {
            var v = value ?? string.Empty;
            if (_find == v) return;
            _find = v;
            OnPropertyChanged(nameof(Find));
        }
    }

    private string _replace = string.Empty;
    public string Replace
    {
        get => _replace;
        set
        {
            var v = value ?? string.Empty;
            if (_replace == v) return;
            _replace = v;
            OnPropertyChanged(nameof(Replace));
        }
    }

    // Parameterless constructor lets the settings DataGrid add new rows.
    public ReplacementRule() { }

    public ReplacementRule(string find, string replace)
    {
        _find = find;
        _replace = replace;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
