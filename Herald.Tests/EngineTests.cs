using System.IO;
using Herald.Services.Engines;

namespace Herald.Tests;

public class EngineRegistryTests : IDisposable
{
    // An empty engines folder: Kokoro and Piper aren't installed, only Windows' voices are there.
    private readonly TempFolder _dir = new();
    private readonly EngineRegistry _engines;

    public EngineRegistryTests() => _engines = new EngineRegistry(_dir.Path);

    public void Dispose()
    {
        _engines.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void Finds_engines_by_id_ignoring_case()
    {
        Assert.Same(_engines.Kokoro, _engines.Find("KOKORO"));
        Assert.Same(_engines.Piper, _engines.Find("piper"));
        Assert.Null(_engines.Find("nope"));
        Assert.Null(_engines.Find(null));
    }

    [Fact]
    public void Uninstalled_engines_report_what_is_missing()
    {
        Assert.False(_engines.Kokoro.IsReady);
        Assert.Contains(_engines.Kokoro.MissingParts, p => p.Contains("kokoro-onnx"));
        Assert.False(_engines.Piper.IsReady);
        Assert.Contains(_engines.Piper.MissingParts, p => p.Contains("Piper program"));
    }

    [Fact]
    public void An_uninstalled_engine_falls_back_to_the_windows_default_voice()
    {
        var (engine, voice) = _engines.Resolve("kokoro", "af_bella");

        Assert.Same(_engines.Windows, engine);
        Assert.Equal(_engines.Windows.DefaultVoiceId, voice);
    }

    [Fact]
    public void An_unknown_engine_falls_back_to_windows()
    {
        Assert.Same(_engines.Windows, _engines.Resolve("gone-engine", "x").Engine);
        Assert.Same(_engines.Windows, _engines.Resolve(null, null).Engine);
    }

    [Fact]
    public void An_unknown_voice_uses_the_engine_default()
    {
        Assert.SkipUnless(_engines.Windows.IsReady, "No Windows voices installed");

        Assert.Equal(_engines.Windows.DefaultVoiceId, _engines.Resolve("windows", "no-such-voice").VoiceId);
    }

    [Fact]
    public void A_known_voice_is_kept()
    {
        Assert.SkipUnless(_engines.Windows.Voices.Count > 0, "No Windows voices installed");
        var voice = _engines.Windows.Voices[^1].Id;

        Assert.Equal((_engines.Windows, voice), _engines.Resolve("windows", voice));
    }
}

public class KokoroEngineTests
{
    [Fact]
    public void Lists_every_voice_with_a_readable_name_and_language()
    {
        var kokoro = new KokoroEngine(Path.Combine(Path.GetTempPath(), "herald-tests", "no-kokoro"));

        Assert.Equal(54, kokoro.Voices.Count);
        Assert.Contains(kokoro.Voices, v => v.Id == kokoro.DefaultVoiceId);
        var michael = kokoro.Voices.Single(v => v.Id == "am_michael");
        Assert.Equal("Michael (American English, male)", michael.DisplayName);
        Assert.Equal("en-us", michael.Language);
        Assert.Equal("Alice (British English, female)", kokoro.Voices.Single(v => v.Id == "bf_alice").DisplayName);
        Assert.Equal("cmn", kokoro.Voices.Single(v => v.Id == "zf_xiaobei").Language);
    }

    [Fact]
    public async Task Does_not_try_to_synthesize_before_it_is_installed()
    {
        var kokoro = new KokoroEngine(Path.Combine(Path.GetTempPath(), "herald-tests", "no-kokoro"));

        Assert.False(await kokoro.SynthesizeAsync("hello", "am_michael", 1.0, "out.wav", CancellationToken.None));
    }
}

public class PiperEngineTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private string PiperDir => Path.Combine(_dir.Path, "piper");
    private string VoicesDir => Path.Combine(PiperDir, "voices");

    public void Dispose() => _dir.Dispose();

    // A trimmed-down copy of Hugging Face's voices.json.
    private const string Catalog = """
        {
          "sv_SE-nst-medium": {
            "name": "nst", "quality": "medium",
            "language": { "name_english": "Swedish", "country_english": "Sweden", "name_native": "Svenska" },
            "files": {
              "sv/sv_SE/nst/medium/sv_SE-nst-medium.onnx": { "size_bytes": 63201294 },
              "sv/sv_SE/nst/medium/sv_SE-nst-medium.onnx.json": { "size_bytes": 4997 }
            }
          },
          "en_US-lessac-medium": {
            "name": "lessac", "quality": "medium",
            "language": { "name_english": "English", "country_english": "United States", "name_native": "English" },
            "files": {
              "en/en_US/lessac/medium/en_US-lessac-medium.onnx": { "size_bytes": 63201294 },
              "en/en_US/lessac/medium/en_US-lessac-medium.onnx.json": { "size_bytes": 4885 }
            }
          },
          "broken-entry": {
            "name": "x", "quality": "low",
            "language": { "name_english": "X", "country_english": "Y", "name_native": "Z" },
            "files": { "only-a-readme.txt": { "size_bytes": 1 } }
          }
        }
        """;

    private void WriteCatalog()
    {
        Directory.CreateDirectory(PiperDir);
        File.WriteAllText(Path.Combine(PiperDir, "voices.json"), Catalog);
    }

    private void InstallVoiceFiles(string key, bool withConfig = true)
    {
        Directory.CreateDirectory(VoicesDir);
        File.WriteAllText(Path.Combine(VoicesDir, key + ".onnx"), "model");
        if (withConfig) File.WriteAllText(Path.Combine(VoicesDir, key + ".onnx.json"), "{}");
    }

    [Fact]
    public async Task Reads_the_cached_catalog_sorted_by_language()
    {
        WriteCatalog();
        var piper = new PiperEngine(_dir.Path);

        var catalog = await piper.GetCatalogAsync(new Progress<string>(), CancellationToken.None);

        Assert.Equal(["en_US-lessac-medium", "sv_SE-nst-medium"], catalog.Select(v => v.Key));
        var swedish = catalog[1];
        Assert.Equal("Swedish (Sweden) / Svenska - nst, medium (63 MB)", swedish.Display);
        Assert.Equal("sv/sv_SE/nst/medium/sv_SE-nst-medium.onnx", swedish.ModelUrlPath);
    }

    [Fact]
    public void Installed_voices_are_found_and_named_from_the_catalog()
    {
        WriteCatalog();
        InstallVoiceFiles("sv_SE-nst-medium");
        InstallVoiceFiles("de_DE-thorsten-low");

        var piper = new PiperEngine(_dir.Path);

        Assert.Equal(2, piper.Voices.Count);
        var swedish = piper.Voices.Single(v => v.Id == "sv_SE-nst-medium");
        Assert.Equal("Swedish (Sweden) / Svenska - nst, medium", swedish.DisplayName);
        Assert.Equal("sv-SE", swedish.Language);
        // Not in the catalog: shown by its key.
        Assert.Equal("de_DE-thorsten-low", piper.Voices.Single(v => v.Language == "de-DE").DisplayName);
    }

    [Fact]
    public void A_voice_without_its_config_file_is_not_usable()
    {
        InstallVoiceFiles("sv_SE-nst-medium", withConfig: false);

        var piper = new PiperEngine(_dir.Path);

        Assert.Empty(piper.Voices);
        Assert.Contains("at least one voice", piper.MissingParts);
    }

    [Fact]
    public void Voices_alone_are_not_enough_without_the_program()
    {
        InstallVoiceFiles("sv_SE-nst-medium");

        var piper = new PiperEngine(_dir.Path);

        Assert.False(piper.IsReady);
        Assert.Equal(["Piper program (about 20 MB)"], piper.MissingParts);
    }

    [Fact]
    public void Removing_a_voice_deletes_its_files()
    {
        InstallVoiceFiles("sv_SE-nst-medium");
        var piper = new PiperEngine(_dir.Path);

        piper.RemoveVoice("sv_SE-nst-medium");

        Assert.Empty(piper.Voices);
        Assert.Empty(Directory.GetFiles(VoicesDir));
    }

    [Fact]
    public void A_corrupt_catalog_is_treated_as_missing()
    {
        Directory.CreateDirectory(PiperDir);
        File.WriteAllText(Path.Combine(PiperDir, "voices.json"), "{ nope");
        InstallVoiceFiles("sv_SE-nst-medium");

        var piper = new PiperEngine(_dir.Path);

        Assert.Equal("sv_SE-nst-medium", piper.Voices.Single().DisplayName);
    }
}
