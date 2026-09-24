using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Herald.Services;

/// <summary>
/// Splits long text into speakable parts. Cuts only at strong breaks - line breaks,
/// sentence ends, ';' and ':' - because every cut becomes an audible gap between parts.
/// Commas are only used for a stretch far too long to speak as one part, and single
/// words only after that. The first part is kept short so audio starts quickly.
/// </summary>
public static class TextChunker
{
    private static readonly Regex StrongBreak = new(@"(?<=[.!?…;:])\s+", RegexOptions.Compiled);
    private static readonly Regex Comma = new(@"(?<=,)\s+", RegexOptions.Compiled);
    private const string EndPunctuation = ".!?…;:,";

    public static List<string> Split(string text, int threshold, int target)
    {
        // Longest stretch spoken as one part before falling back to commas.
        var hardMax = target * 3;

        var pieces = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // A line break is a real pause; without an end mark the voice would run the
            // line (a heading, a list item) straight into the next one.
            if (EndPunctuation.IndexOf(line[^1]) < 0) line += ".";

            foreach (var piece in StrongBreak.Split(line))
            {
                if (piece.Length == 0) continue;
                if (piece.Length <= hardMax) pieces.Add(piece);
                else pieces.AddRange(SplitOverlong(piece, target, hardMax));
            }
        }

        var whole = string.Join(" ", pieces);
        if (whole.Length <= threshold) return whole.Length > 0 ? [whole] : [];

        var chunks = new List<string>();
        var current = new StringBuilder();
        var limit = Math.Max(target / 2, 60);

        foreach (var piece in pieces)
        {
            if (current.Length > 0 && current.Length + 1 + piece.Length > limit)
            {
                chunks.Add(current.ToString());
                current.Clear();
                limit = target;
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(piece);
        }
        if (current.Length > 0) chunks.Add(current.ToString());

        // A tiny trailing part sounds like an afterthought - fold it into the previous one.
        if (chunks.Count > 1 && chunks[^1].Length < 40 && chunks[^2].Length + chunks[^1].Length < target * 3 / 2)
        {
            chunks[^2] = chunks[^2] + " " + chunks[^1];
            chunks.RemoveAt(chunks.Count - 1);
        }

        return chunks;
    }

    /// <summary>Last resort for a stretch with no strong break: commas, then words.</summary>
    private static IEnumerable<string> SplitOverlong(string piece, int target, int hardMax)
    {
        foreach (var clause in Comma.Split(piece))
        {
            if (clause.Length <= hardMax)
            {
                yield return clause;
                continue;
            }

            var sb = new StringBuilder();
            foreach (var word in clause.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (sb.Length > 0 && sb.Length + 1 + word.Length > target)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(word);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }
}
