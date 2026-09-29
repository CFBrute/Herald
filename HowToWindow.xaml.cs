using System.IO;
using System.Windows;
using Herald.Services;

namespace Herald;

/// <summary>
/// How other programs send Herald messages: docs/talking-to-herald.md, embedded in
/// Herald.exe, with this Herald's current address above it.
/// </summary>
public partial class HowToWindow : Window
{
    public HowToWindow(AppServices services)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => this.FitToScreen();
        this.FollowUiScale(Root, services.Settings);

        var server = services.HookServer;
        CurrentAddress.Text = server.ListeningPort is { } port
            ? $"This Herald is listening on 127.0.0.1:{port} right now, as {services.Paths.EndpointFile} says."
            : $"This Herald isn't listening right now: {server.Problem ?? "the hook server is stopped"}.";
        Page.Text = PageText();
    }

    /// <summary>The page's Markdown, from inside Herald.exe.</summary>
    public static string PageText()
    {
        using var stream = typeof(HowToWindow).Assembly.GetManifestResourceStream("talking-to-herald.md");
        return stream == null ? "The page is missing from this build of Herald." : new StreamReader(stream).ReadToEnd();
    }
}
