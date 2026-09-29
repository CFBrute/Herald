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
