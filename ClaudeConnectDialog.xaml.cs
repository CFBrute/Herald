using System;
using System.Windows;
using Herald.Services;

namespace Herald;

public enum ClaudeConnectChoice
{
    Connect,
    NotNow,
    DontAskAgain
}

public partial class ClaudeConnectDialog : Window
{
    public ClaudeConnectChoice Choice { get; private set; } = ClaudeConnectChoice.NotNow;

    public ClaudeConnectDialog(string summary, string detail)
    {
        InitializeComponent();
        SummaryText.Text = summary;
        DetailText.Text = detail;
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => Finish(ClaudeConnectChoice.Connect);
    private void NotNow_Click(object sender, RoutedEventArgs e) => Finish(ClaudeConnectChoice.NotNow);
    private void DontAsk_Click(object sender, RoutedEventArgs e) => Finish(ClaudeConnectChoice.DontAskAgain);

    private void Finish(ClaudeConnectChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }

    /// <summary>Connects Claude Code and reports the result; used by the startup prompt and the settings page.</summary>
    public static bool Connect(Window owner, ClaudeCodeIntegration claude)
    {
        try
        {
            var backup = claude.Connect();
            MessageBox.Show(owner,
                "Claude Code is now connected to Herald.\n\n" +
                "New Claude Code sessions pick this up right away; an open session may need a restart.\n\n" +
                $"The previous settings were saved as:\n{backup}",
                "Connected", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Couldn't update Claude Code's settings:\n\n" + ex.Message,
                "Connect failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
