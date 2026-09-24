using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Herald.Services;

/// <summary>
/// Copies the current selection in whatever app has focus, by sending it a copy shortcut.
/// Uses Ctrl+Insert first because in terminals Ctrl+C with nothing selected interrupts the
/// running program; Ctrl+C is only tried as a fallback outside terminals.
/// </summary>
public static class SelectionCopier
{
    private const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_INSERT = 0x2D, VK_C = 0x43, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

    private static readonly string[] TerminalProcesses =
        ["WindowsTerminal", "conhost", "cmd", "powershell", "pwsh", "OpenConsole", "mintty", "alacritty", "wezterm-gui", "putty"];

    /// <summary>Returns true if new content reached the clipboard.</summary>
    public static async Task<bool> CopySelectionAsync()
    {
        // The hotkey fires while Alt/Shift are still held; a copy shortcut sent now would
        // arrive as Ctrl+Alt+Shift+Insert. Wait for the user to let go.
        await WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1.5));

        var before = GetClipboardSequenceNumber();
        SendChord(VK_CONTROL, VK_INSERT, extended: true);
        if (await WaitForClipboardChangeAsync(before, TimeSpan.FromMilliseconds(400))) return true;

        if (IsTerminalInForeground()) return false;

        SendChord(VK_CONTROL, VK_C, extended: false);
        return await WaitForClipboardChangeAsync(before, TimeSpan.FromMilliseconds(400));
    }

    private static async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && AnyDown(VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN))
        {
            await Task.Delay(15);
        }
    }

    private static bool AnyDown(params ushort[] keys)
    {
        foreach (var key in keys)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        }
        return false;
    }

    private static async Task<bool> WaitForClipboardChangeAsync(uint before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetClipboardSequenceNumber() != before) return true;
            await Task.Delay(20);
        }
        return false;
    }

    private static bool IsTerminalInForeground()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            using var process = Process.GetProcessById((int)pid);
            return Array.Exists(TerminalProcesses, name => string.Equals(name, process.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Unknown: be safe and don't risk sending Ctrl+C to a terminal.
            return true;
        }
    }

    private static void SendChord(ushort modifier, ushort key, bool extended)
    {
        var keyFlags = extended ? KEYEVENTF_EXTENDEDKEY : 0;
        INPUT[] inputs =
        [
            Key(modifier, 0),
            Key(key, keyFlags),
            Key(key, keyFlags | KEYEVENTF_KEYUP),
            Key(modifier, KEYEVENTF_KEYUP)
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // MOUSEINPUT is the largest member; it must be present so the union has the right size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
