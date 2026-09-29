using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Herald.Tests;

public class MarkdownViewTests
{
    private static T OnUiThread<T>(Func<T> read) => UiThread.Invoke(read);

    private static StackPanel Render(string markdown) =>
        (StackPanel)new MarkdownView { Text = markdown }.Content;

    [Fact]
    public void Each_kind_of_block_gets_its_own_element()
    {
        var kinds = OnUiThread(() => Render("""
            ## Result

            The **build** passed.

            - first
            - second

            ```csharp
            var x = 1;
            ```

            > A quote
            """).Children.Cast<UIElement>().Select(c => c.GetType().Name).ToArray());

        Assert.Equal(["TextBlock", "TextBlock", "StackPanel", "Grid", "Border"], kinds);
    }

    [Fact]
    public void Headings_are_bold_and_bold_text_is_bold()
    {
        var (headingWeight, spans) = OnUiThread(() =>
        {
            var blocks = Render("# Title\n\nSome **bold** and *italic* and `code`.").Children;
            var heading = (TextBlock)blocks[0];
            var paragraph = (TextBlock)blocks[1];
            return (heading.FontWeight, paragraph.Inlines.Select(i => i.GetType().Name).ToArray());
        });

        Assert.Equal(FontWeights.Bold, headingWeight);
        Assert.Equal(["Run", "Bold", "Run", "Italic", "Run", "Run", "Run"], spans);
    }

    [Fact]
    public void A_code_block_keeps_its_lines_and_leaves_out_the_fences_and_has_a_copy_button()
    {
        var (code, button) = OnUiThread(() =>
        {
            var block = (Grid)Render("```\nline one\nline two\n```").Children[0];
            return (((TextBlock)((Border)block.Children[0]).Child).Text, (string)((Button)block.Children[1]).Content);
        });

        Assert.Equal("line one\nline two", code);
        Assert.Equal("Copy", button);
    }

    [Theory]
    [InlineData("See [the docs](https://example.com/docs).", "https://example.com/docs")]
    [InlineData("Mail <someone@example.com>.", "mailto:someone@example.com")]
    [InlineData("Open <https://example.com>.", "https://example.com/")]
    public void Web_and_mail_links_are_clickable(string markdown, string target)
    {
        var uri = OnUiThread(() =>
            ((TextBlock)Render(markdown).Children[0]).Inlines.OfType<Hyperlink>().Single().NavigateUri.ToString());

        Assert.Equal(target, uri);
    }

    [Theory]
    [InlineData("[run me](file:///C:/Windows/System32/calc.exe)")]
    [InlineData("[click](javascript:alert(1))")]
    [InlineData("[relative](docs/readme.md)")]
    public void Other_links_are_only_underlined(string markdown)
    {
        var kinds = OnUiThread(() =>
            ((TextBlock)Render(markdown).Children[0]).Inlines.Select(i => i.GetType().Name).ToArray());

        Assert.Equal(["Underline"], kinds);
    }

    [Fact]
    public void List_items_are_numbered_or_bulleted_like_the_source()
    {
        var markers = OnUiThread(() =>
        {
            var numbered = (StackPanel)Render("3. three\n4. four").Children[0];
            var bulleted = (StackPanel)Render("- one").Children[0];
            return numbered.Children.Cast<Grid>().Concat(bulleted.Children.Cast<Grid>())
                .Select(row => ((TextBlock)row.Children[0]).Text).ToArray();
        });

        Assert.Equal(["3.", "4.", "•"], markers);
    }

    [Fact]
    public void Single_line_breaks_are_kept()
    {
        var hasBreak = OnUiThread(() =>
            ((TextBlock)Render("first line\nsecond line").Children[0]).Inlines.OfType<LineBreak>().Any());

        Assert.True(hasBreak);
    }

    [Fact]
    public void Plain_text_looks_like_plain_text()
    {
        var text = OnUiThread(() =>
        {
            var paragraph = (TextBlock)Render("Just a normal sentence.").Children[0];
            return new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        });

        Assert.Equal("Just a normal sentence.", text);
    }
}
