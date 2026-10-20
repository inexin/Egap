using Egami;
using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Documents.Html.Tests;

/// <summary>Word spacing, overline, OpenType features and word breaking.</summary>
public sealed class TextOptionsTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    [Fact]
    public void HtmlReadsAndWritesTheOptions()
    {
        Document html = HtmlFormat.Parse("""
            <p style="word-spacing: 4pt; text-decoration: overline; font-feature-settings: 'liga' 0, 'ss02'; font-variant-numeric: tabular-nums; word-break: break-all">x y</p>
            <p style="overflow-wrap: anywhere">z</p>
            """);
        Paragraph[] paragraphs = [.. html.Descendants().OfType<Paragraph>()];
        Run run = paragraphs[0].Runs.First();

        Assert.Equal(4, run.Format.WordSpacing);
        Assert.True(run.Format.Overline);
        Assert.Equal(FontFeatures.Parse("tnum, liga=0, ss02"), run.Format.Features);
        Assert.Equal(WordBreak.BreakAll, paragraphs[0].Format.WordBreak);
        Assert.Equal(WordBreak.Anywhere, paragraphs[1].Format.WordBreak);

        string written = HtmlFormat.ToHtml(html, out _);
        Assert.Contains("word-spacing:4pt", written.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("overline", written, StringComparison.Ordinal);
        Assert.Contains("'ss02' 1", written, StringComparison.Ordinal);
        Assert.Contains("break-all", written, StringComparison.Ordinal);
        Document again = HtmlFormat.Parse(written);
        Assert.Equal(run.Format.Features, again.Descendants().OfType<Run>().First().Format.Features);
    }

    private static Document OneRun(string text, Action<RunFormat> format)
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.PageWidth = 400;
        section.PageSetup.PageHeight = 400;
        section.PageSetup.MarginLeft = section.PageSetup.MarginRight = section.PageSetup.MarginTop = section.PageSetup.MarginBottom = 36;
        Run run = section.Body.AddParagraph().AddRun(text);
        format(run.Format);
        return document;
    }

    private static double Width(Document document)
    {
        DocumentLayout layout = document.Layout(Isolated);
        return layout.GetTextRects(document.Descendants().OfType<Run>().First()).Sum(r => r.Bounds.Width);
    }

    private static int Lines(Document document) => document.Layout(Isolated).Pages[0].BodyLines.Count();
}
