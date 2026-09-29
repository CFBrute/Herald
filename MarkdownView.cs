using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Md = Markdig.Syntax;
using MdInlines = Markdig.Syntax.Inlines;

namespace Herald;

/// <summary>
/// Shows Markdown formatted - headings, bold and italics, lists, quotes, inline code and
/// code blocks - built from plain text blocks, so it stays light enough for every item in
/// the lists. Anything it doesn't know is shown as written.
/// </summary>
public class MarkdownView : ContentControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Render()));

    /// <summary>The Markdown to show.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();
    private static readonly FontFamily CodeFont = new("Consolas, Courier New");

    public MarkdownView()
    {
        // Clicks and keys go to the list, as they do for plain item text.
        Focusable = false;
        IsTabStop = false;
    }

    private void Render() =>
        Content = string.IsNullOrEmpty(Text) ? null : Blocks(Markdown.Parse(Text, Pipeline), spacing: 4);

    private static StackPanel Blocks(Md.ContainerBlock container, double spacing)
    {
        var panel = new StackPanel();
        foreach (var block in container)
        {
            if (Block(block) is not { } element) continue;
            if (panel.Children.Count > 0) element.Margin = new Thickness(0, spacing, 0, 0);
            panel.Children.Add(element);
        }
        return panel;
    }

    private static FrameworkElement? Block(Md.Block block) => block switch
    {
        Md.HeadingBlock heading => Heading(heading),
        Md.ParagraphBlock paragraph => Paragraph(paragraph.Inline),
        Md.CodeBlock code => Code(code),
        Md.ListBlock list => List(list),
        Md.QuoteBlock quote => Quote(quote),
        Md.ThematicBreakBlock => new Separator(),
        Md.LeafBlock leaf => Plain(leaf.Lines.ToString()),
        Md.ContainerBlock container => Blocks(container, spacing: 4),
        _ => null
    };

    private static TextBlock Paragraph(MdInlines.ContainerInline? inlines)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AddInlines(text.Inlines, inlines);
        return text;
    }

    private static TextBlock Plain(string text) => new() { Text = text.TrimEnd(), TextWrapping = TextWrapping.Wrap };

    private static TextBlock Heading(Md.HeadingBlock heading)
    {
        var text = Paragraph(heading.Inline);
        text.FontWeight = FontWeights.Bold;
        text.FontSize = SystemFonts.MessageFontSize * heading.Level switch { 1 => 1.3, 2 => 1.2, 3 => 1.1, _ => 1.0 };
        return text;
    }

    /// <summary>The code in a shaded box, with a Copy button in its top right corner.</summary>
    private static Grid Code(Md.CodeBlock code)
    {
        var text = code.Lines.ToString().TrimEnd();
        var border = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 4, 6, 4),
            Child = new TextBlock { Text = text, FontFamily = CodeFont, TextWrapping = TextWrapping.Wrap }
        };
        border.SetResourceReference(Border.BackgroundProperty, "CodeBackground");

        var copy = new Button
        {
            Content = "Copy",
            FontSize = 10,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 3, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            // Keyboard focus stays in the list.
            Focusable = false,
            ToolTip = "Copy this code"
        };
        copy.Click += (_, _) => CopyCode(copy, text);

        return new Grid { Children = { border, copy } };
    }

    private static void CopyCode(Button button, string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Another program has the clipboard open; a second click will do.
            return;
        }

        button.Content = "Copied";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = "Copy";
        };
        timer.Start();
    }

    private static StackPanel List(Md.ListBlock list)
    {
        var panel = new StackPanel();
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<Md.ListItemBlock>())
        {
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}{list.OrderedDelimiter}" : "•",
                Margin = new Thickness(4, 0, 6, 0)
            };
            var content = Blocks(item, spacing: 2);
            Grid.SetColumn(content, 1);

            var row = new Grid { Margin = new Thickness(0, panel.Children.Count > 0 ? 2 : 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static Border Quote(Md.QuoteBlock quote)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Child = Blocks(quote, spacing: 4)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "SubtleText");
        border.SetResourceReference(TextElement.ForegroundProperty, "SubtleText");
        return border;
    }

    /// <summary>
    /// A link that opens in the browser. Any program on this PC can send Herald text, so
    /// only web and mail links are clickable; anything else (a file, a script) is just
    /// underlined.
    /// </summary>
    private static Span Link(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return new Underline();
        }

        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.ToString() };
        link.SetResourceReference(TextElement.ForegroundProperty, "LinkText");
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No browser or mail program set up; nothing more to do.
            }
            e.Handled = true;
        };
        return link;
    }

    private static void AddInlines(InlineCollection target, MdInlines.ContainerInline? container)
    {
        if (container == null) return;
        foreach (var inline in container) target.Add(Inline(inline));
    }

    private static Inline Inline(MdInlines.Inline inline)
    {
        switch (inline)
        {
            case MdInlines.LiteralInline literal:
                return new Run(literal.Content.ToString());
            case MdInlines.CodeInline code:
                var run = new Run(code.Content) { FontFamily = CodeFont };
                run.SetResourceReference(TextElement.BackgroundProperty, "CodeBackground");
                return run;
            case MdInlines.EmphasisInline emphasis:
                Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                AddInlines(span.Inlines, emphasis);
                return span;
            case MdInlines.LinkInline { IsImage: false } link:
                Span words = Link(link.Url);
                if (link.FirstChild == null) words.Inlines.Add(new Run(link.Url));
                else AddInlines(words.Inlines, link);
                return words;
            case MdInlines.AutolinkInline autolink:
                var url = Link(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url);
                url.Inlines.Add(new Run(autolink.Url));
                return url;
            // Claude's single line breaks are meant as line breaks, so they aren't joined up.
            case MdInlines.LineBreakInline:
                return new LineBreak();
            case MdInlines.HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case MdInlines.HtmlInline html:
                return new Run(html.Tag);
            case MdInlines.ContainerInline container:
                var group = new Span();
                AddInlines(group.Inlines, container);
                return group;
            default:
                return new Run(inline.ToString());
        }
    }
}
