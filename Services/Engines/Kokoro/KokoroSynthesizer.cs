using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Herald.Services.Engines.Kokoro;

/// <summary>
/// Kokoro v1.0 in-process: text to phonemes (espeak-ng), phonemes to batches of tokens,
/// tokens to audio (ONNX Runtime). A port of kokoro-onnx 0.6.1's Kokoro.create() with its
/// defaults, so it sounds as the Python version did.
/// </summary>
public sealed class KokoroSynthesizer : IDisposable
{
    /// <summary>The model reads at most this many phonemes at once.</summary>
    public const int MaxPhonemes = 510;

    /// <summary>
    /// Above this the model drops short words: at 1.9 "The build finished" came out as
    /// "Bill finished". Faster speeds are reached by speeding up the finished audio instead.
    /// </summary>
    public const double MaxModelSpeed = 1.2;

    // Silence after a batch ending a sentence or a clause, in seconds.
    private const double SentencePause = 0.25, ClausePause = 0.1;
    private const string SentenceMarks = ".!?…", ClauseMarks = ",;:";

    // Least to most disruptive place to cut: sentence, clause, word. Marks stay with the text before them.
    private static readonly Regex[] Boundaries =
    [
        new(@"(?<=[.!?…])\s+", RegexOptions.Compiled),
        new(@"(?<=[,;:])\s+", RegexOptions.Compiled),
        new(@"\s+", RegexOptions.Compiled)
    ];
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly InferenceSession _session;
    private readonly KokoroVoices _voices;
    private readonly KokoroPhonemizer _phonemizer;
    private readonly IReadOnlyDictionary<char, int> _vocabulary;

    public KokoroSynthesizer(string modelPath, string voicesPath, KokoroPhonemizer phonemizer, IReadOnlyDictionary<char, int> vocabulary)
    {
        _session = new InferenceSession(modelPath);
        _voices = KokoroVoices.Load(voicesPath);
        _phonemizer = phonemizer;
        _vocabulary = vocabulary;
    }

    public IReadOnlyCollection<string> Voices => _voices.Names;

    /// <summary>
    /// Speech for the text at Herald's speed (1.0 normal): Kokoro speaks at up to
    /// <see cref="MaxModelSpeed"/>, the rest is done by speeding the audio up.
    /// </summary>
    public float[] Speak(string text, string voice, string language, double speed)
    {
        var modelSpeed = Math.Min(speed, MaxModelSpeed);
        var audio = Create(text, voice, language, (float)modelSpeed);
        return speed > modelSpeed ? KokoroAudio.SpeedUp(audio, speed / modelSpeed) : audio;
    }

    /// <summary>kokoro-onnx's create(): each batch trimmed, with a pause after it as its last mark asks.</summary>
    public float[] Create(string text, string voice, string language, float speed)
    {
        var phonemes = Whitespace.Replace(_phonemizer.Phonemize(text, language).Trim(), " ");
        var batches = SplitPhonemes(phonemes);
        if (batches.Count == 0) throw new ArgumentException($"Nothing to say: \"{text}\" gave no phonemes", nameof(text));

        var parts = new List<float[]>();
        for (var i = 0; i < batches.Count; i++)
        {
            var tokens = Tokenize(batches[i]);
            if (tokens.Count == 0) throw new ArgumentException($"None of the phonemes \"{batches[i]}\" are in the model's vocabulary", nameof(text));

            parts.Add(KokoroAudio.Trim(Infer(tokens, _voices.StyleFor(voice, tokens.Count), speed)));
            var pause = i < batches.Count - 1 ? PauseAfter(batches[i]) : 0;
            if (pause > 0) parts.Add(new float[(int)(pause * KokoroAudio.SampleRate)]);
        }
        return [.. parts.SelectMany(p => p)];
    }

    /// <summary>The model's token numbers; phonemes it doesn't know are left out.</summary>
    public List<int> Tokenize(string phonemes) =>
        [.. phonemes.Where(_vocabulary.ContainsKey).Select(p => _vocabulary[p])];

    private float[] Infer(List<int> tokens, float[] style, float speed)
    {
        // The model expects a padding token (0) on both sides.
        var ids = new long[tokens.Count + 2];
        for (var i = 0; i < tokens.Count; i++) ids[i + 1] = tokens[i];

        using var results = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("tokens", new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("style", new DenseTensor<float>(style, [1, KokoroVoices.StyleLength])),
            NamedOnnxValue.CreateFromTensor("speed", new DenseTensor<float>(new[] { speed }, [1]))
        ]);
        return results.First().AsEnumerable<float>().ToArray();
    }

    /// <summary>Seconds of silence after a batch ending with this text.</summary>
    public static double PauseAfter(string batch)
    {
        var trimmed = batch.TrimEnd();
        if (trimmed.Length == 0) return 0;
        var mark = trimmed[^1];
        if (SentenceMarks.Contains(mark)) return SentencePause;
        if (ClauseMarks.Contains(mark)) return ClausePause;
        return 0;
    }

    /// <summary>
    /// Phonemes in batches of at most <see cref="MaxPhonemes"/>, as few as possible and as
    /// even as possible: a short batch is spoken at a different rate and loudness than its
    /// neighbours (kokoro-onnx's split_phonemes).
    /// </summary>
    public static List<string> SplitPhonemes(string phonemes, int maxLength = MaxPhonemes)
    {
        var atoms = Atoms(phonemes.Trim(), maxLength, 0).ToList();
        if (atoms.Count == 0) return [];

        var lengths = atoms.Select(a => a.Length).ToList();
        var fewest = Pack(lengths, maxLength).Count;
        int low = lengths.Max(), high = maxLength;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Pack(lengths, middle).Count <= fewest) high = middle;
            else low = middle + 1;
        }
        return [.. Pack(lengths, low).Select(r => String.Join(" ", atoms.Skip(r.Start).Take(r.End - r.Start)))];
    }

    /// <summary>Pieces of the phonemes no longer than the limit, cut at the least disruptive place.</summary>
    private static IEnumerable<string> Atoms(string phonemes, int maxLength, int level)
    {
        if (phonemes.Length <= maxLength)
        {
            if (phonemes.Length > 0) yield return phonemes;
            yield break;
        }
        for (var index = level; index < Boundaries.Length; index++)
        {
            var pieces = Boundaries[index].Split(phonemes);
            if (pieces.Length <= 1) continue;
            foreach (var piece in pieces)
            {
                foreach (var atom in Atoms(piece.Trim(), maxLength, index + 1)) yield return atom;
            }
            yield break;
        }
        // One unbroken run longer than the limit: sliced rather than losing the end.
        for (var start = 0; start < phonemes.Length; start += maxLength)
        {
            yield return phonemes.Substring(start, Math.Min(maxLength, phonemes.Length - start));
        }
    }

    /// <summary>Consecutive atoms grouped into batches within the limit, as index ranges.</summary>
    private static List<(int Start, int End)> Pack(List<int> lengths, int limit)
    {
        var batches = new List<(int, int)>();
        int start = 0, size = 0;
        for (var index = 0; index < lengths.Count; index++)
        {
            var candidate = index == start ? lengths[index] : size + 1 + lengths[index];
            if (candidate > limit && index > start)
            {
                batches.Add((start, index));
                start = index;
                size = lengths[index];
            }
            else
            {
                size = candidate;
            }
        }
        if (lengths.Count > 0) batches.Add((start, lengths.Count));
        return batches;
    }

    public void Dispose() => _session.Dispose();
}
