using System.Windows;
using System.Windows.Input;

namespace Herald;

public partial class HotkeyCaptureWindow : Window
{
    public ModifierKeys CapturedModifiers { get; private set; }
    public Key CapturedKey { get; private set; }

    public HotkeyCaptureWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            DialogResult = false;
            return;
        }

        if (IsModifierKey(key)) return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            StatusText.Text = "Needs at least one modifier (Ctrl/Alt/Shift/Win) - try again.";
            return;
        }

        CapturedModifiers = modifiers;
        CapturedKey = key;
        DialogResult = true;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
