using Herald.Models;
using Herald.Services;

namespace Herald.Tests;

public class TextFilterTests
{
    private static string Clean(string text, string filter = "", IReadOnlyList<ReplacementRule>? replacements = null) =>
        TextFilter.Clean(text, filter, replacements ?? []);

    [Theory]
    [InlineData("Hello **world**", "Hello world")]
    [InlineData("Use `dotnet test` now", "Use dotnet test now")]
    [InlineData("See [the docs](https://example.com) here", "See the docs here")]
    [InlineData("## Summary\nAll good", "Summary\nAll good")]
    [InlineData("  # Indented heading", "Indented heading")]
    public void Strips_markdown_that_reads_badly_aloud(string input, string expected) =>
        Assert.Equal(expected, Clean(input));

    [Fact]
    public void Removes_code_blocks_entirely()
    {
        var input = "Before\n```csharp\nvar x = 1;\n```\nAfter";
        Assert.Equal("Before\nAfter", Clean(input));
    }

    [Theory]
    [InlineData("C# is great", "C# is great")]
    [InlineData("Issue #42 is fixed", "Issue #42 is fixed")]
    [InlineData("#hashtag", "#hashtag")]
    public void Keeps_hash_signs_that_are_not_headings(string input, string expected) =>
        Assert.Equal(expected, Clean(input));

    [Fact]
    public void Replaces_filter_characters_with_spaces()
    {
        Assert.Equal("snake case path to file", Clean("snake_case/path/to/file", filter: "_/"));
    }

    [Fact]
    public void Applies_replacements_before_stripping_characters()
    {
        // The default rule turns an em dash into a pause, even though the em dash is also
        // in the default strip list: replacing comes first.
        var result = Clean("Wait—then go", SenderSettings.DefaultFilterCharacters, SenderSettings.DefaultReplacements());

        Assert.Equal("Wait, then go", result);
    }

    [Fact]
    public void Ignores_replacements_with_nothing_to_find()
    {
        Assert.Equal("abc", Clean("abc", replacements: [new ReplacementRule("", "X")]));
    }

    [Fact]
    public void Tidies_whitespace_but_keeps_single_line_breaks()
    {
        Assert.Equal("Hello, world!\nNext line", Clean("  Hello   ,  world  !\n\n\n   Next \t line  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("```\nonly code\n```")]
    public void Returns_empty_when_nothing_speakable_is_left(string input) =>
        Assert.Equal(string.Empty, Clean(input));

    [Fact]
    public void Caps_absurdly_long_input()
    {
        var result = Clean(new string('a', 50_000));

        Assert.Equal(20_000, result.Length);
    }
}
