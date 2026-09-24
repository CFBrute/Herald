using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Herald;

/// <summary>
/// Herald's icon under "hidden icons" in the taskbar: double-click opens the window,
/// right-click offers Open, Speech on/off, Settings and Exit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _speechItem;
    private readonly IntPtr _iconHandle;

    public event Action? OpenRequested;
    public event Action? ToggleSpeechRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _speechItem = new Forms.ToolStripMenuItem("Speech on", null, (_, _) => ToggleSpeechRequested?.Invoke());

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open Herald", null, (_, _) => OpenRequested?.Invoke())
        {
            Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold)
        });
        menu.Items.Add(_speechItem);
        menu.Items.Add("Settings...", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit Herald", null, (_, _) => ExitRequested?.Invoke());

        var bitmap = LoadIcon();
        _iconHandle = bitmap.GetHicon();
        _icon = new Forms.NotifyIcon
        {
            Icon = Icon.FromHandle(_iconHandle),
            Text = "Herald",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        bitmap.Dispose();
    }

    public void SetSpeechEnabled(bool enabled)
    {
        _speechItem.Checked = enabled;
        _icon.Text = enabled ? "Herald - speech on" : "Herald - speech off";
    }

    /// <summary>Tells the user that closing the window didn't quit Herald.</summary>
    public void ShowStillRunningHint()
    {
        _icon.ShowBalloonTip(4000, "Herald is still running",
            "It's in the tray under hidden icons. Right-click the icon and choose Exit Herald to quit.",
            Forms.ToolTipIcon.Info);
    }

    /// <summary>Herald's icon (Assets/Herald.png), scaled down for the tray.</summary>
    private static Bitmap LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Herald.png"));
        using var stream = resource!.Stream;
        using var source = new Bitmap(stream);

        var size = Forms.SystemInformation.SmallIconSize.Width >= 24 ? 32 : 16;
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, size, size);
        return bitmap;
    }

    public void Dispose()
    {
        // Hide first, or a dead icon lingers in the tray until the mouse passes over it.
        _icon.Visible = false;
        _icon.Dispose();
        DestroyIcon(_iconHandle);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
