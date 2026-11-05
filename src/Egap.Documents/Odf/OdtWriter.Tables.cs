using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>Tables (ODF 1.3 Part 3, 9): columns, header rows, rows, cells and their spans as covered cells.</summary>
internal sealed partial class OdtWriter
{
    private int tableCount;

    private void WriteTable(Table table, BlockTarget target)
    {
        if (table.Rows.Count == 0)
        {
            return;
        }

        TableFormat format = table.GetEffectiveFormat();
        if (format.Position is not null)
        {
            warnings.Add(ConversionWarningKind.LayoutApproximated, "Floating tables were written in the text: OpenDocument's tables do not float.");
        }

        double textWidth = Docx.ContainerTables.TextWidth(table);
        List<double> grid = Grid(table, textWidth);
        double width = grid.Sum();

        // The table's style: width, alignment, background, and a master page it starts.
        XElement props = new(StyleNs + "table-properties", new XAttribute(StyleNs + "width", Pt(width)));
        string align = format.Alignment switch
        {
            TableAlignment.Center => "center",
            TableAlignment.Right => "right",
            _ => format.Indent is { } indent && indent != 0 ? "margins" : "left",
        };
        props.Add(new XAttribute(TableNs + "align", align));
        if (align == "margins")
        {
            props.Add(new XAttribute(FoNs + "margin-left", Pt(format.Indent!.Value)),
                new XAttribute(FoNs + "margin-right", Pt(Math.Max(0, textWidth - width - format.Indent.Value))));
        }

        if (format.Shading is { } shading)
        {
            props.Add(new XAttribute(FoNs + "background-color", Background(shading)));
        }

        if (format.Bidi == true)
        {
            props.Add(new XAttribute(StyleNs + "writing-mode", "rl-tb"));
        }

        string? master = null;
        if (target.Main && pendingMaster is { } pending)
        {
            master = pending.Master;
            if (pending.PageNumber is { } number)
            {
                props.Add(new XAttribute(StyleNs + "page-number", Int(Math.Max(0, number))));
            }

            pendingMaster = null;
        }

        string name = "Table" + Int(++tableCount);
        XElement element = new(TableNs + "table", new XAttribute(TableNs + "name", name), new XAttribute(TableNs + "style-name", Auto("table", null, master, props)));
        foreach (double column in grid)
        {
            element.Add(new XElement(TableNs + "table-column",
                new XAttribute(TableNs + "style-name", Auto("table-column", null, null, new XElement(StyleNs + "table-column-properties",
                    new XAttribute(StyleNs + "column-width", Pt(column)))))));
        }

        // Vertical merges: how many rows each restarting cell spans, by its row and grid column.
        List<List<(Cell Cell, int Column, int Span)>> layout = [.. table.Rows.Select(Positions)];
        XElement? header = null;
        bool headerDone = false;
        for (int r = 0; r < table.Rows.Count; r++)
        {
            Row row = table.Rows[r];
            RowFormat rowFormat = row.GetEffectiveFormat();
            XElement rowElement = new(TableNs + "table-row");
            XElement rowProps = new(StyleNs + "table-row-properties");
            if (rowFormat.Height is > 0 and var height)
            {
                rowProps.Add(new XAttribute(StyleNs + (rowFormat.HeightRule == RowHeightRule.Exactly ? "row-height" : "min-row-height"), Pt(height)));
            }

            if (rowFormat.CantSplit == true)
            {
                rowProps.Add(new XAttribute(FoNs + "keep-together", "always"));
            }

            if (rowProps.HasAttributes)
            {
                rowElement.Add(new XAttribute(TableNs + "style-name", Auto("table-row", null, null, rowProps)));
            }

            int column = 0;
            foreach ((Cell cell, int start, int span) in layout[r])
            {
                for (; column < start; column++)
                {
                    rowElement.Add(new XElement(TableNs + "covered-table-cell"));
                }

                CellFormat cellFormat = cell.GetEffectiveFormat();
                if (cellFormat.VerticalMerge == VerticalMerge.Continue && r > 0)
                {
                    for (int i = 0; i < span; i++)
                    {
                        rowElement.Add(new XElement(TableNs + "covered-table-cell"));
                    }

                    column += span;
                    continue;
                }

                XElement cellElement = new(TableNs + "table-cell", new XAttribute(TableNs + "style-name", CellStyle(cellFormat, format, r, table.Rows.Count, start, span, grid.Count)),
                    new XAttribute(OfficeNs + "value-type", "string"));
                int rows = cellFormat.VerticalMerge == VerticalMerge.Restart ? RowsSpanned(layout, r, start) : 1;
                if (rows > 1)
                {
                    cellElement.Add(new XAttribute(TableNs + "number-rows-spanned", Int(rows)));
                }

                if (span > 1)
                {
                    cellElement.Add(new XAttribute(TableNs + "number-columns-spanned", Int(span)));
                }

                WriteStory(cell.Blocks, cellElement);
                rowElement.Add(cellElement);
                for (int i = 1; i < span; i++)
                {
                    rowElement.Add(new XElement(TableNs + "covered-table-cell"));
                }

                column = start + span;
            }

            for (; column < grid.Count; column++)
            {
                rowElement.Add(new XElement(TableNs + "covered-table-cell"));
            }

            if (!rowElement.Elements().Any(e => e.Name.Namespace == TableNs && e.Name.LocalName.EndsWith("cell", StringComparison.Ordinal)))
            {
                rowElement.Add(new XElement(TableNs + "table-cell", new XElement(TextNs + "p")));
            }

            if (!headerDone && rowFormat.IsHeader == true)
            {
                header ??= new XElement(TableNs + "table-header-rows");
                header.Add(rowElement);
                if (header.Parent is null)
                {
                    element.Add(header);
                }

                continue;
            }

            headerDone = true;
            element.Add(rowElement);
        }

        target.Container.Add(element);
    }

    /// <summary>A row's cells with the grid column each starts at and the columns it spans.</summary>
    private static List<(Cell Cell, int Column, int Span)> Positions(Row row)
    {
        List<(Cell, int, int)> cells = [];
        int column = 0;
        foreach (Cell cell in row.Cells)
        {
            int span = Math.Clamp(cell.Format.GridSpan ?? 1, 1, 1000);
            cells.Add((cell, column, span));
            column += span;
        }

        return cells;
    }

    private static int RowsSpanned(List<List<(Cell Cell, int Column, int Span)>> layout, int row, int column)
    {
        int rows = 1;
        for (int r = row + 1; r < layout.Count; r++)
        {
            if (!layout[r].Any(c => c.Column == column && c.Cell.GetEffectiveFormat().VerticalMerge == VerticalMerge.Continue))
            {
                break;
            }

            rows++;
        }

        return rows;
    }

    /// <summary>The table's column widths: its grid, else its first row's cell widths, else the text width shared out.</summary>
    private static List<double> Grid(Table table, double textWidth)
    {
        int columns = table.Rows.Max(r => r.Cells.Sum(c => Math.Clamp(c.Format.GridSpan ?? 1, 1, 1000)));
        columns = Math.Max(columns, 1);
        List<double> grid = table.Grid is { Count: > 0 } g ? [.. g.Take(columns)] : [];
        if (grid.Count < columns)
        {
            double tableWidth = table.Format.Width is { Unit: WidthUnit.Points, Value: > 0 } w ? w.Value
                : table.Format.Width is { Unit: WidthUnit.Percent, Value: > 0 } p ? textWidth * p.Value / 100 : textWidth;
            double known = grid.Sum();
            double each = Math.Max(0, tableWidth - known) / (columns - grid.Count);
            while (grid.Count < columns)
            {
                grid.Add(each);
            }
        }

        for (int i = 0; i < grid.Count; i++)
        {
            if (!(grid[i] > 0))
            {
                grid[i] = 0.75;
            }
        }

        return grid;
    }

    /// <summary>
    /// A cell's style: its borders, with the table's outer and inside borders for those it does not set, its padding,
    /// vertical alignment and background.
    /// </summary>
    private string CellStyle(CellFormat cell, TableFormat table, int row, int rows, int column, int span, int columns)
    {
        XElement props = new(StyleNs + "table-cell-properties");
        Border? top = cell.BorderTop ?? (row == 0 ? table.BorderTop : table.BorderInsideHorizontal);
        Border? bottom = cell.BorderBottom ?? (row == rows - 1 ? table.BorderBottom : table.BorderInsideHorizontal);
        Border? left = cell.BorderLeft ?? (column == 0 ? table.BorderLeft : table.BorderInsideVertical);
        Border? right = cell.BorderRight ?? (column + span >= columns ? table.BorderRight : table.BorderInsideVertical);
        Borders(props, top ?? Border.None, bottom ?? Border.None, left ?? Border.None, right ?? Border.None, padding: false);
        props.Add(new XAttribute(FoNs + "padding-top", Pt(cell.MarginTop ?? table.CellMarginTop ?? 0)),
            new XAttribute(FoNs + "padding-bottom", Pt(cell.MarginBottom ?? table.CellMarginBottom ?? 0)),
            new XAttribute(FoNs + "padding-left", Pt(cell.MarginLeft ?? table.CellMarginLeft ?? 5.4)),
            new XAttribute(FoNs + "padding-right", Pt(cell.MarginRight ?? table.CellMarginRight ?? 5.4)));
        if (cell.VerticalAlignment is { } vertical)
        {
            props.Add(new XAttribute(StyleNs + "vertical-align", vertical switch
            {
                VerticalAlignment.Center => "middle",
                VerticalAlignment.Bottom => "bottom",
                _ => "top",
            }));
        }

        if (cell.Shading is { } shading)
        {
            props.Add(new XAttribute(FoNs + "background-color", Background(shading)));
        }

        if (cell.TextDirection == TextDirection.TopToBottom)
        {
            props.Add(new XAttribute(StyleNs + "writing-mode", "tb-rl"));
        }

        return Auto("table-cell", null, null, props);
    }
}
