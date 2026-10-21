using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Html.Tests.Pdf;

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
