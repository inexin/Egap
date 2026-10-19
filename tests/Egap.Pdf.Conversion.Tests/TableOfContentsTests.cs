using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Pdf.Conversion.Tests;

/// <summary>Tables of contents on conversion, and PAGEREF filled by layout.</summary>
public sealed class TableOfContentsTests
{
    private static readonly PdfConvertOptions Isolated = new()
    {
        Export = new PdfExportOptions { Layout = new LayoutOptions { Fonts = FontSettings.Portable } },
    };

    [Fact]
    public void AnHtmlNavDataTocGetsLinkedEntriesWithPageNumbers()
    {
        string html = "<nav data-toc></nav><h1>Introduction</h1><p>Text</p><h1 style='break-before: page'>Results</h1><h2>Details</h2><p>More</p>";

        using PdfDocument pdf = Reopen(PdfConvert.FromHtml(html, Isolated with { TableOfContents = TableOfContentsMode.WithPageNumbers }));

        Assert.Equal(2, pdf.Pages.Count);
        string[] lines = [.. pdf.Pages[0].GetText().Lines.Select(l => l.Text)];
        Assert.Contains(lines, l => l.StartsWith("Introduction", StringComparison.Ordinal) && l.TrimEnd().EndsWith('1'));
        Assert.Contains(lines, l => l.StartsWith("Results", StringComparison.Ordinal) && l.TrimEnd().EndsWith('2'));
        Assert.Contains(lines, l => l.StartsWith("Details", StringComparison.Ordinal) && l.TrimEnd().EndsWith('2'));
        Assert.True(pdf.Pages[0].Annotations.OfType<PdfLinkAnnotation>().Count() >= 3);

        // Without the option the placeholder is still a table of contents, left unbuilt.
        using PdfDocument plain = Reopen(PdfConvert.FromHtml(html, Isolated));
        Assert.DoesNotContain("Results", plain.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownTocMarkerOrTheStartTakesTheTable()
    {
        using PdfDocument marked = Reopen(PdfConvert.FromMarkdown("Intro text\n\n[TOC]\n\n# Alpha\n\nA\n\n# Beta\n\nB\n", Isolated with { TableOfContents = TableOfContentsMode.WithoutPageNumbers }));
        string[] lines = [.. marked.Pages[0].GetText().Lines.Select(l => l.Text.Trim())];
        Assert.Equal("Intro text", lines[0]);
        Assert.Equal("Alpha", lines[1]);
        Assert.Equal("Beta", lines[2]);
        Assert.DoesNotContain("[TOC]", marked.Pages[0].ExtractText(), StringComparison.Ordinal);

        using PdfDocument start = Reopen(PdfConvert.FromMarkdown("Intro text\n\n# Alpha\n\nA\n", Isolated with { TableOfContents = TableOfContentsMode.WithPageNumbers }));
        string[] first = [.. start.Pages[0].GetText().Lines.Select(l => l.Text.Trim())];
        Assert.StartsWith("Alpha", first[0], StringComparison.Ordinal);
        Assert.EndsWith("1", first[0], StringComparison.Ordinal);
        Assert.Equal("Intro text", first[1]);
    }

    [Fact]
    public void PageRefShowsThePageItsBookmarkIsOn()
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        builder.Write("See page ");
        builder.InsertField("PAGEREF target");
        builder.Writeln(".");
        builder.InsertBreak(BreakKind.Page);
        builder.StartBookmark("target");
        builder.Write("Here");
        builder.EndBookmark("target");

        DocumentLayout layout = document.Layout(new LayoutOptions { Fonts = FontSettings.Portable });

        Assert.Equal("See page 2.", string.Concat(layout.Pages[0].BodyLines.Select(l => l.Text)).Trim());
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using (pdf)
        {
            using MemoryStream saved = new();
            pdf.Save(saved);
            return PdfDocument.Open(saved.ToArray());
        }
    }
}
