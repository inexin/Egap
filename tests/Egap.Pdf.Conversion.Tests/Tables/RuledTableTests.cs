using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Conversion.Tests.Synthetic;

namespace Egap.Pdf.Conversion.Tests.Tables;

/// <summary>Ruled tables from vector paths (Build Plan task 7).</summary>
public sealed class RuledTableTests
{
    [Fact]
    public void AFullyRuledTableComesWithItsCellsAndSpans()
    {
        SyntheticDocument doc = SyntheticScenes.Tables();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        PageContent content = PageReader.Read(pdf, 0);

        (List<TableDraft> tables, _) = RuledTables.Find(content.Paths, content.Glyphs);

        TableDraft table = Assert.Single(tables);
        TruthTable truth = doc.Tables[0];
        Assert.Equal((truth.Rows, truth.Columns), (table.Rows, table.Columns));
        Assert.Equal(truth.Cells.Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text, c.Header)),
            table.Cells.Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text, c.Header)));
        Assert.Equal(truth.Box.Left, table.Box.Left, 1);
        Assert.Equal(truth.Box.Bottom, table.Box.Bottom, 1);
    }

    [Fact]
    public void APartlyRuledTableBecomesAHint()
    {
        SyntheticDocument doc = SyntheticScenes.Tables();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        PageContent content = PageReader.Read(pdf, 0);

        (_, List<TableHint> hints) = RuledTables.Find(content.Paths, content.Glyphs);

        TableHint hint = Assert.Single(hints);
        TruthTable partial = doc.Tables[1];
        Assert.Equal(3, hint.Rules.Count);
        Assert.Equal(partial.Box.Top, hint.Box.Top, 1);
        Assert.Equal(partial.Box.Bottom, hint.Box.Bottom, 1);
    }

    [Fact]
    public void ATableAcrossPagesIsFoundOnEachPage()
    {
        SyntheticDocument doc = SyntheticScenes.TableAcrossPages();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        for (int page = 0; page < doc.PageCount; page++)
        {
            PageContent content = PageReader.Read(pdf, page);
            (List<TableDraft> tables, _) = RuledTables.Find(content.Paths, content.Glyphs);
            TruthTable truth = doc.Tables.Single(t => t.Page == page);

            TableDraft table = Assert.Single(tables);
            Assert.Equal((truth.Rows, truth.Columns), (table.Rows, table.Columns));
            Assert.Equal(truth.Cells.Select(c => c.Text), table.Cells.Select(c => c.Text));
        }
    }

    [Fact]
    public void AFramedBoxIsNotATable()
    {
        PageContent content = Draw(c =>
        {
            c.StrokeRect(new RectF(50, 50, 200, 60), new Stroke(1), Ink);
            c.DrawText("A note in a frame.", new Vector2(60, 80), SyntheticDocument.Body, Ink);
        });

        Assert.Empty(RuledTables.Find(content.Paths, content.Glyphs).Tables);
    }

    [Fact]
    public void ShadedRowsWithoutRulesMakeAGrid()
    {
        // Rows told apart by fills only, columns by the cells' own boxes.
        PageContent content = Draw(c =>
        {
            SolidPaint grey = new(new RgbaF32(0.85f, 0.85f, 0.85f, 1));
            for (int r = 0; r < 3; r++)
            {
                for (int k = 0; k < 2; k++)
                {
                    c.FillRect(new RectF(50 + (k * 100), 50 + (r * 20), 99, 19), grey);
                    c.DrawText($"r{r}c{k}", new Vector2(55 + (k * 100), 64 + (r * 20)), SyntheticDocument.Body, Ink);
                }
            }
        });

        TableDraft table = Assert.Single(RuledTables.Find(content.Paths, content.Glyphs).Tables);

        Assert.Contains(table.Cells, c => c.Text == "r2c1" && c.Row == table.Rows - 1);
        Assert.True(table.Cells.Count(c => c.Text.Length > 0) == 6);
    }

    [Fact]
    public void CollinearPiecesMergeIntoOneRule()
    {
        List<RuledTables.Rule> merged = RuledTables.Merge([new(true, 10, 0, 50), new(true, 10.5, 50, 100), new(true, 10, 150, 200), new(false, 0, 0, 10)]);

        Assert.Equal(3, merged.Count);
        Assert.Contains(merged, r => r.Horizontal && r.From == 0 && r.To == 100);
    }

    private static readonly SolidPaint Ink = new(new RgbaF32(0, 0, 0, 1));

    private static PageContent Draw(Action<Canvas> draw)
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 400, 300));
        using (PdfCanvas canvas = new(page))
        {
            draw(canvas);
        }

        using MemoryStream saved = new();
        created.Save(saved);
        using PdfDocument pdf = PdfDocument.Open(saved.ToArray());
        return PageReader.Read(pdf, 0);
    }
}
