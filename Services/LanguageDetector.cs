using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Herald.Models;

namespace Herald.Services;

/// <summary>Immutable copy of a <see cref="LanguageProfile"/>, safe to read from any thread.</summary>
public sealed record CompiledLanguageProfile(
    string Name,
    IReadOnlySet<string> Words,
    IReadOnlyList<string> Letters,
    int MinMatches)
{
    public static CompiledLanguageProfile From(LanguageProfile profile)
    {
        var markers = profile.Markers
            .Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim().ToLowerInvariant())
            .Where(m => m.Length > 0)
            .Distinct()
            .ToList();

        return new CompiledLanguageProfile(
            profile.Name,
            markers.Where(m => m.Length > 1).ToHashSet(),
            markers.Where(m => m.Length == 1).ToList(),
            profile.MinMatches);
    }
}

public record LanguageMatch(CompiledLanguageProfile Profile, int Matches);

public static class LanguageDetector
{
    private static readonly Regex Word = new(@"\p{L}+", RegexOptions.Compiled);

    /// <summary>
    /// Counts marker hits per profile: a word in the profile's word list counts once, and a
    /// word containing one of its marker letters counts once (a word never counts twice).
    /// Returns every profile's result, best first.
    /// </summary>
    public static List<LanguageMatch> Score(string text, IEnumerable<CompiledLanguageProfile> profiles)
    {
        var words = Word.Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();

        return profiles
            .Select(p => new LanguageMatch(p, words.Count(w => p.Words.Contains(w) || p.Letters.Any(l => w.Contains(l)))))
            .OrderByDescending(m => m.Matches)
            .ToList();
    }

    /// <summary>The best profile that reached its minimum, or null (use the sender's normal voice).</summary>
    public static CompiledLanguageProfile? Detect(string text, IEnumerable<CompiledLanguageProfile> profiles) =>
        Score(text, profiles).FirstOrDefault(m => m.Matches >= m.Profile.MinMatches)?.Profile;
}
