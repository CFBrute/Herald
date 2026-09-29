using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Herald.Services.Engines.Kokoro;

/// <summary>
/// The phonemes Kokoro v1.0 knows and their token numbers (kokoro-onnx's config.json,
/// embedded in Herald.exe as kokoro-vocab.json).
/// </summary>
public static class KokoroVocabulary
{
    private static readonly Lazy<IReadOnlyDictionary<char, int>> Instance = new(Load);

    public static IReadOnlyDictionary<char, int> Tokens => Instance.Value;

    private static IReadOnlyDictionary<char, int> Load()
    {
        using var stream = typeof(KokoroVocabulary).Assembly.GetManifestResourceStream("kokoro-vocab.json")
                           ?? throw new InvalidOperationException("kokoro-vocab.json is missing from Herald.exe");
        var entries = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)!;
        // Every phoneme is a single character, so text can be tokenized character by character.
        return entries.ToDictionary(e => e.Key.Length == 1 ? e.Key[0] : throw new InvalidOperationException($"\"{e.Key}\" isn't one character"),
                                    e => e.Value);
    }
}
