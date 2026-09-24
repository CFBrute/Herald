using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Herald.Models;

namespace Herald.Services;

/// <summary>
/// Registers global hotkeys via the Win32 RegisterHotKey API. This is deliberately
/// NOT a WH_KEYBOARD_LL keyboard hook - RegisterHotKey only ever notifies the app when
/// one of its own exact registered combos fires, so it never observes other keystrokes
/// (which is what makes low-level hooks look like a keylogger to AV/EDR). Registered
/// hotkeys fire regardless of which window has focus.
/// </summary>
public class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    private readonly SpeechEngine _engine;
    private readonly HwndSource _source;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, HotkeyBinding> _active = new();

    public HotkeyManager(Window window, SpeechEngine engine, HotkeySettingsStore store)
    {
        _engine = engine;

        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);

        foreach (var binding in store.Bindings)
        {
            TryRegister(binding);
        }
    }

    /// <summary>
    /// Attempts to move a binding to a new combo. Reverts and returns false if the new
    /// combo is already claimed (by Herald itself or another app/the OS).
    /// </summary>
    public bool Rebind(HotkeyBinding binding, ModifierKeys modifiers, Key key)
    {
        Unregister(binding);

        var previousModifiers = binding.Modifiers;
        var previousKey = binding.Key;

        binding.Modifiers = modifiers;
        binding.Key = key;

        if (TryRegister(binding)) return true;

        binding.Modifiers = previousModifiers;
        binding.Key = previousKey;
        TryRegister(binding);
        return false;
    }

    private bool TryRegister(HotkeyBinding binding)
    {
        var vk = (uint)KeyInterop.VirtualKeyFromKey(binding.Key);
        var ok = RegisterHotKey(_hwnd, binding.Id, (uint)binding.Modifiers, vk);
        if (ok)
        {
            _active[binding.Id] = binding;
        }
        return ok;
    }

    private void Unregister(HotkeyBinding binding)
    {
        UnregisterHotKey(_hwnd, binding.Id);
        _active.Remove(binding.Id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _active.TryGetValue(wParam.ToInt32(), out var binding))
        {
            Execute(binding);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void Execute(HotkeyBinding binding)
    {
        switch (binding.Action)
        {
            case HotkeyAction.Toggle:
                _engine.Enabled = !_engine.Enabled;
                _engine.Announce(_engine.Enabled ? "Activated" : "Off");
                break;

            case HotkeyAction.Stop:
                _engine.Skip();
                break;

            case HotkeyAction.SkipMessage:
                _engine.SkipMessage();
                break;

            case HotkeyAction.SpeakClipboard:
                SpeakClipboard();
                break;

            case HotkeyAction.SetSpeed:
                _engine.SpeedPercent = binding.SpeedValue ?? 100;
                _engine.EnqueueText($"Speed {_engine.SpeedPercent}", "herald");
                break;
        }
    }

    /// <summary>
    /// Copies the selection in the focused app, then speaks the clipboard. If nothing new
    /// was copied (no selection), whatever was already on the clipboard is read instead.
    /// Starts on the UI thread (WM_HOTKEY) and stays there, which the clipboard API requires.
    /// </summary>
    private async void SpeakClipboard()
    {
        // This copy is read right here; the automatic clipboard reader must not read it too.
        ClipboardWatcher.IgnoreChangesFor(TimeSpan.FromSeconds(3));

        await SelectionCopier.CopySelectionAsync();

        var text = string.Empty;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                break;
            }
            catch
            {
                // the app that just copied can still hold the clipboard open for a moment
                await Task.Delay(50);
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _engine.Announce("The clipboard has no text");
            return;
        }

        _engine.EnqueueRequested(text, "clipboard");
    }

    public void Dispose()
    {
        foreach (var id in _active.Keys)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _active.Clear();
        _source.RemoveHook(WndProc);
    }
}
