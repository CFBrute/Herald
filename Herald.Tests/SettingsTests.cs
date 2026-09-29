using System.IO;
using System.Text.Json.Nodes;
using Herald.Services;

namespace Herald.Tests;

public class SafeFileTests : IDisposable
{
    private readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    private record Sample(string Name, int Count);

    [Fact]
    public void Round_trips_a_value()
    {
        var path = _dir.File("sample.json");

        SafeFile.WriteJson(path, new Sample("a", 3));

        Assert.Equal(new Sample("a", 3), SafeFile.ReadJson<Sample>(path));
    }

    [Fact]
    public void Reading_a_missing_or_corrupt_file_gives_null()
    {
        var corrupt = _dir.File("corrupt.json");
        File.WriteAllText(corrupt, "{ not json");

        Assert.Null(SafeFile.ReadJson<Sample>(_dir.File("missing.json")));
        Assert.Null(SafeFile.ReadJson<Sample>(corrupt));
    }

    [Fact]
    public void Writing_replaces_the_file_and_leaves_no_temp_files()
    {
        var path = _dir.File("sample.json");
        SafeFile.WriteJson(path, new Sample("old", 1));

        SafeFile.WriteJson(path, new Sample("new", 2));

        Assert.Equal("new", SafeFile.ReadJson<Sample>(path)?.Name);
        Assert.Equal(["sample.json"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void A_failed_write_keeps_the_old_file()
    {
        var path = _dir.File("sample.json");
        SafeFile.WriteJson(path, new Sample("kept", 1));

        // Locked by another writer: the write fails quietly and changes nothing.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            SafeFile.WriteJson(path, new Sample("lost", 2));
        }

        Assert.Equal("kept", SafeFile.ReadJson<Sample>(path)?.Name);
        Assert.Single(Directory.GetFiles(_dir.Path));
    }

    [Fact]
    public void Deleting_a_missing_file_is_fine()
    {
        SafeFile.TryDelete(_dir.File("nothing-here.wav"));
    }
}

public class AppSettingsTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private string FilePath => _dir.File("engine-settings.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Starts_with_defaults_when_there_is_no_file()
    {
        var settings = new AppSettings(FilePath);

        Assert.Equal(150, settings.SpeedPercent);
        Assert.Equal(300, settings.ChunkThreshold);
        Assert.Equal(250, settings.ChunkTargetLength);
        Assert.Equal(200, settings.HistoryLimit);
        Assert.Equal(100, settings.Volume);
        Assert.Equal(100, settings.UiScalePercent);
        Assert.Equal(ThemeChoice.System, settings.Theme);
        Assert.True(settings.AskToConnectClaude);
        Assert.False(settings.ReadClipboardAutomatically);
        Assert.False(settings.TrayHintShown);
    }

    [Fact]
    public void Changes_are_saved_and_read_back()
    {
        var settings = new AppSettings(FilePath)
        {
            SpeedPercent = 170,
            Volume = 40,
            Theme = ThemeChoice.Dark,
            ReadClipboardAutomatically = true,
            HistoryLimit = 50,
            HookPort = 9000
        };

        var reloaded = new AppSettings(FilePath);

        Assert.Equal(170, reloaded.SpeedPercent);
        Assert.Equal(40, reloaded.Volume);
        Assert.Equal(ThemeChoice.Dark, reloaded.Theme);
        Assert.True(reloaded.ReadClipboardAutomatically);
        Assert.Equal(50, reloaded.HistoryLimit);
        Assert.Equal(9000, reloaded.HookPort);
    }

    [Theory]
    [InlineData(nameof(AppSettings.SpeedPercent), 20, 50)]
    [InlineData(nameof(AppSettings.SpeedPercent), 500, 300)]
    [InlineData(nameof(AppSettings.Volume), -5, 0)]
    [InlineData(nameof(AppSettings.Volume), 150, 100)]
    [InlineData(nameof(AppSettings.UiScalePercent), 10, 70)]
    [InlineData(nameof(AppSettings.UiScalePercent), 300, 130)]
    [InlineData(nameof(AppSettings.HistoryLimit), 1, 10)]
    [InlineData(nameof(AppSettings.ChunkThreshold), 10_000, 5000)]
    [InlineData(nameof(AppSettings.ChunkTargetLength), 5, 40)]
    [InlineData(nameof(AppSettings.HookPort), 80, 1024)]
    [InlineData(nameof(AppSettings.HookPort), 70000, 65535)]
    public void Values_are_kept_in_range(string property, int value, int expected)
    {
        var settings = new AppSettings(FilePath);
        var prop = typeof(AppSettings).GetProperty(property)!;

        prop.SetValue(settings, value);

        Assert.Equal(expected, prop.GetValue(settings));
    }

    [Fact]
    public void Out_of_range_values_in_the_file_are_clamped_too()
    {
        File.WriteAllText(FilePath, """{ "SpeedPercent": 999, "Volume": -20, "UiScalePercent": 5 }""");

        var settings = new AppSettings(FilePath);

        Assert.Equal(300, settings.SpeedPercent);
        Assert.Equal(0, settings.Volume);
        Assert.Equal(70, settings.UiScalePercent);
    }

    [Fact]
    public void Files_from_older_versions_keep_defaults_for_newer_options()
    {
        // Only the speed existed in the first version of this file.
        File.WriteAllText(FilePath, """{ "SpeedPercent": 130 }""");

        var settings = new AppSettings(FilePath);

        Assert.Equal(130, settings.SpeedPercent);
        Assert.Equal(300, settings.ChunkThreshold);
        Assert.Equal(200, settings.HistoryLimit);
        Assert.Equal(100, settings.Volume);
        Assert.True(settings.AskToConnectClaude);
    }

    [Fact]
    public void A_file_without_a_speed_keeps_the_default_speed()
    {
        File.WriteAllText(FilePath, """{ "Volume": 70 }""");

        Assert.Equal(150, new AppSettings(FilePath).SpeedPercent);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults()
    {
        File.WriteAllText(FilePath, "{ this is not json");

        Assert.Equal(150, new AppSettings(FilePath).SpeedPercent);
    }

    [Fact]
    public void An_unknown_theme_name_keeps_the_default()
    {
        File.WriteAllText(FilePath, """{ "SpeedPercent": 100, "Theme": "Purple" }""");

        Assert.Equal(ThemeChoice.System, new AppSettings(FilePath).Theme);
    }

    [Fact]
    public void Loading_does_not_rewrite_the_file()
    {
        const string original = """{ "SpeedPercent": 120, "Volume": 70 }""";
        File.WriteAllText(FilePath, original);

        _ = new AppSettings(FilePath);

        Assert.Equal(original, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Notifies_once_per_actual_change()
    {
        var settings = new AppSettings(FilePath);
        var changes = new List<string?>();
        settings.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        settings.Volume = 60;
        settings.Volume = 60;
        settings.Volume = 500; // clamped to 100
        settings.Volume = 100;

        Assert.Equal(["Volume", "Volume"], changes);
    }

    [Fact]
    public void Uses_the_property_names_the_file_format_depends_on()
    {
        new AppSettings(FilePath) { Volume = 55 }.Theme = ThemeChoice.Light;

        var json = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();

        Assert.Equal(
            ["SpeedPercent", "ChunkThreshold", "ChunkTargetLength", "HistoryLimit", "AskToConnectClaude",
             "ReadClipboardAutomatically", "TrayHintShown", "Theme", "Volume", "UiScalePercent", "HookPort"],
            json.Select(p => p.Key));
        Assert.Equal("Light", (string?)json["Theme"]);
    }
}
