using System.Windows;

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
}
