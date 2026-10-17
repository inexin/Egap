using Egap.Pdf.Layout.Tests.Synthetic;

namespace Egap.Pdf.Layout.Tests.Tables;

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
