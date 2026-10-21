using Egap.Documents.Layout;

namespace Egap.Documents.Markdown.Tests;

/// <summary>Markdown with the core's other formats and layout, and hostile Markdown.</summary>
public sealed class MarkdownInteropTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    [Fact]
    public void MarkdownBecomesDocx()
    {
        Document document = MarkdownFormat.Parse("# Heading\n\nSome *emphasis* and a [link](https://example.com/).\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n");

        Document read = RoundTrip(document);

        Assert.Equal(document.GetText(), read.GetText());
        Assert.Equal(2, read.Descendants<Paragraph>().Count(p => p.ListLabel is not null));
    }

    [Fact]
    public void ParagraphsListsTablesAndNotesInReadingOrder()
    {
        Document document = MarkdownFormat.Parse(
            "# Title\n\nSome *emphasis* and a note[^1].\n\n1. first\n2. second\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n[^1]: The note.\n");

        string text = TextFormat.ToText(document);

        Assert.Equal("Title\nSome emphasis and a note[1].\n1.\tfirst\n2.\tsecond\nA\tB\n1\t2\n[1] The note.\n", text);
    }

    [Fact]
    public void DeepMarkdownNesting()
    {
        string markdown = new string('>', 50_000) + " quote\n\n" + string.Concat(Enumerable.Repeat("- ", 50_000)) + "item\n\n" + new string('*', 100_000) + "x";

        Document document = MarkdownFormat.Parse(markdown);
        _ = document.Layout(Isolated).Pages.Count;

        Assert.Contains("quote", document.GetText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[li <http://x.y> www.z.com][r]\n\n[r]: /u")]
    [InlineData("[a www.b.com c](/u)")]
    public void LinksInsideLinksKeepTheirText(string markdown)
    {
        // Fuzz finding (task 28): an extended autolink inside a link's text made a hyperlink inside a hyperlink.
        Document document = MarkdownFormat.Parse(markdown);

        Assert.DoesNotContain(document.Descendants().OfType<Hyperlink>(), h => h.Descendants().OfType<Hyperlink>().Any());
        Assert.Contains("www", document.GetText(), StringComparison.Ordinal);
    }

    private static Document RoundTrip(Document document)
    {
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        stream.Position = 0;
        return DocxFormat.Load(stream);
    }
}
