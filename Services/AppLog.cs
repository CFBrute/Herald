using System;
using System.IO;
using System.Threading;

namespace Herald.Services;

/// <summary>
/// Herald's own log (herald.log): things that go wrong without anyone seeing them - a
/// message that couldn't be read, a hotkey another program holds, a part the voice
/// couldn't make. One line per event: "time | area | message", with the error's details
/// on the lines after it. Starts over in herald.old.log at 512 KB, like the playback log.
/// </summary>
public class AppLog
{
    private const long MaxSize = 512 * 1024;

    private readonly string _path;
    private readonly string _oldPath;
    private readonly Lock _lock = new();

    public AppLog(string path, string oldPath)
    {
        _path = path;
        _oldPath = oldPath;
    }

    /// <param name="area">Which part of Herald: "hook", "hotkeys", "speech", "kokoro", ...</param>
    public void Write(string area, string message, Exception? error = null)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {area} | {message}";
        if (error != null) line += Environment.NewLine + "    " + error.ToString().Replace("\n", "\n    ");

        lock (_lock)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxSize) File.Move(_path, _oldPath, overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never be the thing that breaks Herald.
            }
        }
    }

    /// <summary>A shortened, single-line version of a text, to show which message a line is about.</summary>
    public static string Excerpt(string? text, int length = 80)
    {
        if (String.IsNullOrEmpty(text)) return "\"\"";
        var line = text.ReplaceLineEndings(" ");
        return "\"" + (line.Length > length ? line[..length] + "…" : line) + "\"";
    }
}
