using Herald.Models;
using Herald.Services;

namespace Herald.Tests;

public class LanguageDetectorTests
{
    private static CompiledLanguageProfile Profile(string name, string markers, int minMatches = 2) =>
        CompiledLanguageProfile.From(new LanguageProfile { Name = name, Markers = markers, MinMatches = minMatches });

    private static readonly CompiledLanguageProfile Swedish = Profile("Swedish", "och att det är som på å ä ö", 3);
    private static readonly CompiledLanguageProfile German = Profile("German", "und der die das ist nicht ü ß", 3);

    [Fact]
    public void Markers_are_split_into_words_and_single_letters()
    {
        var profile = Profile("X", "Och, ATT;det\nå  Ä");

        Assert.Equal(["att", "det", "och"], profile.Words.Order());
        Assert.Equal(["å", "ä"], profile.Letters);
    }

    [Fact]
    public void Detects_the_language_with_enough_marker_words()
    {
        var detected = LanguageDetector.Detect("Det är bra och jag tycker att det fungerar", [Swedish, German]);

        Assert.Equal("Swedish", detected?.Name);
    }

    [Fact]
    public void Returns_null_below_the_minimum_so_the_default_voice_is_used()
    {
        Assert.Null(LanguageDetector.Detect("This is plain English och that is it", [Swedish, German]));
    }

    [Fact]
    public void A_marker_letter_matches_any_word_containing_it()
    {
        var score = LanguageDetector.Score("gå får röd", [Swedish]).Single();

        Assert.Equal(3, score.Matches);
    }

    [Fact]
    public void A_word_counts_once_even_when_it_matches_as_word_and_letter()
    {
        // "på" is a marker word and also contains the marker letter "å".
        var score = LanguageDetector.Score("på", [Swedish]).Single();

        Assert.Equal(1, score.Matches);
    }

    [Fact]
    public void Matching_ignores_case_and_punctuation()
    {
        var score = LanguageDetector.Score("OCH! Att, DET?", [Swedish]).Single();

        Assert.Equal(3, score.Matches);
    }

    [Fact]
    public void The_profile_with_most_matches_wins()
    {
        var text = "und der die das ist nicht och att";

        Assert.Equal("German", LanguageDetector.Detect(text, [Swedish, German])?.Name);
        Assert.Equal(["German", "Swedish"], LanguageDetector.Score(text, [Swedish, German]).Select(m => m.Profile.Name));
    }
}
