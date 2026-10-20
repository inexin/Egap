using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>Scale to fit, clamped lines, repeating table footers and balanced columns.</summary>
public sealed class MorePagingTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

    [Fact]
    public void MaxLinesClampsAParagraphWithAnEllipsis()
    {
        Document document = Page(out Section section);
        Paragraph paragraph = section.Body.AddParagraph(string.Join(' ', Enumerable.Repeat("words that wrap", 40)));
        int lines = document.Layout(Isolated).Pages[0].BodyLines.Count();

        paragraph.Format.MaxLines = 2;
        LineBox[] clamped = [.. document.Layout(Isolated).Pages[0].BodyLines];

        Assert.True(lines > 2);
        Assert.Equal(2, clamped.Length);
        Assert.True(clamped[^1].Line.Ellipsized);

        Document html = HtmlFormat.Parse("<p style='-webkit-line-clamp: 3'>x</p>");
        Assert.Equal(3, html.Descendants().OfType<Paragraph>().Single().Format.MaxLines);
        Assert.Contains("line-clamp", HtmlFormat.ToHtml(html, out _), StringComparison.Ordinal);
    }

    private static Document Page(out Section section)
    {
        Document document = new();
        section = document.AddSection();
        section.PageSetup.PageWidth = 400;
        section.PageSetup.PageHeight = 400;
        section.PageSetup.MarginTop = section.PageSetup.MarginBottom = section.PageSetup.MarginLeft = section.PageSetup.MarginRight = 36;
        return document;
    }
}
