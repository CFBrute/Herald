using System.Globalization;
using System.IO;
using Herald.Services;

namespace Herald.Tests;

public class HeraldCleanupTests
{
    [Theory]
    [InlineData(500L, "500 bytes")]
    [InlineData(2048L, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(3L * 512 * 1024 * 1024, "1.5 GB")]
    public void Sizes_are_described_in_the_largest_fitting_unit(long bytes, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, HeraldCleanup.Describe(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Folder_size_adds_up_nested_files_and_is_zero_when_missing()
    {
        using var dir = new TempFolder();
        Directory.CreateDirectory(Path.Combine(dir.Path, "sub"));
        File.WriteAllBytes(dir.File("a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir.Path, "sub", "b.bin"), new byte[50]);

        Assert.Equal(150, HeraldCleanup.FolderSize(dir.Path));
        Assert.Equal(0, HeraldCleanup.FolderSize(dir.File("missing")));
    }
}

public class AppPathsTests
{
    [Fact]
    public void Keeps_the_file_names_earlier_versions_used()
    {
        using var dir = new TempFolder();
        var paths = new AppPaths(dir.Path);

        // Renaming any of these would make Herald lose the user's existing settings.
        Assert.Equal(
            ["engine-settings.json", "sender-settings.json", "hotkeys.json", "language-profiles.json"],
            paths.SettingsFiles.Select(Path.GetFileName));
        Assert.Equal("history.json", Path.GetFileName(paths.HistoryFile));
        Assert.Equal(Path.Combine(dir.Path, "integrations", "claude-hook.ps1"), paths.ClaudeHookScript);
        Assert.All(paths.SettingsFiles.Concat(paths.LogFiles), f => Assert.Equal(dir.Path, Path.GetDirectoryName(f)));
    }

    [Fact]
    public void Creates_the_data_and_history_folders()
    {
        using var dir = new TempFolder();
        var root = dir.File("new-data");

        var paths = new AppPaths(root);

        Assert.True(Directory.Exists(paths.DataDir));
        Assert.True(Directory.Exists(paths.HistoryDir));
    }
}

public class BuildInfoTests
{
    [Theory]
    [InlineData("0.91.11+29f2105", "0.91.11", "29f2105")]
    [InlineData("0.91.11+29f2105*", "0.91.11", "29f2105*")]
    [InlineData("0.91.0", "0.91.0", "")]
    [InlineData(null, "0.0.0", "")]
    public void The_stamped_version_is_split_into_version_and_commit(string? stamped, string version, string commit) =>
        Assert.Equal((version, commit), BuildInfo.Split(stamped));

    [Theory]
    [InlineData("0.91.11", "29f2105", "v0.91.11 · 29f2105")]
    [InlineData("0.91.0", "", "v0.91.0")]
    public void The_label_shows_the_version_and_commit(string version, string commit, string expected) =>
        Assert.Equal(expected, BuildInfo.Describe(version, commit));

    [Fact]
    public void The_tooltip_tells_when_it_was_built_and_what_the_star_means()
    {
        Assert.Equal("Built 2026-09-29 17:50", BuildInfo.Explain("29f2105", "2026-09-29 17:50"));
        Assert.Equal("Built 2026-09-29 17:50\n* with changes that weren't committed yet",
                     BuildInfo.Explain("29f2105*", "2026-09-29 17:50"));
        Assert.Equal("Build time unknown", BuildInfo.Explain("", ""));
    }

    [Fact]
    public void Herald_is_stamped_with_this_version()
    {
        Assert.StartsWith("0.91.", BuildInfo.Version);
        Assert.Matches(@"^[0-9a-f]{7}\*?$", BuildInfo.Commit);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", BuildInfo.BuildTime);
    }
}

public class AppLogTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private string LogPath => _dir.File("herald.log");
    private string OldLogPath => _dir.File("herald.old.log");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Writes_one_line_per_event_with_the_error_indented_below()
    {
        var log = new AppLog(LogPath, OldLogPath);

        log.Write("hook", "Something happened");
        log.Write("speech", "It broke", new InvalidOperationException("boom"));

        var lines = File.ReadAllLines(LogPath);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \| hook \| Something happened$", lines[0]);
        Assert.EndsWith("| speech | It broke", lines[1]);
        Assert.Equal("    System.InvalidOperationException: boom", lines[2]);
    }

    [Fact]
    public void A_full_log_starts_over_and_keeps_the_previous_one()
    {
        File.WriteAllText(LogPath, new string('x', 600 * 1024));
        var log = new AppLog(LogPath, OldLogPath);

        log.Write("herald", "Fresh start");

        Assert.Single(File.ReadAllLines(LogPath));
        Assert.Equal(600 * 1024, new FileInfo(OldLogPath).Length);
    }

    [Theory]
    [InlineData(null, "\"\"")]
    [InlineData("Short", "\"Short\"")]
    [InlineData("Two\nlines", "\"Two lines\"")]
    [InlineData("A rather long message", "\"A rather lon…\"")]
    public void Excerpts_are_short_single_lines(string? text, string expected) =>
        Assert.Equal(expected, AppLog.Excerpt(text, 12));
}
