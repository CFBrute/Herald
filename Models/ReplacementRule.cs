namespace Herald.Models;

/// <summary>
/// Literal find/replace applied before synthesis, e.g. an em dash replaced with
/// a comma so the voice pauses instead of skipping over it.
/// </summary>
public class ReplacementRule : ObservableObject
{
    private string _find = string.Empty;
    public string Find
    {
        get => _find;
        set => SetField(ref _find, value ?? string.Empty);
    }

    private string _replace = string.Empty;
    public string Replace
    {
        get => _replace;
        set => SetField(ref _replace, value ?? string.Empty);
    }

    // Parameterless constructor lets the settings DataGrid add new rows.
    public ReplacementRule() { }

    public ReplacementRule(string find, string replace)
    {
        _find = find;
        _replace = replace;
    }
}
