using System.Collections.Generic;
using System.Text.RegularExpressions;
using Herald.Models;

namespace Herald.Services;

/// <summary>
/// Cleans text before it's sent to TTS: strips markdown formatting that reads
/// awkwardly aloud, then strips a caller-supplied (per-sender) set of characters.
/// </summary>
public static class TextFilter
{
    private static readonly Regex CodeBlock = new(@"```[\s\S]*?```", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*([^\*]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex MarkdownLink = new(@"\[([^\]]+)\]\([^\)]+\)", RegexOptions.Compiled);
    // Line breaks are kept (collapsed to one) because the splitter uses them as cut points.
    private static readonly Regex LineBreaks = new(@"[ \t]*(\r?\n[ \t]*)+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[^\S\n]+", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunctuation = new(@"[ \t]+([,.;:!?])", RegexOptions.Compiled);

    // Long text is split into parts downstream; this only guards against absurd input.
    private const int MaxLength = 20000;

    public static string Clean(string text, string filterCharacters, IReadOnlyList<ReplacementRule> replacements)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var result = CodeBlock.Replace(text, "");
        result = InlineCode.Replace(result, "$1");
        result = Bold.Replace(result, "$1");
        result = Heading.Replace(result, "");
        result = MarkdownLink.Replace(result, "$1");

        foreach (var rule in replacements)
        {
            if (rule.Find.Length == 0) continue;
            result = result.Replace(rule.Find, rule.Replace);
        }

        foreach (var ch in filterCharacters)
        {
            result = result.Replace(ch, ' ');
        }

        result = LineBreaks.Replace(result, "\n");
        result = Spaces.Replace(result, " ");
        result = SpaceBeforePunctuation.Replace(result, "$1").Trim();

        if (result.Length > MaxLength)
        {
            result = result.Substring(0, MaxLength);
        }

        return result;
    }
}
