using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf;

/// <summary>Tables of contents on conversion, and PAGEREF filled by layout.</summary>
public sealed class TableOfContentsTests
{
    private static readonly PdfConvertOptions Isolated = new()
    {
        Export = new PdfExportOptions { Layout = new LayoutOptions { Fonts = FontSettings.Portable } },
    };

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
