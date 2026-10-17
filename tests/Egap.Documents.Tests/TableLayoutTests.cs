using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Table layout (Phase 6 Build Plan task 18).</summary>
public class TableLayoutTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    private static IEnumerable<RowBox> Rows(DocumentLayout layout, Table table)
    {
        return layout.FindBoxes(table).SelectMany(b => b.Box.Children).OfType<RowBox>();
    }

    [Fact]
    public void AutofitGivesWideContentMoreRoom()
    {
        Document document = new();
        Table table = document.AddSection().Body.AddTable(1, 2);
        table.Rows[0].Cells[0].Paragraphs.Single().AddRun("x");
        table.Rows[0].Cells[1].Paragraphs.Single().AddRun("a much longer stretch of text that wants a wide column");

        DocumentLayout layout = document.Layout(Isolated);

        CellBox[] cells = [.. Rows(layout, table).Single().Children.Cast<CellBox>()];
        Assert.True(cells[1].Bounds.Width > cells[0].Bounds.Width * 3);
        Assert.True(cells[0].Bounds.Width + cells[1].Bounds.Width <= 468 + 0.5);
    }

    [Fact]
    public void GridWidthsAndPercentTables()
    {
        Document document = new();
        Table table = document.AddSection().Body.AddTable(1, 2);
        table.Grid = [100, 200];
        table.Format.Width = TableWidth.Percent(50);

        DocumentLayout layout = document.Layout(Isolated);

        CellBox[] cells = [.. Rows(layout, table).Single().Children.Cast<CellBox>()];
        Assert.Equal(78, cells[0].Bounds.Width, 1);
        Assert.Equal(156, cells[1].Bounds.Width, 1);
    }

    [Fact]
    public void VerticalMergeSpansRows()
    {
        Document document = new();
        Table table = document.AddSection().Body.AddTable(3, 2);
        table.Grid = [100, 100];
        table.Rows[0].Cells[0].Format.VerticalMerge = VerticalMerge.Restart;
        table.Rows[1].Cells[0].Format.VerticalMerge = VerticalMerge.Continue;
        table.Rows[2].Cells[0].Format.VerticalMerge = VerticalMerge.Continue;

        DocumentLayout layout = document.Layout(Isolated);

        RowBox[] rows = [.. Rows(layout, table)];
        CellBox merged = (CellBox)rows[0].Children[0];
        Assert.Equal(rows.Sum(r => r.Bounds.Height), merged.Bounds.Height, 1);
        Assert.Single(rows[1].Children);
    }

    [Fact]
    public void HeaderRowsRepeatAndRowsSplit()
    {
        Document document = new();
        Table table = document.AddSection().Body.AddTable(40, 2);
        table.Grid = [200, 200];
        table.Rows[0].Format.IsHeader = true;
        table.Rows[0].Cells[0].Paragraphs.Single().AddRun("Header");
        for (int i = 0; i < 30; i++)
        {
            table.Rows[20].Cells[1].AddParagraph("a line inside one tall cell " + i);
        }

        DocumentLayout layout = document.Layout(Isolated);

        Assert.True(layout.Pages.Count >= 2);
        RowBox[] rows = [.. Rows(layout, table)];
        Assert.Contains(rows, r => r.IsRepeatedHeader);
        Assert.True(layout.FindBoxes(table.Rows[20]).Count >= 2, "the tall row splits across pages");
        Assert.All(layout.Pages.SelectMany(p => p.BodyLines), line => Assert.True(line.Bounds.Bottom <= 720.5));
    }

    [Fact]
    public void RowsThatMayNotSplitMoveWhole()
    {
        Document document = new();
        Body body = document.AddSection().Body;
        for (int i = 0; i < 38; i++)
        {
            body.AddParagraph("filler " + i);
        }

        Table table = body.AddTable(1, 1);
        table.Rows[0].Format.CantSplit = true;
        for (int i = 0; i < 10; i++)
        {
            table.Rows[0].Cells[0].AddParagraph("kept together " + i);
        }

        DocumentLayout layout = document.Layout(Isolated);

        Assert.Single(layout.FindBoxes(table.Rows[0]));
        Assert.Equal(1, layout.PageOf(table.Rows[0])!.Index);
    }

    [Fact]
    public void BordersComeFromCellThenTable()
    {
        Document document = new();
        Table table = document.AddSection().Body.AddTable(2, 2);
        Border outside = new(BorderStyle.Single, 1);
        Border inside = new(BorderStyle.Dotted, 0.5);
        Border own = new(BorderStyle.Double, 1.5);
        table.Format.BorderTop = outside;
        table.Format.BorderLeft = outside;
        table.Format.BorderInsideHorizontal = inside;
        table.Format.BorderInsideVertical = inside;
        table.Rows[1].Cells[1].Format.BorderBottom = own;
        table.Rows[0].Cells[0].Format.Shading = new Shading(ShadingPattern.Clear, DocumentColor.FromHex("FF0000"));

        DocumentLayout layout = document.Layout(Isolated);

        CellBox[][] cells = [.. Rows(layout, table).Select(r => r.Children.Cast<CellBox>().ToArray())];
        Assert.Equal(outside, cells[0][0].Borders.Top);
        Assert.Equal(outside, cells[0][0].Borders.Left);
        Assert.Equal(inside, cells[0][0].Borders.Bottom);
        Assert.Equal(inside, cells[0][1].Borders.Left);
        Assert.Equal(own, cells[1][1].Borders.Bottom);
        Assert.Equal(255, cells[0][0].Fill!.Value.R);
    }

    [Fact]
    public void HeaderRowsTallerThanThePageStopRepeating()
    {
        Document document = new();
        Table table = document.AddSection().Body.AddTable(6, 1);
        table.Rows[0].Format.IsHeader = true;
        table.Rows[0].Format.Height = 700;
        table.Rows[0].Format.HeightRule = RowHeightRule.Exactly;
        foreach (Row row in table.Rows.Skip(1))
        {
            row.Format.Height = 300;
            row.Format.HeightRule = RowHeightRule.Exactly;
        }

        DocumentLayout layout = document.Layout(Isolated);

        Assert.Equal(6, Rows(layout, table).Count(r => !r.IsRepeatedHeader));
        Assert.True(layout.Pages.Count < 10);
    }
}
