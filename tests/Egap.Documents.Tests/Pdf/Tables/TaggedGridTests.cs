using Egap.Documents.Pdf;
using Egap.Documents.Tests.Pdf.Synthetic;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Tables;

/// <summary>Tagged tables without span attributes, as Word writes them, get their spans from rules or text (3.7 task 2).</summary>
public sealed class TaggedGridTests
{
    [Fact]
    public void ARuledTableTakesItsSpansFromTheMissingRules()
    {
        SyntheticDocument doc = SyntheticScenes.Tables(tagged: true, spanAttributes: false);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        LayoutTable table = PdfLayoutAnalyzer.Analyze(pdf, 0).Tables[0];

        TruthTable truth = doc.Tables[0];
        Assert.True(table.IsRuled);
        Assert.Equal((truth.Rows, truth.Columns), (table.RowCount, table.ColumnCount));
        Assert.Equal(truth.Cells.Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text)),
            table.Cells.Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text)));
    }

    [Fact]
    public void ATitleRowOfOneCellSpansAnUnruledTable()
    {
        SyntheticDocument doc = new(tagged: true) { SpanAttributes = false };
        doc.Table(
        [
            ["Opening hours", "", ""],
            ["Day", "From", "To"],
            ["Monday", "08:00", "16:00"],
            ["Tuesday", "09:00", "17:00"],
        ], [120, 80, 80], TruthRules.None, merges: [(0, 0, 1, 3)]);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        LayoutTable table = PdfLayoutAnalyzer.Analyze(pdf, 0).Tables[0];

        Assert.Equal((4, 3), (table.RowCount, table.ColumnCount));
        Assert.Contains(table.Cells, c => (c.Row, c.Column, c.ColumnSpan, c.Text) == (0, 0, 3, "Opening hours"));
        Assert.Equal(9, table.Cells.Count(c => c.Row > 0 && c.ColumnSpan == 1));
    }

    [Fact]
    public void ATaggedTableAcrossPagesIsOneTable()
    {
        SyntheticDocument doc = SyntheticScenes.TableAcrossPages(tagged: true);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        Egap.Documents.Document back = PdfToDocument.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Egap.Documents.Table table = Assert.Single(back.Descendants<Egap.Documents.Table>());
        Assert.Equal(61, table.Rows.Count);
        Assert.Equal("60", table.Rows[^1].Cells[0].GetText().Trim());
    }

    [Fact]
    public void ARuledTableTaggedAsParagraphsIsStillATable()
    {
        SyntheticDocument doc = new(tagged: true) { TablesAsParagraphs = true };
        doc.Paragraph("The results by region follow.");
        doc.Table(
        [
            ["Region", "2024", "2025"],
            ["North", "120", "135"],
            ["South", "98", "104"],
        ], [120, 80, 80], TruthRules.All);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        LayoutTable table = Assert.Single(PdfLayoutAnalyzer.Analyze(pdf, 0).Tables);

        Assert.Equal((3, 3), (table.RowCount, table.ColumnCount));
        Assert.Contains(table.Cells, c => (c.Row, c.Column, c.Text) == (2, 2, "104"));
    }

    [Fact]
    public void APartlyRuledTableTaggedAsParagraphsTakesAParagraphPerCell()
    {
        SyntheticDocument doc = new(tagged: true) { TablesAsParagraphs = true };
        doc.Paragraph("The test statistics follow.");
        doc.Table(
        [
            ["Test", "Chi-Square", "DF", "Pr > ChiSq"],
            ["Log-Rank", "1.5209", "1", "0.2175"],
            ["Wilcoxon", "0.7660", "1", "0.3815"],
        ], [90, 70, 30, 70], TruthRules.Partial);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        LayoutTable table = Assert.Single(PdfLayoutAnalyzer.Analyze(pdf, 0).Tables);

        Assert.Equal((3, 4), (table.RowCount, table.ColumnCount));
        Assert.Contains(table.Cells, c => (c.Row, c.Column, c.Text) == (2, 3, "0.3815"));
    }

    [Fact]
    public void AnEmptyRuledTableComesBackOnItsGrid()
    {
        SyntheticDocument doc = new(tagged: true) { SpanAttributes = false };
        doc.Paragraph("Fill in the form below.");
        doc.Table([["", "", ""], ["", "", ""]], [100, 100, 100], TruthRules.All, headerRows: 0);
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        LayoutTable table = Assert.Single(PdfLayoutAnalyzer.Analyze(pdf, 0).Tables);

        Assert.True(table.IsRuled);
        Assert.Equal((2, 3), (table.RowCount, table.ColumnCount));
        Assert.All(table.Cells, c => Assert.Equal("", c.Text));
    }
}
