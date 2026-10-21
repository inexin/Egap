using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Tables;

/// <summary>A table tagged inside another's cell stays in that cell (3.7 task 7).</summary>
public sealed class NestedTableTests
{
    private static PdfDocument Tagged(Document document)
    {
        using MemoryStream stream = new();
        document.SaveAsPdf(stream, new PdfExportOptions { Tagged = true, Layout = new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false } } });
        return PdfDocument.Open(stream.ToArray());
    }

    private static Document WithNestedTable()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        Table outer = body.AddTable(2, 2);
        outer.Rows[0].Cells[0].Blocks.OfType<Paragraph>().First().Inlines.Add(new Run(document, "Outer one"));
        outer.Rows[1].Cells[0].Blocks.OfType<Paragraph>().First().Inlines.Add(new Run(document, "Outer two"));
        Table inner = outer.Rows[1].Cells[1].AddTable(2, 2);
        string[] texts = ["Inner a", "Inner b", "Inner c", "Inner d"];
        for (int i = 0; i < 4; i++)
        {
            inner.Rows[i / 2].Cells[i % 2].Blocks.OfType<Paragraph>().First().Inlines.Add(new Run(document, texts[i]));
        }

        return document;
    }

    [Fact]
    public void ANestedTableIsInItsCell()
    {
        using PdfDocument pdf = Tagged(WithNestedTable());

        PdfPageLayout page = PdfLayoutAnalyzer.Analyze(pdf, 0);
        LayoutTable table = Assert.Single(page.Tables);

        LayoutTableCell holder = Assert.Single(table.Cells, c => c.Tables.Count > 0);
        LayoutTable inner = Assert.Single(holder.Tables);
        Assert.Equal((2, 2), (inner.RowCount, inner.ColumnCount));
        Assert.Equal(["Inner a", "Inner b", "Inner c", "Inner d"], inner.Cells.Select(c => c.Text));
    }

    [Fact]
    public void ANestedTableComesBackInsideItsCell()
    {
        using PdfDocument pdf = Tagged(WithNestedTable());

        Document back = PdfToDocument.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Table outer = Assert.Single(back.Sections.SelectMany(s => s.Body.Blocks).OfType<Table>());
        Table inner = Assert.Single(outer.Descendants<Table>());
        Assert.Contains("Inner d", inner.GetText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Inner", string.Concat(outer.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks.OfType<Paragraph>()).Select(p => p.GetText())), StringComparison.Ordinal);
    }
}
