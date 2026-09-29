using System.IO;
using Herald.Services.Engines.Kokoro;
using System.Text.Json;

namespace Herald.Tests;

/// <summary>
/// Herald's C# Kokoro against what the Python version (kokoro-onnx 0.6.1) made of the same
/// texts, recorded by tools/kokoro_reference.py into TestData/kokoro-reference.json.
/// Needs espeak-ng on this PC; skipped where it isn't installed.
/// </summary>
public class KokoroParityTests
{
    private record Batch(string Phonemes, int[] Tokens, double Pause);
    private record PhonemeCase(string Lang, string Text, string Phonemes, Batch[] Batches);
    private record Reference(PhonemeCase[] Phonemes);

    private static readonly Reference Expected = JsonSerializer.Deserialize<Reference>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "kokoro-reference.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// <summary>
    /// espeak-ng's folder: HERALD_ESPEAK_DIR, Herald's Kokoro install, or the Python
    /// package the reference was made with.
    /// </summary>
    internal static string? EspeakFolder()
    {
        var kokoro = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Herald", "engines", "kokoro");
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("HERALD_ESPEAK_DIR"),
            Path.Combine(kokoro, "espeak"),
            Path.Combine(kokoro, "venv", "Lib", "site-packages", "espeakng_loader")
        ];
        return candidates.FirstOrDefault(c => c != null && EspeakNg.IsInstalledIn(c));
    }

    private static readonly Lazy<EspeakNg?> Espeak = new(() =>
        EspeakFolder() is { } folder
            ? EspeakNg.Open(Path.Combine(folder, "espeak-ng.dll"), Path.Combine(folder, "espeak-ng-data"))
            : null);

    private static KokoroPhonemizer Phonemizer()
    {
        var espeak = Espeak.Value;
        if (espeak == null) Assert.Skip("espeak-ng isn't installed on this PC");
        return new KokoroPhonemizer(espeak.TextToPhonemes, KokoroVocabulary.Tokens);
    }

    [Fact]
    public void Phonemes_match_the_python_version_exactly()
    {
        var phonemizer = Phonemizer();

        var differences = Expected.Phonemes
            .Select(c => (c.Lang, c.Text, Expected: c.Phonemes, Actual: phonemizer.Phonemize(c.Text, c.Lang)))
            .Where(c => c.Expected != c.Actual)
            .Select(c => $"[{c.Lang}] {c.Text}\n  python: {c.Expected}\n  herald: {c.Actual}")
            .ToList();

        Assert.True(differences.Count == 0, String.Join("\n", differences));
    }

    [Fact]
    public void Batches_tokens_and_pauses_match_the_python_version_exactly()
    {
        var tokenize = new KokoroSynthesizerTokens();

        foreach (var c in Expected.Phonemes)
        {
            var batches = KokoroSynthesizer.SplitPhonemes(String.Join(" ", c.Phonemes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
            var expected = c.Batches.Select((b, i) => (b.Phonemes, String.Join(",", b.Tokens), i < c.Batches.Length - 1 ? b.Pause : 0.0));
            var actual = batches.Select((b, i) => (b, String.Join(",", tokenize.Of(b)), i < batches.Count - 1 ? KokoroSynthesizer.PauseAfter(b) : 0.0));
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Tokens the way the synthesizer makes them, without loading the model.</summary>
    private sealed class KokoroSynthesizerTokens
    {
        public IEnumerable<int> Of(string phonemes) =>
            phonemes.Where(KokoroVocabulary.Tokens.ContainsKey).Select(p => KokoroVocabulary.Tokens[p]);
    }

    private record AudioCase(string Name, string Voice, string Lang, double Speed, string Text, int Samples);
    private record AudioReference(AudioCase[] Audio);

    /// <summary>
    /// The audio references (.npy, too big for the repository) are only on a PC where
    /// tools/kokoro_reference.py was run: HERALD_KOKORO_REFERENCE, or %TEMP%\kokoro-ref.
    /// </summary>
    [Fact]
    public void Audio_matches_the_python_version()
    {
        var referenceDir = Environment.GetEnvironmentVariable("HERALD_KOKORO_REFERENCE") ?? Path.Combine(Path.GetTempPath(), "kokoro-ref");
        var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Herald", "engines", "kokoro", "models");
        if (!File.Exists(Path.Combine(referenceDir, "kokoro-reference.json")) || !File.Exists(Path.Combine(models, "kokoro-v1.0.onnx")))
        {
            Assert.Skip("The Kokoro audio references or model files aren't on this PC");
        }

        using var kokoro = new KokoroSynthesizer(Path.Combine(models, "kokoro-v1.0.onnx"), Path.Combine(models, "voices-v1.0.bin"),
                                                 Phonemizer(), KokoroVocabulary.Tokens);
        var cases = JsonSerializer.Deserialize<AudioReference>(File.ReadAllText(Path.Combine(referenceDir, "kokoro-reference.json")),
                                                              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Audio;
        var report = new List<string>();
        foreach (var c in cases)
        {
            using var file = File.OpenRead(Path.Combine(referenceDir, c.Name + ".npy"));
            var expected = KokoroVoices.ReadNpyFloats(file);
            var actual = kokoro.Create(c.Text, c.Voice, c.Lang, (float)c.Speed);

            var length = Math.Min(expected.Length, actual.Length);
            var maxDifference = Enumerable.Range(0, length).Max(i => Math.Abs(expected[i] - actual[i]));
            report.Add($"{c.Name}: python {expected.Length} samples, herald {actual.Length}, largest difference {maxDifference:0.000000}");

            Assert.True(expected.Length == actual.Length, String.Join("\n", report));
            // Far below anything audible (one step of 16-bit audio is 0.00003).
            Assert.True(maxDifference < 0.001, String.Join("\n", report));
        }
        TestContext.Current.SendDiagnosticMessage(String.Join("\n", report));
    }
}
