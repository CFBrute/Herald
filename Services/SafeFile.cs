using System.IO;
using System.Text.Json;

namespace Herald.Services;

/// <summary>
/// Best-effort file helpers for Herald's own settings and data: a failed read or write
/// falls back quietly instead of crashing the app.
/// </summary>
public static class SafeFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The file's contents, or null when it's missing, unreadable or corrupt.</summary>
    public static T? ReadJson<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), options) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes to a temporary file first and then swaps it in, so a crash halfway through
    /// never leaves a half-written file (which would reset the settings to defaults).
    /// </summary>
    public static void WriteJson<T>(string path, T value, bool indented = true) =>
        WriteJson(path, value, indented ? Indented : JsonSerializerOptions.Default);

    /// <inheritdoc cref="WriteJson{T}(string, T, bool)"/>
    public static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        var temp = $"{path}.{Path.GetRandomFileName()}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, options));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
        }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch
        {
            // in use or already gone - nothing to do
        }
    }
}
