using Herald.Services;

namespace Herald.Tests;

public class TextChunkerTests
{
    private static string Sentences(int count) =>
        string.Join(" ", Enumerable.Range(1, count).Select(i => $"This is sentence number {i}."));

    [Fact]
    public void Short_text_is_one_part()
    {
        Assert.Equal(["Hello world."], TextChunker.Split("Hello world", 300, 250));
    }

    [Fact]
    public void Existing_end_punctuation_is_kept()
    {
        Assert.Equal(["Is this a question?"], TextChunker.Split("Is this a question?", 300, 250));
    }

    [Fact]
    public void Line_breaks_become_sentence_ends_so_the_voice_pauses()
    {
        Assert.Equal(["Title. First item. Second item."], TextChunker.Split("Title\nFirst item\nSecond item", 300, 250));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n \n  ")]
    public void Empty_text_gives_no_parts(string text) =>
        Assert.Empty(TextChunker.Split(text, 300, 250));

    [Fact]
    public void Long_text_is_split_only_at_sentence_ends()
    {
        var text = Sentences(30);

        var parts = TextChunker.Split(text, 300, 250);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.EndsWith(".", p));
        Assert.Equal(text, string.Join(" ", parts));
    }

    [Fact]
    public void First_part_is_short_so_speech_starts_quickly()
    {
        var parts = TextChunker.Split(Sentences(30), 300, 250);

        Assert.True(parts[0].Length <= 125, $"first part was {parts[0].Length} characters");
        Assert.All(parts.Skip(1).SkipLast(1), p => Assert.InRange(p.Length, 1, 250));
    }

    [Fact]
    public void Text_at_the_threshold_is_not_split()
    {
        var text = Sentences(30)[..299].TrimEnd() + ".";

        Assert.Single(TextChunker.Split(text, text.Length, 100));
    }

    [Fact]
    public void A_tiny_last_part_is_folded_into_the_one_before()
    {
        var text = Sentences(12) + " Done.";

        var parts = TextChunker.Split(text, 100, 150);

        Assert.EndsWith("Done.", parts[^1]);
        Assert.True(parts[^1].Length > "Done.".Length);
    }

    [Fact]
    public void A_stretch_without_sentence_ends_falls_back_to_commas()
    {
        var clauses = Enumerable.Range(1, 12).Select(i => $"and then clause number {i} continues on");
        var text = string.Join(", ", clauses) + ".";

        var parts = TextChunker.Split(text, 100, 100);

        Assert.True(parts.Count > 1);
        Assert.All(parts.SkipLast(1), p => Assert.EndsWith(",", p));
        Assert.Equal(text, string.Join(" ", parts));
    }

    [Fact]
    public void A_stretch_without_any_punctuation_falls_back_to_words()
    {
        var words = Enumerable.Range(1, 200).Select(i => $"word{i}").ToList();

        var parts = TextChunker.Split(string.Join(" ", words), 100, 100);

        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= 150, $"part was {p.Length} characters"));
        // No word is cut in half or lost.
        Assert.Equal(string.Join(" ", words) + ".", string.Join(" ", parts));
    }
}
