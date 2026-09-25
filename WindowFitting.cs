using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Herald;

public static class WindowFitting
{
    /// <summary>
    /// Shrinks a window's initial size so it fits the work area (screen minus taskbar) of
    /// the monitor it will appear on - the owner's monitor, or the one with the mouse.
    /// Small laptop screens at high scaling (e.g. 1280x800 at 150%) leave only ~850x500
    /// logical pixels, less than the windows' designed sizes.
    /// </summary>
    /// <summary>
    /// Scales everything inside the window (not the window frame) by the UI scale setting.
    /// A layout transform, so text stays crisp and layout reflows to the new size.
    /// </summary>
    public static void ApplyUiScale(this FrameworkElement content, int percent)
    {
        var scale = percent / 100.0;
        content.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? null : new ScaleTransform(scale, scale);
    }

    public static void FitToScreen(this Window window)
    {
        try
        {
            var owner = window.Owner;
            var screen = owner != null && new WindowInteropHelper(owner).Handle != IntPtr.Zero
                ? Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle)
                : Forms.Screen.FromPoint(Forms.Cursor.Position);

            // Screen sizes are physical pixels; window sizes are logical (DPI-scaled) ones.
            var dpi = VisualTreeHelper.GetDpi(owner ?? window);
            var workWidth = screen.WorkingArea.Width / dpi.DpiScaleX;
            var workHeight = screen.WorkingArea.Height / dpi.DpiScaleY;

            const double margin = 16;
            if (window.Width > workWidth - margin) window.Width = Math.Max(window.MinWidth, workWidth - margin);
            if (window.Height > workHeight - margin) window.Height = Math.Max(window.MinHeight, workHeight - margin);
        }
        catch
        {
            // best effort - the designed size is used if the screen can't be determined
        }
    }
}
