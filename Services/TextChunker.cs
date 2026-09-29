using System;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private const string EndPunctuation = ".!?…;:,";

    /// <summary>A part as it was sent (shown in the lists) and as the voice gets it.</summary>
    public record Part(string Shown, string Spoken);

    /// <summary>A stretch of the original text and how long it is once cleaned (0: nothing to say).</summary>
    private record struct Piece(int Start, int End, int Length);

    /// <summary>Splits text that needs no cleaning; returns what the voice gets.</summary>
    public static List<string> Split(string text, int threshold, int target) =>
        [.. SplitMessage(text, t => t, threshold, target).Select(p => p.Spoken)];

    /// <summary>
    /// Splits the original text, so each part can show its own unchanged slice of it, and
    /// cleans each part for the voice. Sizes are measured on the cleaned text, since that's
    /// what's spoken. Stretches with nothing to say (a code block) stay with the part before.
    /// </summary>
    public static List<Part> SplitMessage(string text, Func<string, string> clean, int threshold, int target)
    {
        var pieces = Pieces(text, clean, target * 3);
        var spoken = pieces.Where(p => p.Length > 0).ToList();
        if (spoken.Count == 0) return [];

        // Indexes into pieces where a part starts.
        var starts = new List<int> { 0 };
        if (spoken.Sum(p => p.Length) + spoken.Count - 1 > threshold)
        {
            var lengths = new List<int>();
            var current = 0;
            var limit = Math.Max(target / 2, 60);
            for (var i = 0; i < pieces.Count; i++)
            {
                var length = pieces[i].Length;
                if (length == 0) continue;
                if (current > 0 && current + 1 + length > limit)
                {
                    lengths.Add(current);
                    starts.Add(i);
                    current = 0;
                    limit = target;
                }
                current += (current > 0 ? 1 : 0) + length;
            }
            lengths.Add(current);

            // A tiny trailing part sounds like an afterthought - fold it into the previous one.
            if (starts.Count > 1 && lengths[^1] < 40 && lengths[^2] + lengths[^1] < target * 3 / 2)
            {
                starts.RemoveAt(starts.Count - 1);
            }
        }

        var parts = new List<Part>(starts.Count);
        for (var k = 0; k < starts.Count; k++)
        {
            var from = pieces[starts[k]].Start;
            var hasNext = k + 1 < starts.Count;
            var to = hasNext ? pieces[starts[k + 1]].Start : text.Length;
            // A part cut inside a line doesn't end that line, so it gets no added pause.
            var endsLine = !hasNext || text[pieces[starts[k + 1] - 1].End..to].Contains('\n');

            var shown = text[from..to].Trim();
            var voice = ForVoice(clean(shown), endsLine);
            if (voice.Length > 0) parts.Add(new Part(shown, voice));
        }
        return parts;
    }

    /// <summary>
    /// A line break is a real pause; without an end mark the voice would run the line
    /// (a heading, a list item) straight into the next one.
    /// </summary>
    private static string ForVoice(string cleaned, bool endsLine)
    {
        var lines = cleaned.Split('\n')
            .Select(l => Spaces.Replace(l.Trim(), " "))
            .Where(l => l.Length > 0)
            .ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if ((i < lines.Count - 1 || endsLine) && EndPunctuation.IndexOf(lines[i][^1]) < 0) lines[i] += ".";
        }
        return String.Join(" ", lines);
    }

    private static List<Piece> Pieces(string text, Func<string, string> clean, int hardMax)
    {
        var pieces = new List<Piece>();
        foreach (var (start, end, whole) in Lines(text))
        {
            if (whole)
            {
                Add(pieces, text, start, end, clean);
                continue;
            }
            foreach (var (s, e) in SplitRange(text, start, end, StrongBreak))
            {
                if (Add(pieces, text, s, e, clean) <= hardMax) continue;
                pieces.RemoveAt(pieces.Count - 1);
                foreach (var (cs, ce) in SplitOverlong(text, s, e, clean, hardMax / 3, hardMax))
                {
                    Add(pieces, text, cs, ce, clean);
                }
            }
        }
        return pieces;
    }

    /// <summary>Adds the trimmed stretch (if any) and returns its cleaned length.</summary>
    private static int Add(List<Piece> pieces, string text, int start, int end, Func<string, string> clean)
    {
        while (start < end && Char.IsWhiteSpace(text[start])) start++;
        while (end > start && Char.IsWhiteSpace(text[end - 1])) end--;
        if (start == end) return 0;

        var length = clean(text[start..end]).Trim().Length;
        pieces.Add(new Piece(start, end, length));
        return length;
    }

    /// <summary>
    /// The lines of the text as (start, end) ranges. A line touching a code block runs to
    /// the end of the block's last line and is kept whole, so the block is cleaned (removed)
    /// as one piece instead of being split into lines the filter no longer recognises.
    /// </summary>
    private static IEnumerable<(int Start, int End, bool Whole)> Lines(string text)
    {
        var blocks = TextFilter.CodeBlock.Matches(text);
        var pos = 0;
        while (pos < text.Length)
        {
            var end = LineEnd(text, pos);
            var whole = false;
            foreach (Match block in blocks)
            {
                var blockEnd = block.Index + block.Length;
                if (block.Index >= end || blockEnd <= pos) continue;
                whole = true;
                if (blockEnd > end) end = LineEnd(text, blockEnd);
            }
            yield return (pos, end, whole);
            pos = end + 1;
        }
    }

    private static int LineEnd(string text, int from)
    {
        var i = text.IndexOf('\n', from);
        return i < 0 ? text.Length : i;
    }

    /// <summary>Cuts a range at the separator's matches.</summary>
    private static IEnumerable<(int Start, int End)> SplitRange(string text, int start, int end, Regex separator)
    {
        var pos = start;
        foreach (Match m in separator.Matches(text[start..end]))
        {
            yield return (pos, start + m.Index);
            pos = start + m.Index + m.Length;
        }
        yield return (pos, end);
    }

    /// <summary>Last resort for a stretch with no strong break: commas, then words.</summary>
    private static IEnumerable<(int Start, int End)> SplitOverlong(string text, int start, int end,
                                                                   Func<string, string> clean, int target, int hardMax)
    {
        foreach (var (cs, ce) in SplitRange(text, start, end, Comma))
        {
            if (clean(text[cs..ce]).Trim().Length <= hardMax)
            {
                yield return (cs, ce);
                continue;
            }

            int? runStart = null;
            var runEnd = cs;
            var runLength = 0;
            foreach (var (ws, we) in SplitRange(text, cs, ce, Spaces))
            {
                if (ws == we) continue;
                if (runStart != null && runLength + 1 + (we - ws) > target)
                {
                    yield return (runStart.Value, runEnd);
                    runStart = null;
                    runLength = 0;
                }
                runLength += (runStart != null ? 1 : 0) + (we - ws);
                runStart ??= ws;
                runEnd = we;
            }
            if (runStart != null) yield return (runStart.Value, runEnd);
        }
    }
}
