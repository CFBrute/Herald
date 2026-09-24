using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace Herald.Services;

/// <summary>
/// Reads text that gets copied to the clipboard, when the user has switched that on.
/// Uses AddClipboardFormatListener (Windows notifies Herald that the clipboard changed,
/// like any clipboard manager) - it sees no keystrokes. Skips content that password
/// managers mark as "don't monitor", and copies made by Herald itself.
/// </summary>
public class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    // Formats that apps (password managers in particular) set to ask clipboard monitors to ignore them.
    private static readonly string[] DoNotMonitorFormats =
        ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", "CanIncludeInClipboardHistory"];

    private static DateTime _ignoreUntilUtc;

    private readonly SpeechEngine _engine;
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private string? _lastText;
    private DateTime _lastTextUtc;

    public ClipboardWatcher(Window window, SpeechEngine engine)
    {
        _engine = engine;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);
    }

    /// <summary>Ignore clipboard changes for a moment, e.g. while the speak-selection hotkey copies.</summary>
    public static void IgnoreChangesFor(TimeSpan duration) => _ignoreUntilUtc = DateTime.UtcNow + duration;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _engine.ReadClipboardAutomatically && DateTime.UtcNow >= _ignoreUntilUtc)
        {
            _ = ReadClipboardAsync();
        }
        return IntPtr.Zero;
    }

    private async Task ReadClipboardAsync()
    {
        if (CopiedByHerald() || IsMarkedDoNotMonitor()) return;

        string? text = null;
        for (var attempt = 0; attempt < 5 && text == null; attempt++)
        {
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            }
            catch
            {
                // the copying app can still hold the clipboard open for a moment
                await Task.Delay(50);
            }
        }
        if (string.IsNullOrWhiteSpace(text)) return;

        // Some apps put the same text on the clipboard several times per copy.
        if (text == _lastText && DateTime.UtcNow - _lastTextUtc < TimeSpan.FromSeconds(2)) return;
        _lastText = text;
        _lastTextUtc = DateTime.UtcNow;

        _engine.EnqueueText(text, "clipboard");
    }

    private static bool CopiedByHerald()
    {
        GetWindowThreadProcessId(GetClipboardOwner(), out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static bool IsMarkedDoNotMonitor()
    {
        foreach (var name in DoNotMonitorFormats)
        {
            if (IsClipboardFormatAvailable(RegisterClipboardFormat(name))) return true;
        }
        return false;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_hwnd);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
