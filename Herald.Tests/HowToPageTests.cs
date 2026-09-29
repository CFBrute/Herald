using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Herald.Services;

namespace Herald.Tests;

/// <summary>The "Talking to Herald" page (docs/talking-to-herald.md, shown from Settings).</summary>
public class HowToPageTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly SpeechHarness _h = new();

    public void Dispose()
    {
        _h.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void The_page_is_inside_herald_and_renders_with_its_table()
    {
        var page = HowToWindow.PageText();
        Assert.StartsWith("# Talking to Herald", page);

        var blocks = UiThread.Invoke(() => ((StackPanel)new MarkdownView { Text = page }.Content).Children.Cast<object>()
                                                                                                          .Select(b => b.GetType().Name).ToArray());
        // The commands table is drawn as a table (a framed grid), not as text with pipes.
        Assert.Contains(blocks, name => name == "Border");
        Assert.True(blocks.Length > 20);
    }

    [Fact]
    public async Task The_powershell_example_on_the_page_really_speaks()
    {
        // Herald as the example finds it: %LOCALAPPDATA%\Herald\endpoint.json, here in a temporary folder.
        var server = new HookServer(_h.Speech, _h.Settings, port: 0, endpointFile: Path.Combine(_dir.Path, "Herald", "endpoint.json"));
        server.Start();
        try
        {
            var example = Regex.Match(HowToWindow.PageText(), "```powershell\n(.*?)```", RegexOptions.Singleline).Groups[1].Value;
            // The test copy never falls back to 8766, where the Herald actually running on this PC listens.
            var testCopy = example.Replace("$port = 8766", "$port = 1");
            Assert.DoesNotContain("8766", testCopy);
            var script = Path.Combine(_dir.Path, "example.ps1");
            File.WriteAllText(script, testCopy);

            var run = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                Environment = { ["LOCALAPPDATA"] = _dir.Path }
            };
            using var process = Process.Start(run)!;
            var reply = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.StartsWith("{\"status\":\"ok\"", reply);
            await _h.HistoryCount(1);
            Assert.Equal(("my-script", "The backup finished."), (_h.History[0].Sender, _h.History[0].SpokenText));
        }
        finally
        {
            server.Stop();
        }
    }
}
