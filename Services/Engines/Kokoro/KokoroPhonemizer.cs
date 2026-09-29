using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Herald.Services.Engines.Kokoro;

/// <summary>
/// Text to the phonemes Kokoro reads: a port of what kokoro-onnx 0.6.1 does with
/// phonemizer 3.4 (phonemize(text, lang, preserve_punctuation=True, with_stress=True)),
/// quirks included, so Herald's voices sound exactly as they did with the Python version.
/// Punctuation is taken out before espeak sees the text and put back after, since espeak
/// would otherwise drop it and Kokoro pauses on it.
/// </summary>
public sealed class KokoroPhonemizer
{
    // phonemizer's default marks. '.' and ',' only count when not between digits ("3.14").
    private static readonly Regex Marks = new(
        """(\s*(?:[;:!?¡¿—…"«»“”(){}\[\]]|(?<![0-9])[,.]|[,.](?![0-9]))+\s*)+""", RegexOptions.Compiled);
    private static readonly Regex Underscores = new("_+", RegexOptions.Compiled);

    // phonemizer splits lines on os.linesep, which is "\r\n" on Windows (a lone "\n" isn't a line break).
    private const string LineSeparator = "\r\n";

    private readonly Func<string, string, string> _textToPhonemes;
    private readonly IReadOnlyDictionary<char, int> _vocabulary;

    /// <param name="textToPhonemes">espeak for one line of text and a language: <see cref="EspeakNg.TextToPhonemes"/>.</param>
    public KokoroPhonemizer(Func<string, string, string> textToPhonemes, IReadOnlyDictionary<char, int> vocabulary)
    {
        _textToPhonemes = textToPhonemes;
        _vocabulary = vocabulary;
    }

    /// <summary>kokoro-onnx's Tokenizer.phonemize: phonemes with stress marks, only those the model knows.</summary>
    public string Phonemize(string text, string language)
    {
        var phonemes = PhonemizeLines(text.Trim(), language);
        return new string(phonemes.Where(_vocabulary.ContainsKey).ToArray()).Trim();
    }

    /// <summary>phonemizer.phonemize for a string: line by line, joined with line breaks again.</summary>
    private string PhonemizeLines(string text, string language)
    {
        var lines = text.Trim('\r', '\n').Split(LineSeparator)
            .Select(line => line.Trim('\r', '\n'))
            .Where(line => line.Trim().Length > 0)
            .ToList();
        if (lines.Count == 0) return String.Empty;

        var (chunks, marks) = Preserve(lines);
        var phonemized = chunks.Select(chunk => CleanUp(_textToPhonemes(chunk, language))).ToList();
        return String.Join(LineSeparator, Restore(phonemized, marks));
    }

    private record struct Mark(int Line, string Text, char Position);

    /// <summary>
    /// Takes the punctuation out: "hello, my world!" becomes ["hello", "my world"] with
    /// [", " inside, "!" at the end]. Position: B(eginning), I(nside), E(nd), A(ll of the line).
    /// </summary>
    private static (List<string> Chunks, List<Mark> Marks) Preserve(List<string> lines)
    {
        var chunks = new List<string>();
        var marks = new List<Mark>();
        for (var number = 0; number < lines.Count; number++)
        {
            var line = lines[number];
            var matches = Marks.Matches(line);
            if (matches.Count == 0)
            {
                chunks.Add(line);
                continue;
            }
            if (matches.Count == 1 && matches[0].Value == line)
            {
                marks.Add(new Mark(number, line, 'A'));
                continue;
            }

            var lineMarks = new List<Mark>();
            for (var i = 0; i < matches.Count; i++)
            {
                var value = matches[i].Value;
                var position = 'I';
                if (i == 0 && line.StartsWith(value, StringComparison.Ordinal)) position = 'B';
                else if (i == matches.Count - 1 && line.EndsWith(value, StringComparison.Ordinal)) position = 'E';
                lineMarks.Add(new Mark(number, value, position));
            }

            // Cut at each mark's first appearance in what's left of the line.
            foreach (var mark in lineMarks)
            {
                var at = line.IndexOf(mark.Text, StringComparison.Ordinal);
                if (at < 0)
                {
                    chunks.Add(line);
                    line = String.Empty;
                    continue;
                }
                chunks.Add(line[..at]);
                line = line[(at + mark.Text.Length)..];
            }
            chunks.Add(line);
            marks.AddRange(lineMarks);
        }
        return (chunks.Where(chunk => chunk.Length > 0).ToList(), marks);
    }

    /// <summary>
    /// EspeakBackend._postprocess_line: one space-separated word per word, each followed by
    /// a space, without espeak's '_' between phonemes.
    /// </summary>
    private static string CleanUp(string line)
    {
        line = line.Trim().Replace("\n", " ").Replace("  ", " ");
        line = Underscores.Replace(line, "_").Replace("_ ", " ");
        if (line.Length == 0) return String.Empty;

        var output = new StringBuilder();
        foreach (var word in line.Split(' ')) output.Append(word.Trim().Replace("_", "")).Append(' ');
        return output.ToString();
    }

    /// <summary>Puts the punctuation back: Punctuation.restore, with a space as the word separator.</summary>
    private static List<string> Restore(List<string> chunks, List<Mark> marks)
    {
        var text = new List<string>(chunks);
        var remaining = new Queue<Mark>(marks);
        var punctuated = new List<string>();
        var position = 0;

        while (text.Count > 0 || remaining.Count > 0)
        {
            if (remaining.Count == 0)
            {
                punctuated.AddRange(text.Select(line => line.EndsWith(' ') ? line : line + " "));
                text.Clear();
            }
            else if (text.Count == 0)
            {
                punctuated.Add(String.Concat(remaining.Select(m => m.Text)));
                remaining.Clear();
            }
            else if (remaining.Peek().Line == position)
            {
                var mark = remaining.Dequeue();
                if (text[0].EndsWith(' ')) text[0] = text[0][..^1];

                switch (mark.Position)
                {
                    case 'B':
                        text[0] = mark.Text + text[0];
                        break;
                    case 'E':
                        punctuated.Add(text[0] + mark.Text + (mark.Text.EndsWith(' ') ? "" : " "));
                        text.RemoveAt(0);
                        position++;
                        break;
                    case 'A':
                        punctuated.Add(mark.Text + (mark.Text.EndsWith(' ') ? "" : " "));
                        position++;
                        break;
                    default:
                        if (text.Count == 1)
                        {
                            text[0] += mark.Text;
                        }
                        else
                        {
                            var first = text[0];
                            text.RemoveAt(0);
                            text[0] = first + mark.Text + text[0];
                        }
                        break;
                }
            }
            else
            {
                punctuated.Add(text[0]);
                text.RemoveAt(0);
                position++;
            }
        }
        return punctuated;
    }
}
