using System.IO;
using Herald.Services;
using Herald.Services.Engines;
using Herald.Services.Engines.Kokoro;

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
        Assert.Contains(_engines.Kokoro.MissingParts, p => p.Contains("espeak-ng"));
        Assert.Contains(_engines.Kokoro.MissingParts, p => p.Contains("kokoro-v1.0.onnx"));
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

    [Fact]
    public void Takes_espeak_over_from_the_python_environment_an_earlier_herald_installed()
    {
        using var dir = new TempFolder();
        var old = Path.Combine(dir.Path, "kokoro", "venv", "Lib", "site-packages", "espeakng_loader");
        Directory.CreateDirectory(Path.Combine(old, "espeak-ng-data", "voices"));
        File.WriteAllText(Path.Combine(old, "espeak-ng.dll"), "dll");
        File.WriteAllText(Path.Combine(old, "espeak-ng-data", "voices", "en"), "voice");

        var kokoro = new KokoroEngine(dir.Path);

        var taken = Path.Combine(dir.Path, "kokoro", "espeak");
        Assert.Equal("dll", File.ReadAllText(Path.Combine(taken, "espeak-ng.dll")));
        Assert.Equal("voice", File.ReadAllText(Path.Combine(taken, "espeak-ng-data", "voices", "en")));
        Assert.DoesNotContain(kokoro.MissingParts, p => p.Contains("espeak-ng"));
    }
}

public class KokoroErrorTests
{
    /// <summary>An engines folder whose Kokoro files are all there but aren't what they claim to be.</summary>
    private static TempFolder BrokenKokoro()
    {
        var dir = new TempFolder();
        var kokoro = Path.Combine(dir.Path, "kokoro");
        Directory.CreateDirectory(Path.Combine(kokoro, "models"));
        Directory.CreateDirectory(Path.Combine(kokoro, "espeak", "espeak-ng-data"));
        File.WriteAllText(Path.Combine(kokoro, "models", "kokoro-v1.0.onnx"), "not a model");
        File.WriteAllText(Path.Combine(kokoro, "models", "voices-v1.0.bin"), "not voices");
        File.WriteAllText(Path.Combine(kokoro, "espeak", "espeak-ng.dll"), "not a dll");
        return dir;
    }

    [Fact]
    public async Task A_kokoro_that_cant_load_says_why_and_lets_the_windows_voice_take_over()
    {
        using var dir = BrokenKokoro();
        var logPath = Path.Combine(dir.Path, "herald.log");
        using var engines = new EngineRegistry(dir.Path, new AppLog(logPath, logPath + ".old"));
        Assert.True(engines.Kokoro.IsReady);

        Assert.False(await engines.Kokoro.SynthesizeAsync("Hello", "am_michael", 1.0, Path.Combine(dir.Path, "out.wav"), CancellationToken.None));

        Assert.False(engines.Kokoro.IsReady);
        Assert.Contains(engines.Kokoro.MissingParts, p => p.Contains("couldn't be loaded"));
        Assert.Contains("| kokoro | Couldn't load the Kokoro model", File.ReadAllText(logPath));
        Assert.Same(engines.Windows, engines.Resolve("kokoro", "am_michael").Engine);

        // Checking again (e.g. after reinstalling) gives it another try.
        engines.Kokoro.Refresh();
        Assert.True(engines.Kokoro.IsReady);
    }

    [Fact]
    public async Task A_cancelled_request_is_simply_not_spoken()
    {
        using var dir = BrokenKokoro();
        using var kokoro = new KokoroEngine(dir.Path);

        Assert.False(await kokoro.SynthesizeAsync("Hello", "am_michael", 1.0, "out.wav", new CancellationToken(canceled: true)));
        // Nothing was even tried.
        Assert.True(kokoro.IsReady);
    }

    /// <summary>The real Kokoro install on this PC, if there is one.</summary>
    private static string RealEngines()
    {
        var engines = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Herald", "engines");
        if (!File.Exists(Path.Combine(engines, "kokoro", "models", "kokoro-v1.0.onnx")) ||
            !EspeakNg.IsInstalledIn(Path.Combine(engines, "kokoro", "espeak")))
        {
            Assert.Skip("Kokoro isn't installed on this PC");
        }
        return engines;
    }

    [Fact]
    public async Task Bad_requests_fail_on_their_own_and_are_logged()
    {
        using var dir = new TempFolder();
        var logPath = Path.Combine(dir.Path, "herald.log");
        using var kokoro = new KokoroEngine(RealEngines(), new AppLog(logPath, logPath + ".old"));

        Assert.False(await kokoro.SynthesizeAsync("Hello", "xx_nobody", 1.0, Path.Combine(dir.Path, "a.wav"), CancellationToken.None));
        Assert.False(await kokoro.SynthesizeAsync("|||", "am_michael", 1.0, Path.Combine(dir.Path, "b.wav"), CancellationToken.None));

        // Neither breaks Kokoro for the next message.
        Assert.True(await kokoro.SynthesizeAsync("Hello", "am_michael", 1.0, Path.Combine(dir.Path, "c.wav"), CancellationToken.None));
        Assert.True(kokoro.IsReady);
        var log = File.ReadAllText(logPath);
        Assert.Contains("Couldn't speak \"Hello\" with voice xx_nobody", log);
        Assert.Contains("Couldn't speak \"|||\" with voice am_michael", log);
    }

    [Fact]
    public async Task Closing_during_a_synthesis_waits_for_it_to_finish_before_freeing_the_model()
    {
        using var dir = new TempFolder();
        var logPath = Path.Combine(dir.Path, "herald.log");
        var kokoro = new KokoroEngine(RealEngines(), new AppLog(logPath, logPath + ".old"));
        // Loaded first, so closing lands in the middle of the synthesis, not the loading.
        Assert.True(await kokoro.SynthesizeAsync("Ready", "am_michael", 1.0, Path.Combine(dir.Path, "ready.wav"), CancellationToken.None));
        var text = String.Join(" ", Enumerable.Repeat("This is a long sentence that keeps the model busy for a while.", 20));

        var speaking = kokoro.SynthesizeAsync(text, "am_michael", 1.0, Path.Combine(dir.Path, "long.wav"), CancellationToken.None);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        kokoro.Dispose();

        Assert.True(await speaking, File.Exists(logPath) ? File.ReadAllText(logPath) : "no log");
        Assert.False(await kokoro.SynthesizeAsync("Hello", "am_michael", 1.0, Path.Combine(dir.Path, "after.wav"), CancellationToken.None));
    }
}

public class KokoroPartsTests
{
    // A stand-in for espeak: words as "phonemes" in capitals, the way it hands out one clause.
    private static string FakeEspeak(string text, string language) => String.Join(" ", text.Split(' ').Select(w => w.ToUpperInvariant()));

    private static readonly IReadOnlyDictionary<char, int> Letters =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ ,.!?;:\"()—…".Select((c, i) => (c, i + 1)).ToDictionary(p => p.c, p => p.Item2);

    [Theory]
    [InlineData("Hello world", "HELLO WORLD")]
    [InlineData("Hello, world!", "HELLO, WORLD!")]
    [InlineData("(Quiet) please.", "(QUIET) PLEASE.")]
    [InlineData("It costs 3.50 now", "IT COSTS 3.50 NOW")]
    [InlineData("...", "...")]
    [InlineData("  ", "")]
    public void Punctuation_is_taken_out_for_espeak_and_put_back(string text, string expected)
    {
        // "3.50" keeps its point: between digits it isn't punctuation, and the fake espeak
        // passes it through, but the vocabulary here has no digits.
        var phonemizer = new KokoroPhonemizer(FakeEspeak, Letters);

        Assert.Equal(expected.Replace("3.50", "."), phonemizer.Phonemize(text, "en-us"));
    }

    [Fact]
    public void Silence_before_and_after_the_speech_is_trimmed()
    {
        var audio = new float[24000];
        for (var i = 8000; i < 16000; i++) audio[i] = 0.5f * MathF.Sin(i * 0.1f);

        var trimmed = KokoroAudio.Trim(audio);

        // Frames of 2048 samples centred every 512, so up to a frame of quiet stays on each side.
        Assert.InRange(trimmed.Length, 8000, 8000 + 2 * 2048);
        // As in librosa, pure silence has no loudest part to measure against and stays as it is.
        Assert.Equal(5000, KokoroAudio.Trim(new float[5000]).Length);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.5)]
    public void Speeding_up_shortens_the_audio_by_the_rate(double rate)
    {
        var audio = new float[48000];
        for (var i = 0; i < audio.Length; i++) audio[i] = 0.3f * MathF.Sin(i * 0.05f);

        var faster = KokoroAudio.SpeedUp(audio, rate);

        Assert.Equal((int)(audio.Length / rate), faster.Length);
        Assert.InRange(faster.Max(), 0.25f, 0.35f);
    }

    [Fact]
    public void Npy_arrays_are_read_as_floats()
    {
        var header = "{'descr': '<f4', 'fortran_order': False, 'shape': (2, 2), }".PadRight(118) + "\n";
        using var stream = new MemoryStream();
        stream.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0]);
        stream.Write(BitConverter.GetBytes((ushort)header.Length));
        stream.Write(System.Text.Encoding.ASCII.GetBytes(header));
        foreach (var value in new[] { 1.5f, -2f, 0.25f, 3f }) stream.Write(BitConverter.GetBytes(value));
        stream.Position = 0;

        Assert.Equal([1.5f, -2f, 0.25f, 3f], KokoroVoices.ReadNpyFloats(stream));
    }

    [Fact]
    public void Audio_is_saved_as_16_bit_wav_at_kokoros_rate()
    {
        using var dir = new TempFolder();
        var path = Path.Combine(dir.Path, "out.wav");

        KokoroAudio.WriteWav(path, [0f, 0.5f, -1f, 2f]);

        using var reader = new NAudio.Wave.WaveFileReader(path);
        Assert.Equal(24000, reader.WaveFormat.SampleRate);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        var samples = new byte[8];
        reader.ReadExactly(samples);
        Assert.Equal(new short[] { 0, 16384, -32767, 32767 },
                     Enumerable.Range(0, 4).Select(i => BitConverter.ToInt16(samples, i * 2)));
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
