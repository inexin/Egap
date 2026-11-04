using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>Tables (ODF 1.3 Part 3, 9): columns, header rows, rows, cells and their spans and styles.</summary>
internal sealed partial class OdtReader
{
    /// <summary>The most columns and rows a repeated element adds, far more than any text table has.</summary>
    private const int MaxRepeat = 1024;

    private int cellCount;

    private void ReadTable(XElement element, BlockContext context)
    {
        Enter();
        try
        {
            OdfStyle? style = Find("table", element.Attr(TableNs, "style-name"));
            BreakForMaster(style, "table", context);
            Table table = new(document);
            List<double> grid = [];
            List<double?> relative = [];
            ReadColumns(element, grid, relative);

            int columns = grid.Count;
            int[] spanRows = new int[Math.Max(columns, 1)];
            int[] spanWidth = new int[Math.Max(columns, 1)];
            foreach ((XElement row, bool header) in Rows(element, false))
            {
                ReadRow(table, row, header, ref spanRows, ref spanWidth);
            }

            if (table.Rows.Count == 0)
            {
                return;
            }

            ApplyTableStyle(style, table, grid, relative);
            if (pending.PageBreak || Inherited(style, "table", "table-properties", FoNs, "break-before") is "page")
            {
                // Word holds a page break before a table as a break at the end of the paragraph before it.
                pending.PageBreak = false;
                if (context.Blocks.Count > 0 && context.Blocks[^1] is Paragraph previous)
                {
                    previous.Inlines.AddTrusted(new Break(document, BreakKind.Page));
                }
                else if (table.Rows[0].Cells.Count > 0 && table.Rows[0].Cells[0].Blocks.Count > 0 && table.Rows[0].Cells[0].Blocks[0] is Paragraph first)
                {
                    first.Format.PageBreakBefore = true;
                }
            }

            AddBlock(context, table);

            if (Inherited(style, "table", "table-properties", FoNs, "break-after") is "page")
            {
                pending.PageBreak = true;
            }
        }
        finally
        {
            depth--;
        }
    }

    private void ReadColumns(XElement parent, List<double> grid, List<double?> relative)
    {
        foreach (XElement child in parent.Elements())
        {
            if (child.Name == TableNs + "table-column")
            {
                OdfStyle? style = Find("table-column", child.Attr(TableNs, "style-name"));
                XElement? props = style?.Properties("table-column-properties");
                double width = OdfUnits.Length(props?.Attr(StyleNs, "column-width")) ?? 0;
                double? rel = OdfUnits.Int(props?.Attr(StyleNs, "rel-column-width")?.TrimEnd('*'));
                int repeat = Math.Clamp(OdfUnits.Int(child.Attr(TableNs, "number-columns-repeated")) ?? 1, 1, MaxRepeat);
                for (int i = 0; i < repeat && grid.Count < MaxRepeat; i++)
                {
                    grid.Add(Math.Max(0, width));
                    relative.Add(rel);
                }
            }
            else if (child.Name == TableNs + "table-columns" || child.Name == TableNs + "table-header-columns" || child.Name == TableNs + "table-column-group")
            {
                ReadColumns(child, grid, relative);
            }
        }
    }

    private static IEnumerable<(XElement Row, bool Header)> Rows(XElement parent, bool header)
    {
        foreach (XElement child in parent.Elements())
        {
            if (child.Name == TableNs + "table-row")
            {
                int repeat = Math.Clamp(OdfUnits.Int(child.Attr(TableNs, "number-rows-repeated")) ?? 1, 1, MaxRepeat);
                for (int i = 0; i < repeat; i++)
                {
                    yield return (child, header);
                }
            }
            else if (child.Name == TableNs + "table-header-rows")
            {
                foreach ((XElement, bool) row in Rows(child, true))
                {
                    yield return row;
                }
            }
            else if (child.Name == TableNs + "table-rows" || child.Name == TableNs + "table-row-group")
            {
                foreach ((XElement, bool) row in Rows(child, header))
                {
                    yield return row;
                }
            }
        }
    }

    private void ReadRow(Table table, XElement element, bool header, ref int[] spanRows, ref int[] spanWidth)
    {
        Row row = table.Rows.AddTrusted(new Row(document));
        if (header)
        {
            row.Format.IsHeader = true;
        }

        if (Find("table-row", element.Attr(TableNs, "style-name"))?.Properties("table-row-properties") is { } props)
        {
            if (OdfUnits.Length(props.Attr(StyleNs, "row-height")) is > 0 and var exactly)
            {
                row.Format.Height = exactly;
                row.Format.HeightRule = RowHeightRule.Exactly;
            }
            else if (OdfUnits.Length(props.Attr(StyleNs, "min-row-height")) is > 0 and var least)
            {
                row.Format.Height = least;
                row.Format.HeightRule = RowHeightRule.AtLeast;
            }

            if (props.Attr(FoNs, "keep-together") == "always")
            {
                row.Format.CantSplit = true;
            }
        }

        // Vertical spans: a cell spanning rows covers the cells below it, which come as covered cells (ODF 1.3 Part 3,
        // 9.1.5). The model has a continuing cell for each, as wide as the spanning cell.
        int column = 0;
        int skipCovered = 0;
        foreach (XElement child in element.Elements())
        {
            bool covered = child.Name == TableNs + "covered-table-cell";
            if (!covered && child.Name != TableNs + "table-cell")
            {
                continue;
            }

            int repeat = Math.Clamp(OdfUnits.Int(child.Attr(TableNs, "number-columns-repeated")) ?? 1, 1, MaxRepeat);
            for (int r = 0; r < repeat; r++)
            {
                if (++cellCount > MaxEntries * 100)
                {
                    throw new DocumentLimitException($"The document's tables have more than {MaxEntries * 100} cells.");
                }

                Grow(ref spanRows, ref spanWidth, column + 1);
                if (covered)
                {
                    if (skipCovered > 0)
                    {
                        skipCovered--;
                    }
                    else if (spanRows[column] > 0)
                    {
                        spanRows[column]--;
                        Cell continuing = row.Cells.AddTrusted(new Cell(document));
                        continuing.Format.VerticalMerge = VerticalMerge.Continue;
                        if (spanWidth[column] > 1)
                        {
                            continuing.Format.GridSpan = spanWidth[column];
                        }

                        continuing.Blocks.AddTrusted(new Paragraph(document));
                        skipCovered = spanWidth[column] - 1;
                    }
                    else
                    {
                        row.Cells.AddTrusted(new Cell(document)).Blocks.AddTrusted(new Paragraph(document));
                    }

                    column++;
                    continue;
                }

                Cell cell = row.Cells.AddTrusted(new Cell(document));
                int across = Math.Clamp(OdfUnits.Int(child.Attr(TableNs, "number-columns-spanned")) ?? 1, 1, MaxRepeat);
                int down = Math.Clamp(OdfUnits.Int(child.Attr(TableNs, "number-rows-spanned")) ?? 1, 1, int.MaxValue);
                if (across > 1)
                {
                    cell.Format.GridSpan = across;
                }

                if (down > 1)
                {
                    cell.Format.VerticalMerge = VerticalMerge.Restart;
                    spanRows[column] = down - 1;
                    spanWidth[column] = across;
                }

                ApplyCellStyle(Find("table-cell", child.Attr(TableNs, "style-name")), cell);
                ReadStory(child, cell.Blocks);
                skipCovered = across - 1;
                column++;
            }
        }
    }

    private static void Grow(ref int[] spanRows, ref int[] spanWidth, int length)
    {
        if (length > spanRows.Length)
        {
            Array.Resize(ref spanRows, Math.Max(length, spanRows.Length * 2));
            Array.Resize(ref spanWidth, spanRows.Length);
        }
    }

    private static void ApplyTableStyle(OdfStyle? style, Table table, List<double> grid, List<double?> relative)
    {
        XElement? props = style?.Properties("table-properties");
        double? width = OdfUnits.Length(props?.Attr(StyleNs, "width"));
        string? align = props?.Attr(TableNs, "align");
        if (align == "margins")
        {
            table.Format.Width = TableWidth.Percent(100);
        }
        else if (OdfUnits.Percent(props?.Attr(StyleNs, "rel-width")) is > 0 and var percent)
        {
            table.Format.Width = TableWidth.Percent(Math.Min(percent, 100));
        }
        else if (width is > 0)
        {
            table.Format.Width = TableWidth.Points(width.Value);
        }

        table.Format.Alignment = align switch
        {
            "center" => TableAlignment.Center,
            "right" => TableAlignment.Right,
            _ => null,
        };
        if (align is "left" or null && OdfUnits.Length(props?.Attr(FoNs, "margin-left")) is { } indent && indent != 0)
        {
            table.Format.Indent = indent;
        }

        if (OdfUnits.Color(props?.Attr(FoNs, "background-color")) is { } back)
        {
            table.Format.Shading = Shading.Solid(DocumentColor.FromRgb(back));
        }

        if (props?.Attr(StyleNs, "writing-mode") is "rl-tb" or "rl")
        {
            table.Format.Bidi = true;
        }

        // Only relative widths: shared out over the table's width.
        if (grid.Count > 0 && grid.All(w => w == 0) && relative.All(r => r is > 0) && width is > 0)
        {
            double total = relative.Sum(r => r!.Value);
            for (int i = 0; i < grid.Count; i++)
            {
                grid[i] = relative[i]!.Value / total * width.Value;
            }
        }

        if (grid.Count > 0 && grid.Any(w => w > 0))
        {
            table.Grid = grid;
            table.Format.Layout = TableLayoutKind.Fixed;
            foreach (Row row in table.Rows)
            {
                int column = 0;
                foreach (Cell cell in row.Cells)
                {
                    int span = cell.Format.GridSpan ?? 1;
                    double sum = 0;
                    for (int i = column; i < column + span && i < grid.Count; i++)
                    {
                        sum += grid[i];
                    }

                    if (sum > 0)
                    {
                        cell.Format.Width = TableWidth.Points(sum);
                    }

                    column += span;
                }
            }
        }
    }

    private void ApplyCellStyle(OdfStyle? style, Cell cell)
    {
        if (style is null)
        {
            return;
        }

        // A cell's automatic style may build on a common cell style: the parents' properties first, the style's over them.
        foreach (OdfStyle s in Chain(style).Reverse())
        {
            if (s.Properties("table-cell-properties") is not { } p)
            {
                continue;
            }

            CellFormat f = cell.Format;
            string? all = p.Attr(FoNs, "border");
            string? allWidths = p.Attr(StyleNs, "border-line-width");
            Border? Side(string side) => OdfUnits.Border(p.Attr(FoNs, "border-" + side) ?? all, p.Attr(StyleNs, "border-line-width-" + side) ?? allWidths);
            f.BorderTop = Side("top") ?? f.BorderTop;
            f.BorderBottom = Side("bottom") ?? f.BorderBottom;
            f.BorderLeft = Side("left") ?? f.BorderLeft;
            f.BorderRight = Side("right") ?? f.BorderRight;
            f.BorderDiagonalDown = OdfUnits.Border(p.Attr(StyleNs, "diagonal-tl-br")) is { IsVisible: true } down ? down : f.BorderDiagonalDown;
            f.BorderDiagonalUp = OdfUnits.Border(p.Attr(StyleNs, "diagonal-bl-tr")) is { IsVisible: true } up ? up : f.BorderDiagonalUp;
            double? padding = OdfUnits.Length(p.Attr(FoNs, "padding"));
            f.MarginTop = OdfUnits.Length(p.Attr(FoNs, "padding-top")) ?? padding ?? f.MarginTop;
            f.MarginBottom = OdfUnits.Length(p.Attr(FoNs, "padding-bottom")) ?? padding ?? f.MarginBottom;
            f.MarginLeft = OdfUnits.Length(p.Attr(FoNs, "padding-left")) ?? padding ?? f.MarginLeft;
            f.MarginRight = OdfUnits.Length(p.Attr(FoNs, "padding-right")) ?? padding ?? f.MarginRight;
            if (p.Attr(FoNs, "background-color") is { } background)
            {
                f.Shading = OdfUnits.Color(background) is { } color ? Shading.Solid(DocumentColor.FromRgb(color)) : null;
            }

            f.VerticalAlignment = p.Attr(StyleNs, "vertical-align") switch
            {
                "middle" => VerticalAlignment.Center,
                "bottom" => VerticalAlignment.Bottom,
                "top" => VerticalAlignment.Top,
                _ => f.VerticalAlignment,
            };
            f.TextDirection = p.Attr(StyleNs, "writing-mode") switch
            {
                "tb-rl" or "tb" => TextDirection.TopToBottom,
                "bt-lr" => TextDirection.BottomToTop,
                "lr-tb" => TextDirection.Horizontal,
                _ => f.TextDirection,
            };
            if (p.Attr(FoNs, "wrap-option") == "no-wrap")
            {
                f.NoWrap = true;
            }
        }
    }
}
