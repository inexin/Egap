namespace Egap.Documents.Layout;

/// <summary>Tables, first cut: grid columns, cell margins, rows that do not split; Build Plan task 18 completes them.</summary>
internal sealed partial class Paginator
{
    /// <summary>
    /// Lays out the rows of <paramref name="table"/> from <paramref name="firstRow"/> that fit above <paramref name="bottom"/>,
    /// from <paramref name="top"/>; <paramref name="nextRow"/> is the first row left over.
    /// </summary>
    private TableBox LayTable(Table table, double x, double top, double width, double bottom, int firstRow, out int nextRow, int maxRows = int.MaxValue)
    {
        TableFormat format = table.GetEffectiveFormat();
        double[] grid = GridWidths(table, width);
        double left = x + (format.Indent ?? 0);
        List<LayoutBox> rows = [];
        double at = top;
        int r = firstRow;
        for (; r < table.Rows.Count && r - firstRow < maxRows; r++)
        {
            Row row = table.Rows[r];
            RowFormat rowFormat = row.GetEffectiveFormat();
            (RowBox box, double height) = LayRow(row, rowFormat, format, grid, left, at);
            if (at + height > bottom + 0.01 && r > firstRow)
            {
                break;
            }

            if (at + height > bottom + 0.01)
            {
                break;
            }

            rows.Add(box);
            at += height;
        }

        nextRow = r;
        return new TableBox(table, new LayoutRect(left, top, grid.Sum(), at - top), rows);
    }

    private (RowBox Box, double Height) LayRow(Row row, RowFormat rowFormat, TableFormat tableFormat, double[] grid, double left, double top)
    {
        List<(Cell Cell, double X, double Width, List<LayoutBox> Blocks, double Height)> cells = [];
        double x = left;
        int column = 0;
        double height = 0;
        foreach (Cell cell in row.Cells)
        {
            CellFormat cellFormat = cell.GetEffectiveFormat();
            int span = Math.Max(1, cellFormat.GridSpan ?? 1);
            double width = 0;
            for (int i = column; i < Math.Min(grid.Length, column + span); i++)
            {
                width += grid[i];
            }

            column += span;
            double marginLeft = cellFormat.MarginLeft ?? tableFormat.CellMarginLeft ?? 5.4;
            double marginRight = cellFormat.MarginRight ?? tableFormat.CellMarginRight ?? 5.4;
            double marginTop = cellFormat.MarginTop ?? tableFormat.CellMarginTop ?? 0;
            double marginBottom = cellFormat.MarginBottom ?? tableFormat.CellMarginBottom ?? 0;
            (List<LayoutBox> blocks, double contentHeight) = LayStory(cell, x + marginLeft, top + marginTop, Math.Max(1, width - marginLeft - marginRight));
            double cellHeight = contentHeight + marginTop + marginBottom;
            if (cellFormat.VerticalMerge != VerticalMerge.Continue)
            {
                height = Math.Max(height, cellHeight);
            }

            cells.Add((cell, x, width, blocks, cellHeight));
            x += width;
        }

        if (rowFormat.Height is double given)
        {
            height = rowFormat.HeightRule == RowHeightRule.Exactly ? given : Math.Max(height, given);
        }

        List<LayoutBox> cellBoxes = [.. cells.Select(c => (LayoutBox)new CellBox(c.Cell, new LayoutRect(c.X, top, c.Width, height), c.Blocks))];
        return (new RowBox(row, new LayoutRect(left, top, x - left, height), cellBoxes, false), height);
    }

    /// <summary>The widths of the table's grid columns: the table's grid, scaled to fit, or equal columns.</summary>
    private static double[] GridWidths(Table table, double available)
    {
        int columns = Math.Max(1, table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.Cells.Sum(c => Math.Max(1, c.Format.GridSpan ?? 1))));
        if (table.Grid is { Count: > 0 } grid && grid.Sum() > 0)
        {
            double[] widths = [.. grid];
            if (widths.Length < columns)
            {
                widths = [.. widths, .. Enumerable.Repeat(widths.Average(), columns - widths.Length)];
            }

            return widths;
        }

        TableFormat format = table.GetEffectiveFormat();
        double total = format.Width is { Unit: WidthUnit.Points } points && points.Value > 0 ? points.Value
            : format.Width is { Unit: WidthUnit.Percent } percent && percent.Value > 0 ? available * percent.Value / 100
            : available;
        return [.. Enumerable.Repeat(total / columns, columns)];
    }
}
