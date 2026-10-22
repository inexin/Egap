using Egap.Documents.Layout;

namespace Egap.Documents.Docx;

/// <summary>
/// Flex and grid containers and boxed groups as Word tables (Phase 9 design Q8), for formats without such containers: a
/// borderless table whose cells hold copies of the items' blocks, each cell with its item's background, borders and padding.
/// A row container is one row, its items wrapped onto more rows where their widths say so; a column container is a row per
/// item; a grid keeps its columns, spans and empty cells. Rounded corners and shadows have no place in a table and are left out.
/// </summary>
internal static class ContainerTables
{
    /// <summary>The table for a flex or grid container, or a one-cell table for a boxed group's blocks; null for other blocks.</summary>
    public static Table? For(Block block, double textWidth)
    {
        return block switch
        {
            FlexContainer flex => Flex(flex, textWidth),
            GridContainer grid => Grid(grid, textWidth),
            ContentControl { Box: { } box } group => Boxed(group, box, textWidth),
            _ => null,
        };
    }

    /// <summary>The text width of the section a block is in: what percentages and fractions share.</summary>
    public static double TextWidth(Block block)
    {
        PageSetup? setup = (block.GetAncestor<Body>()?.Section ?? (block.Document.Sections.Count > 0 ? block.Document.Sections[0] : null))?.PageSetup;
        return Math.Max(36, (setup?.PageWidth ?? 612) - (setup?.MarginLeft ?? 72) - (setup?.MarginRight ?? 72));
    }

    private static Table Flex(FlexContainer flex, double textWidth)
    {
        double width = Inner(flex.Box, textWidth);
        Table table = Start(flex.Document, flex.Box, textWidth, Math.Max(flex.Layout.RowGap, flex.Layout.ColumnGap));
        List<FlexItem> items = [.. flex.Items.OrderBy(i => i.Order)];
        if (flex.Layout.Direction is FlexDirection.RowReverse or FlexDirection.ColumnReverse)
        {
            items.Reverse();
        }

        if (!flex.Layout.IsRow)
        {
            table.Grid = [width];
            foreach (FlexItem item in items)
            {
                AddCell(table.AddRow(), item, item.Box, width, Align(item.AlignSelf));
            }

            return table;
        }

        // Each item's width: its basis or width where given, the rest sharing what is left by grow factor (or equally).
        double[] given = [.. items.Select(i => Width(i.Basis, width) ?? Width(i.Box?.Width ?? default, width) ?? 0)];
        List<List<int>> rows = [[]];
        double used = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (flex.Layout.Wrap != FlexWrap.NoWrap && rows[^1].Count > 0 && given[i] > 0 && used + given[i] > width + 0.5)
            {
                rows.Add([]);
                used = 0;
            }

            rows[^1].Add(i);
            used += given[i];
        }

        int columns = rows.Max(r => r.Count);
        double[] grid = new double[columns];
        foreach (List<int> row in rows)
        {
            double fixedWidth = row.Sum(i => given[i]);
            double weight = row.Where(i => given[i] == 0).Sum(i => Math.Max(items[i].Grow, 1));
            for (int c = 0; c < row.Count; c++)
            {
                int i = row[c];
                double share = given[i] > 0 ? given[i] : Math.Max(0, width - fixedWidth) * Math.Max(items[i].Grow, 1) / Math.Max(weight, 1);
                grid[c] = Math.Max(grid[c], share);
            }
        }

        table.Grid = grid;
        foreach (List<int> row in rows)
        {
            Row tableRow = table.AddRow();
            for (int c = 0; c < row.Count; c++)
            {
                FlexItem item = items[row[c]];
                AddCell(tableRow, item, item.Box, grid[c], Align(item.AlignSelf ?? flex.Layout.AlignItems));
            }

            if (row.Count < columns)
            {
                tableRow.Cells[^1].Format.GridSpan = columns - row.Count + 1;
            }
        }

        return table;
    }

    private static Table Grid(GridContainer container, double textWidth)
    {
        double width = Inner(container.Box, textWidth);
        GridLayout layout = container.Layout;
        Table table = Start(container.Document, container.Box, textWidth, Math.Max(layout.RowGap, layout.ColumnGap));
        GridPlacement placement = Paginator.PlaceGridItems(container);
        int columns = placement.Columns;

        // Fixed tracks keep their size; fractions share what is left by their factor, other tracks as one fraction each.
        double[] grid = new double[columns];
        double fixedWidth = 0, fractions = 0;
        for (int c = 0; c < columns; c++)
        {
            GridTrack track = placement.ColumnTrack(c);
            double? size = track.Max.Unit switch
            {
                GridTrackUnit.Points => track.Max.Value,
                GridTrackUnit.Percent => width * track.Max.Value / 100,
                _ => track.Min.Unit == GridTrackUnit.Points ? track.Min.Value : null,
            };
            if (size is double points)
            {
                grid[c] = points;
                fixedWidth += points;
            }
            else
            {
                fractions += track.Max.Unit == GridTrackUnit.Fraction ? Math.Max(track.Max.Value, 0.01) : 1;
            }
        }

        for (int c = 0; c < columns; c++)
        {
            GridTrack track = placement.ColumnTrack(c);
            if (grid[c] == 0)
            {
                double share = track.Max.Unit == GridTrackUnit.Fraction ? Math.Max(track.Max.Value, 0.01) : 1;
                grid[c] = Math.Max(9, Math.Max(0, width - fixedWidth) * share / Math.Max(fractions, 1));
            }
        }

        table.Grid = grid;
        // Items placed on the same cell share it: the first sets the cell, the others add their blocks to it.
        Dictionary<(int Row, int Column), GridSlot> starts = [];
        Dictionary<(int Row, int Column), List<GridSlot>> sharing = [];
        foreach (GridSlot slot in placement.Slots)
        {
            if (!starts.TryAdd((slot.Row, slot.Column), slot))
            {
                (sharing.TryGetValue((slot.Row, slot.Column), out List<GridSlot>? list) ? list : sharing[(slot.Row, slot.Column)] = []).Add(slot);
            }
        }

        Dictionary<(int Row, int Column), GridSlot> covered = [];
        foreach (GridSlot slot in placement.Slots)
        {
            for (int r = slot.Row + 1; r < slot.Row + slot.RowSpan && r < placement.Rows; r++)
            {
                covered[(r, slot.Column)] = slot;
            }
        }

        for (int r = 0; r < placement.Rows; r++)
        {
            Row row = table.AddRow();
            int c = 0;
            while (c < columns)
            {
                if (starts.TryGetValue((r, c), out GridSlot slot))
                {
                    int span = Math.Min(slot.ColumnSpan, columns - c);
                    Cell cell = AddCell(row, slot.Item, slot.Item.Box, grid.Skip(c).Take(span).Sum(), Align(slot.Item.AlignSelf ?? layout.AlignItems));
                    foreach (GridSlot other in sharing.GetValueOrDefault((r, c)) ?? [])
                    {
                        foreach (Block block in other.Item.Blocks)
                        {
                            cell.Blocks.Add((Block)block.Clone());
                        }
                    }

                    if (span > 1)
                    {
                        cell.Format.GridSpan = span;
                    }

                    if (slot.RowSpan > 1)
                    {
                        cell.Format.VerticalMerge = VerticalMerge.Restart;
                    }

                    c += span;
                }
                else if (covered.TryGetValue((r, c), out GridSlot above))
                {
                    int span = Math.Min(above.ColumnSpan, columns - c);
                    Cell continuation = row.AddCell();
                    continuation.Format.VerticalMerge = VerticalMerge.Continue;
                    if (span > 1)
                    {
                        continuation.Format.GridSpan = span;
                    }

                    c += span;
                }
                else
                {
                    // A cell no item fills.
                    row.AddCell().Format.Width = TableWidth.Points(grid[c]);
                    c++;
                }
            }
        }

        return table;
    }

    private static Table Boxed(ContentControl group, BoxStyle box, double textWidth)
    {
        Table table = Start(group.Document, null, textWidth, 0);
        table.Format.Indent = box.MarginLeft;
        double outer = box.Width.Unit switch
        {
            WidthUnit.Points => box.Width.Value,
            WidthUnit.Percent => (textWidth - box.MarginsX) * box.Width.Value / 100,
            _ => textWidth - box.MarginsX,
        };
        table.Format.Width = TableWidth.Points(Math.Max(9, outer));
        table.Grid = [Math.Max(9, outer)];
        Cell cell = new(group.Document);
        table.AddRow().Cells.Add(cell);
        Style(cell, box, Math.Max(9, outer), null);
        foreach (Block block in group.Blocks)
        {
            cell.Blocks.Add((Block)block.Clone());
        }

        return table;
    }

    /// <summary>An empty borderless table for a container's box: its margins as the indent, its background and borders.</summary>
    private static Table Start(Document document, BoxStyle? box, double textWidth, double gap)
    {
        Table table = new(document);
        TableFormat format = table.Format;
        format.Layout = TableLayoutKind.Fixed;
        format.Indent = box?.MarginLeft ?? 0;
        format.Width = TableWidth.Points(Math.Max(9, box?.Width.Unit switch
        {
            WidthUnit.Points => box.Width.Value,
            WidthUnit.Percent => (textWidth - box.MarginsX) * box.Width.Value / 100,
            _ => textWidth - (box?.MarginsX ?? 0),
        }));
        format.CellMarginLeft = format.CellMarginRight = format.CellMarginTop = format.CellMarginBottom = 0;
        foreach (Action<Border> set in (ReadOnlySpan<Action<Border>>)[b => format.BorderTop = b, b => format.BorderBottom = b, b => format.BorderLeft = b, b => format.BorderRight = b, b => format.BorderInsideHorizontal = b, b => format.BorderInsideVertical = b])
        {
            set(Border.None);
        }

        if (box is not null)
        {
            format.BorderTop = box.BorderTop ?? Border.None;
            format.BorderBottom = box.BorderBottom ?? Border.None;
            format.BorderLeft = box.BorderLeft ?? Border.None;
            format.BorderRight = box.BorderRight ?? Border.None;
            if (box.Background is { } background)
            {
                format.Shading = Shading.Solid(background);
            }
        }

        if (gap > 0)
        {
            format.CellSpacing = gap / 2;
        }

        return table;
    }

    private static Cell AddCell(Row row, BlockContainer item, BoxStyle? box, double width, VerticalAlignment? alignment)
    {
        Cell cell = new(row.Document);
        row.Cells.Add(cell);
        Style(cell, box, width, alignment);
        foreach (Block block in item.Blocks)
        {
            cell.Blocks.Add((Block)block.Clone());
        }

        return cell;
    }

    private static void Style(Cell cell, BoxStyle? box, double width, VerticalAlignment? alignment)
    {
        CellFormat format = cell.Format;
        format.Width = TableWidth.Points(width);
        format.VerticalAlignment = alignment ?? VerticalAlignment.Top;
        if (box is null)
        {
            return;
        }

        format.MarginTop = box.PaddingTop;
        format.MarginBottom = box.PaddingBottom;
        format.MarginLeft = box.PaddingLeft;
        format.MarginRight = box.PaddingRight;
        format.BorderTop = box.BorderTop;
        format.BorderBottom = box.BorderBottom;
        format.BorderLeft = box.BorderLeft;
        format.BorderRight = box.BorderRight;
        if (box.Background is { } background)
        {
            format.Shading = Shading.Solid(background);
        }
    }

    private static double Inner(BoxStyle? box, double textWidth)
    {
        double outer = box?.Width.Unit switch
        {
            WidthUnit.Points => box.Width.Value,
            WidthUnit.Percent => (textWidth - box.MarginsX) * box.Width.Value / 100,
            _ => textWidth - (box?.MarginsX ?? 0),
        };
        return Math.Max(9, outer - (box?.Horizontal ?? 0));
    }

    private static double? Width(TableWidth width, double available)
    {
        return width switch
        {
            { Unit: WidthUnit.Points, Value: > 0 } points => points.Value,
            { Unit: WidthUnit.Percent, Value: > 0 } percent => available * percent.Value / 100,
            _ => null,
        };
    }

    private static VerticalAlignment? Align(LayoutAlignment? alignment)
    {
        return alignment switch
        {
            LayoutAlignment.Center => VerticalAlignment.Center,
            LayoutAlignment.End => VerticalAlignment.Bottom,
            _ => null,
        };
    }
}
