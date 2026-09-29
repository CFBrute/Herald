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

    private static string Clean(string text) => TextFilter.Clean(text, "*", []);

    [Fact]
    public void Each_part_shows_its_own_unchanged_slice_of_the_message()
    {
        var text = "## Result\n" + string.Join(" ", Enumerable.Range(1, 30).Select(i => $"Sentence **{i}** is here."));

        var parts = TextChunker.SplitMessage(text, Clean, 300, 250);

        Assert.True(parts.Count > 1);
        // The shown parts are the original, cut only between parts.
        Assert.Equal(text, string.Join(" ", parts.Select(p => p.Shown)));
        Assert.StartsWith("## Result\nSentence **1** is here.", parts[0].Shown);
        Assert.StartsWith("Result. Sentence 1 is here.", parts[0].Spoken);
        Assert.All(parts, p => Assert.DoesNotContain("*", p.Spoken));
    }

    [Fact]
    public void A_code_block_is_shown_with_the_text_before_it_but_not_spoken()
    {
        var text = "Here is the fix:\n```csharp\nvar x = 1;\nvar y = 2;\n```\nThat is all.";

        var parts = TextChunker.SplitMessage(text, Clean, 300, 250);

        var part = Assert.Single(parts);
        Assert.Equal(text, part.Shown);
        Assert.Equal("Here is the fix: That is all.", part.Spoken);
    }

    [Fact]
    public void A_message_with_nothing_to_say_gives_no_parts()
    {
        Assert.Empty(TextChunker.SplitMessage("```\ncode only\n```", Clean, 300, 250));
    }

    [Fact]
    public void A_part_cut_inside_a_line_gets_no_added_pause()
    {
        var words = string.Join(" ", Enumerable.Range(1, 200).Select(i => $"word{i}"));

        var parts = TextChunker.SplitMessage(words, Clean, 100, 100);

        Assert.All(parts.SkipLast(1), p => Assert.False(p.Spoken.EndsWith('.'), p.Spoken));
        Assert.EndsWith(".", parts[^1].Spoken);
        Assert.Equal(words, string.Join(" ", parts.Select(p => p.Shown)));
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
