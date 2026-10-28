using Egami.Pixels;

namespace Egap.Documents.Layout;

/// <summary>
/// Tables (Phase 6 Build Plan task 18): grid widths, fixed or fitted to their content; cells spanning grid columns and
/// merged down rows; header rows repeated on each page; rows that split across pages unless they may not; and each cell's
/// borders, from its own, the table's inside and outside borders and the table style, and its shading.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>A table measured for one column width: grid, cells with their borders, and each row's height.</summary>
    private sealed class TablePlan
    {
        public required Table Table { get; init; }

        public required double Left { get; init; }

        public required double Width { get; init; }

        public required List<RowPlan> Rows { get; init; }

        public required int HeaderRows { get; init; }

        /// <summary>How many of the last rows repeat at the bottom of each part the table continues from.</summary>
        public int FooterRows { get; init; }
    }

    private sealed class RowPlan
    {
        public required Row Row { get; init; }

        public required RowFormat Format { get; init; }

        public required List<CellPlan> Cells { get; init; }

        /// <summary>The row's height laid out whole, merges counted.</summary>
        public double Height { get; set; }

        /// <summary>Whether a cell merges down from or into this row.</summary>
        public bool HasMerges { get; set; }
    }

    private sealed class CellPlan
    {
        public required Cell Cell { get; init; }

        public required CellFormat Format { get; init; }

        public required int Column { get; init; }

        public required double X { get; init; }

        public required double Width { get; init; }

        public required (double Top, double Left, double Bottom, double Right) Margins { get; init; }

        public required CellBorders Borders { get; init; }

        public required Rgba8? Fill { get; init; }

        /// <summary>The rows a restarting vertical merge covers (1 for a plain cell, 0 for a cell a merge continues into).</summary>
        public int RowSpan { get; set; } = 1;

        public double ContentHeight { get; set; }
    }

    /// <summary>
    /// Plans made in this pass, by table, position and width: a nested table is planned when its cell is measured and again
    /// when it is placed, which, nested deep, doubles the work at every level.
    /// </summary>
    private readonly Dictionary<(Table Table, double X, double Available), TablePlan> plans = [];

    private TablePlan Plan(Table table, double x, double available)
    {
        if (plans.TryGetValue((table, x, available), out TablePlan? planned))
        {
            return planned;
        }

        TablePlan plan = MakePlan(table, x, available);
        plans[(table, x, available)] = plan;
        return plan;
    }

    private TablePlan MakePlan(Table table, double x, double available)
    {
        TableFormat format = table.GetEffectiveFormat();
        double[] grid = GridWidths(table, format, available);
        double width = grid.Sum();
        double left = x + (format.Indent ?? 0);

        // Before Word 2013's layout (compatibility mode 15), a left-aligned table's indent is measured to its first cell's
        // text, so the table's edge sits the cell's left margin further left.
        if (document.Settings.WordCompatibility.TableIndentFromCellText && format.Alignment is null or TableAlignment.Left
            && table.Rows.Count > 0 && table.Rows[0].Cells.Count > 0 && table.Rows[0].Cells[0] is { } firstCell)
        {
            left -= firstCell.GetEffectiveFormat().MarginLeft ?? format.CellMarginLeft ?? 5.4;
        }

        if (format.Alignment == TableAlignment.Center)
        {
            left = x + ((available - width) / 2);
        }
        else if (format.Alignment == TableAlignment.Right)
        {
            left = x + available - width;
        }

        List<RowPlan> rows = [];
        for (int r = 0; r < table.Rows.Count; r++)
        {
            Row row = table.Rows[r];
            List<CellPlan> cells = [];
            int column = 0;
            double cx = left;
            for (int c = 0; c < row.Cells.Count; c++)
            {
                Cell cell = row.Cells[c];
                CellFormat cellFormat = cell.GetEffectiveFormat();
                int span = Math.Max(1, cellFormat.GridSpan ?? 1);
                double cellWidth = 0;
                for (int i = column; i < Math.Min(grid.Length, column + span); i++)
                {
                    cellWidth += grid[i];
                }

                bool firstColumn = c == 0;
                bool lastColumn = c == row.Cells.Count - 1;
                CellBorders borders = new(
                    cellFormat.BorderTop ?? (r == 0 ? format.BorderTop : format.BorderInsideHorizontal),
                    cellFormat.BorderLeft ?? (firstColumn ? format.BorderLeft : format.BorderInsideVertical),
                    cellFormat.BorderBottom ?? (r == table.Rows.Count - 1 ? format.BorderBottom : format.BorderInsideHorizontal),
                    cellFormat.BorderRight ?? (lastColumn ? format.BorderRight : format.BorderInsideVertical));
                Shading? shading = cellFormat.Shading ?? format.Shading;
                cells.Add(new CellPlan
                {
                    Cell = cell,
                    Format = cellFormat,
                    Column = column,
                    X = cx,
                    Width = cellWidth,
                    Margins = (cellFormat.MarginTop ?? format.CellMarginTop ?? 0, cellFormat.MarginLeft ?? format.CellMarginLeft ?? 5.4,
                        cellFormat.MarginBottom ?? format.CellMarginBottom ?? 0, cellFormat.MarginRight ?? format.CellMarginRight ?? 5.4),
                    Borders = borders,
                    Fill = shading is { } s && s.Fill.Kind != DocumentColorKind.Auto ? s.Fill.Resolve(document.Theme, default) : null,
                });
                column += span;
                cx += cellWidth;
            }

            rows.Add(new RowPlan { Row = row, Format = row.GetEffectiveFormat(), Cells = cells });
        }

        // Vertical merges: a restarting cell covers the cells below it in the same grid column that continue it.
        for (int r = 0; r < rows.Count; r++)
        {
            foreach (CellPlan cell in rows[r].Cells)
            {
                if (cell.Format.VerticalMerge == VerticalMerge.Continue && r > 0)
                {
                    cell.RowSpan = 0;
                    rows[r].HasMerges = true;
                    continue;
                }

                if (cell.Format.VerticalMerge != VerticalMerge.Restart)
                {
                    continue;
                }

                int span = 1;
                while (r + span < rows.Count && rows[r + span].Cells.Find(c => c.Column == cell.Column) is { Format.VerticalMerge: VerticalMerge.Continue })
                {
                    span++;
                }

                cell.RowSpan = span;
                rows[r].HasMerges |= span > 1;
            }
        }

        // Row heights: each cell's content, merged cells adding what their rows lack to the last row they cover.
        for (int r = 0; r < rows.Count; r++)
        {
            RowPlan row = rows[r];
            double height = 0;
            foreach (CellPlan cell in row.Cells)
            {
                if (cell.RowSpan == 0)
                {
                    continue;
                }

                measuring++;
                (_, double content) = LayStory(cell.Cell, 0, 0, Math.Max(1, cell.Width - cell.Margins.Left - cell.Margins.Right));
                measuring--;
                cell.ContentHeight = content + cell.Margins.Top + cell.Margins.Bottom;
                if (cell.RowSpan == 1)
                {
                    height = Math.Max(height, cell.ContentHeight);
                }
            }

            row.Height = RowHeight(row.Format, height);
        }

        for (int r = 0; r < rows.Count; r++)
        {
            foreach (CellPlan cell in rows[r].Cells.Where(c => c.RowSpan > 1))
            {
                double covered = 0;
                for (int i = r; i < r + cell.RowSpan; i++)
                {
                    covered += rows[i].Height;
                }

                if (cell.ContentHeight > covered)
                {
                    RowPlan last = rows[r + cell.RowSpan - 1];
                    last.Height = RowHeight(last.Format, last.Height + (cell.ContentHeight - covered));
                }
            }
        }

        int headerRows = 0;
        while (headerRows < rows.Count && rows[headerRows].Format.IsHeader == true)
        {
            headerRows++;
        }

        int footerRows = 0;
        while (footerRows < rows.Count - headerRows && rows[rows.Count - 1 - footerRows].Format.IsFooter == true)
        {
            footerRows++;
        }

        return new TablePlan { Table = table, Left = left, Width = width, Rows = rows, HeaderRows = headerRows, FooterRows = footerRows };
    }

    private static double RowHeight(RowFormat format, double content)
    {
        return format.Height is double given
            ? format.HeightRule == RowHeightRule.Exactly ? given : Math.Max(content, given)
            : content;
    }

    /// <summary>Flows a body table down columns and pages, repeating header rows and splitting rows that may split.</summary>
    private void FlowTable(Table table)
    {
        if (Floats(table, out TablePosition position))
        {
            FlowFloatingTable(table, position);
            return;
        }

        TablePlan plan = Plan(table, Column.X, Column.Width);
        int r = 0;
        (int Block, int Line)[]? resume = null;
        bool firstChunk = true;
        while (r < plan.Rows.Count)
        {
            double top = y;
            List<LayoutBox> rows = [];
            double at = top;
            double bottom = ColumnBottom;
            bool startedAtTop = AtColumnTop;

            // Header rows repeat at the top of each later part of the table, while they leave room for the rows they head.
            double headers = plan.Rows.Take(plan.HeaderRows).Sum(h => h.Height);
            if (!firstChunk && plan.HeaderRows > 0 && r >= plan.HeaderRows && top + headers + Math.Min(plan.Rows[r].Height, 24) <= bottom)
            {
                for (int h = 0; h < plan.HeaderRows && at + plan.Rows[h].Height <= bottom; h++)
                {
                    rows.Add(PlaceRow(plan, plan.Rows[h], at, plan.Rows[h].Height, null, out _, repeated: true));
                    at += plan.Rows[h].Height;
                }
            }

            // Footer rows repeat at the bottom of each part the table continues from, so the rows above them leave room.
            int footerStart = plan.Rows.Count - plan.FooterRows;
            double footers = plan.Rows.Skip(footerStart).Sum(f => f.Height);
            double fullBottom = bottom;
            int placedHere = 0;
            while (r < plan.Rows.Count)
            {
                RowPlan row = plan.Rows[r];
                bottom = r < footerStart && plan.FooterRows > 0 ? fullBottom - footers : fullBottom;
                double needed = resume is null ? row.Height : PartHeight(plan, row, resume, double.PositiveInfinity);
                if (at + needed <= bottom + 0.01)
                {
                    rows.Add(PlaceRow(plan, row, at, needed, resume, out _, repeated: false));
                    at += needed;
                    resume = null;
                    r++;
                    placedHere++;
                    continue;
                }

                // The row does not fit: split it when it may and something of it fits; else it starts the next part.
                bool mayBreak = row.Format.CantSplit != true && !row.HasMerges && row.Format.HeightRule != RowHeightRule.Exactly;
                bool emptyHere = placedHere == 0 && startedAtTop;
                if (mayBreak || emptyHere)
                {
                    double room = bottom - at;
                    (int Block, int Line)[] start = resume ?? [.. row.Cells.Select(_ => (0, 0))];
                    RowBox part = PlaceRow(plan, row, at, room, start, out (int, int)[]? next, repeated: false);
                    if (next is not null && part.Bounds.Height > 0.01 && Progressed(start, next))
                    {
                        rows.Add(part);
                        at += part.Bounds.Height;
                        resume = next;
                        placedHere++;
                    }
                    else if (emptyHere)
                    {
                        // Taller than an empty column and cannot split: placed anyway, so layout moves on.
                        rows.Add(PlaceRow(plan, row, at, needed, resume, out _, repeated: false));
                        at += needed;
                        resume = null;
                        r++;
                        placedHere++;
                    }
                }

                break;
            }

            if (placedHere > 0 && r < footerStart && plan.FooterRows > 0)
            {
                for (int f = footerStart; f < plan.Rows.Count; f++)
                {
                    rows.Add(PlaceRow(plan, plan.Rows[f], at, plan.Rows[f].Height, null, out _, repeated: true));
                    at += plan.Rows[f].Height;
                }
            }

            if (rows.Count > 0 && placedHere > 0)
            {
                page!.Add(column, new TableBox(table, new LayoutRect(plan.Left, top, plan.Width, at - top), rows));
                y = at;
            }

            firstChunk = firstChunk && placedHere == 0;
            if (placedHere > 0)
            {
                firstChunk = false;
            }

            if (r < plan.Rows.Count)
            {
                NextColumn();
            }
        }
    }

    private static bool Progressed((int Block, int Line)[] from, (int Block, int Line)[] to)
    {
        for (int i = 0; i < from.Length; i++)
        {
            if (to[i] != from[i])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The height of a row's remaining part from <paramref name="resume"/>, laid out whole.</summary>
    private double PartHeight(TablePlan plan, RowPlan row, (int Block, int Line)[] resume, double limit)
    {
        double height = 0;
        for (int i = 0; i < row.Cells.Count; i++)
        {
            CellPlan cell = row.Cells[i];
            measuring++;
            (_, double content, _) = LayCell(cell, 0, 0, limit, resume[i]);
            measuring--;
            height = Math.Max(height, content + cell.Margins.Top + cell.Margins.Bottom);
        }

        _ = plan;
        return height;
    }

    /// <summary>
    /// Places a row (or its part from <paramref name="resume"/>) at <paramref name="top"/> within <paramref name="height"/>;
    /// <paramref name="next"/> is where each cell goes on when the row did not fit whole, null when it did.
    /// </summary>
    private RowBox PlaceRow(TablePlan plan, RowPlan row, double top, double height, (int Block, int Line)[]? resume, out (int Block, int Line)[]? next, bool repeated)
    {
        List<LayoutBox> cells = [];
        (int, int)[] cursors = new (int, int)[row.Cells.Count];
        bool unfinished = false;
        double used = 0;
        for (int i = 0; i < row.Cells.Count; i++)
        {
            CellPlan cell = row.Cells[i];
            if (cell.RowSpan == 0)
            {
                continue;
            }

            (int, int) start = resume?[i] ?? (0, 0);
            double contentLimit = height - cell.Margins.Top - cell.Margins.Bottom;
            (List<LayoutBox> blocks, double content, (int, int)? stop) = LayCell(cell, cell.X + cell.Margins.Left, top + cell.Margins.Top, resume is null ? double.PositiveInfinity : contentLimit, start);
            cursors[i] = stop ?? (int.MaxValue, 0);
            unfinished |= stop is not null;
            used = Math.Max(used, content + cell.Margins.Top + cell.Margins.Bottom);
            double cellHeight = height;
            if (cell.RowSpan > 1)
            {
                cellHeight = 0;
                int index = plan.Rows.IndexOf(row);
                for (int r = index; r < index + cell.RowSpan; r++)
                {
                    cellHeight += plan.Rows[r].Height;
                }
            }

            // Vertical alignment moves the content down within the cell.
            double free = cellHeight - content - cell.Margins.Top - cell.Margins.Bottom;
            double shift = cell.Format.VerticalAlignment switch
            {
                VerticalAlignment.Center => Math.Max(0, free / 2),
                VerticalAlignment.Bottom => Math.Max(0, free),
                _ => 0,
            };
            if (shift > 0 && resume is null)
            {
                blocks = [.. blocks.Select(b => Shift(b, shift))];
            }

            cells.Add(new CellBox(cell.Cell, new LayoutRect(cell.X, top, cell.Width, cellHeight), blocks, cell.Borders, cell.Fill));
        }

        next = unfinished ? cursors : null;
        double rowHeight = resume is null ? height : Math.Min(height, used);
        if (resume is not null)
        {
            cells = [.. cells.Select(c => (LayoutBox)new CellBox(((CellBox)c).Cell, c.Bounds with { Height = rowHeight }, c.Children, ((CellBox)c).Borders, ((CellBox)c).Fill))];
        }

        return new RowBox(row.Row, new LayoutRect(plan.Left, top, plan.Width, rowHeight), cells, repeated);
    }

    /// <summary>
    /// Lays a cell's blocks out from <paramref name="start"/> (block and line) within <paramref name="limit"/>; the stop is where
    /// it goes on, null when all fitted.
    /// </summary>
    private (List<LayoutBox> Blocks, double Height, (int Block, int Line)? Stop) LayCell(CellPlan cell, double x, double top, double limit, (int Block, int Line) start)
    {
        return LayBlocksFrom(cell.Cell.Blocks, x, top, Math.Max(1, cell.Width - cell.Margins.Left - cell.Margins.Right), limit, start);
    }

    /// <summary>
    /// Lays blocks out from <paramref name="start"/> (block and line) within <paramref name="limit"/>, breaking between lines
    /// of a paragraph or before a block that does not fit; the stop is where they go on, null when all fitted. Something is
    /// placed even when it does not fit, so a part never comes out empty, unless <paramref name="mayPlaceNothing"/> says the
    /// part may. With <paramref name="splitTables"/>, a table breaks between its rows, the stop's line being the row it goes
    /// on from.
    /// </summary>
    private (List<LayoutBox> Blocks, double Height, (int Block, int Line)? Stop) LayBlocksFrom(NodeCollection<Block> blocks, double x, double top, double width, double limit, (int Block, int Line) start, bool mayPlaceNothing = false, bool splitTables = false)
    {
        List<LayoutBox> boxes = [];
        double at = top;
        if (start.Block >= blocks.Count)
        {
            return (boxes, 0, null);
        }

        for (int b = start.Block; b < blocks.Count; b++)
        {
            switch (blocks[b])
            {
                case Paragraph paragraph:
                    {
                        ParagraphContent content = ContentOf(paragraph);
                        ResolvedParagraphFormat format = content.Format;
                        int firstLine = b == start.Block ? start.Line : 0;
                        (double spaceBefore, double spaceAfter) = Spacing(paragraph, format, b > 0 ? blocks[b - 1] as Paragraph : null, b + 1 < blocks.Count ? blocks[b + 1] as Paragraph : null);
                        double before = firstLine == 0 ? spaceBefore : 0;
                        if (firstLine == 0 && content.Floats.Count > 0)
                        {
                            PlaceFloats(content, at + before);
                        }

                        List<PlacedLine> lines = Lines(content, width);
                        List<LineBox> lineBoxes = [];
                        double lineTop = at + before;
                        for (int l = firstLine; l < lines.Count; l++)
                        {
                            PlacedLine line = lines[l];
                            if (lineTop + line.Height - top > limit + 0.01 && (lineBoxes.Count > 0 || boxes.Count > 0 || l > firstLine || mayPlaceNothing))
                            {
                                if (lineBoxes.Count > 0)
                                {
                                    boxes.Add(new ParagraphBox(paragraph, new LayoutRect(x, at, width, lineTop - at), lineBoxes, firstLine > 0, true, InlineObjectBoxes(content, lineBoxes)));
                                }

                                return (boxes, lineTop - top, (b, l));
                            }

                            lineBoxes.Add(new LineBox(paragraph, new LayoutRect(x + line.Indent, lineTop, line.Available, line.Height), lineTop + line.Ascent, line.Line, content));
                            TakeNotes(content, line.Line.Start, line.Line.End);
                            lineTop += line.Height;
                        }

                        boxes.Add(new ParagraphBox(paragraph, new LayoutRect(x, at, width, lineTop - at), lineBoxes, firstLine > 0, false, InlineObjectBoxes(content, lineBoxes)));
                        at = lineTop + spaceAfter;
                        break;
                    }

                case Table nested when splitTables:
                    {
                        // Rows from where the table goes on, as many as fit.
                        TablePlan plan = Plan(nested, x, width);
                        int firstRow = b == start.Block ? start.Line : 0;
                        List<LayoutBox> rows = [];
                        double rowTop = at;
                        for (int r = firstRow; r < plan.Rows.Count; r++)
                        {
                            RowPlan row = plan.Rows[r];
                            if (rowTop + row.Height - top > limit + 0.01 && !row.HasMerges && (rows.Count > 0 || boxes.Count > 0 || mayPlaceNothing))
                            {
                                if (rows.Count > 0)
                                {
                                    boxes.Add(new TableBox(nested, new LayoutRect(plan.Left, at, plan.Width, rowTop - at), rows));
                                }

                                return (boxes, rowTop - top, (b, r));
                            }

                            rows.Add(PlaceRow(plan, row, rowTop, row.Height, null, out _, repeated: false));
                            rowTop += row.Height;
                        }

                        if (rows.Count > 0)
                        {
                            boxes.Add(new TableBox(nested, new LayoutRect(plan.Left, at, plan.Width, rowTop - at), rows));
                        }

                        at = rowTop;
                        break;
                    }

                case Table nested:
                    {
                        TablePlan plan = Plan(nested, x, width);
                        double height = plan.Rows.Sum(r => r.Height);
                        if (at + height - top > limit + 0.01 && (boxes.Count > 0 || mayPlaceNothing))
                        {
                            return (boxes, at - top, (b, 0));
                        }

                        boxes.Add(PlaceWholeTable(plan, at));
                        at += height;
                        break;
                    }

                case FlexContainer or GridContainer or ContentControl { Box: not null }:
                    {
                        // Laid out whole: a cell's part ends before one that does not fit.
                        measuring++;
                        LayWhole(blocks[b], x, at, width, out double height);
                        measuring--;
                        if (at + height - top > limit + 0.01 && (boxes.Count > 0 || mayPlaceNothing))
                        {
                            return (boxes, at - top, (b, 0));
                        }

                        if (LayWhole(blocks[b], x, at, width, out _) is { } whole)
                        {
                            boxes.Add(whole);
                        }

                        at += height;
                        break;
                    }

                case ContentControl control:
                    {
                        // A content control in a cell lays out its blocks in place.
                        foreach (Block inner in control.Blocks)
                        {
                            if (inner is Paragraph p)
                            {
                                ParagraphContent content = ContentOf(p);
                                List<PlacedLine> lines = Lines(content, width);
                                List<LineBox> lineBoxes = [];
                                double lineTop = at + content.Format.SpaceBefore;
                                foreach (PlacedLine line in lines)
                                {
                                    lineBoxes.Add(new LineBox(p, new LayoutRect(x + line.Indent, lineTop, line.Available, line.Height), lineTop + line.Ascent, line.Line, content));
                                    lineTop += line.Height;
                                }

                                boxes.Add(new ParagraphBox(p, new LayoutRect(x, at, width, lineTop - at), lineBoxes, false, false, InlineObjectBoxes(content, lineBoxes)));
                                at = lineTop + content.Format.SpaceAfter;
                            }
                        }

                        break;
                    }
            }
        }

        return (boxes, at - top, null);
    }

    /// <summary>A whole table placed at <paramref name="top"/> without breaking (in a header, footer, text box or cell).</summary>
    private TableBox PlaceWholeTable(TablePlan plan, double top)
    {
        List<LayoutBox> rows = [];
        double at = top;
        foreach (RowPlan row in plan.Rows)
        {
            rows.Add(PlaceRow(plan, row, at, row.Height, null, out _, repeated: false));
            at += row.Height;
        }

        return new TableBox(plan.Table, new LayoutRect(plan.Left, top, plan.Width, at - top), rows);
    }

    private TableBox LayTable(Table table, double x, double top, double width)
    {
        return PlaceWholeTable(Plan(table, x, width), top);
    }

    private static LayoutBox Shift(LayoutBox box, double dy)
    {
        return box switch
        {
            ParagraphBox p => new ParagraphBox(p.Paragraph, p.Bounds with { Y = p.Bounds.Y + dy },
                [.. p.Lines.Select(l => new LineBox((Paragraph)l.Node!, l.Bounds with { Y = l.Bounds.Y + dy }, l.Baseline + dy, l.Line, l.Content))], p.IsContinuation, p.Continues),
            _ => box,
        };
    }

    /// <summary>
    /// The widths of the table's grid columns. A fixed layout or a grid the file gives is used as is, scaled to the table's
    /// preferred width when it has one; otherwise columns are fitted to their content between its narrowest and widest.
    /// </summary>
    private double[] GridWidths(Table table, TableFormat format, double available)
    {
        int columns = Math.Max(1, table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.Cells.Sum(c => Math.Max(1, c.Format.GridSpan ?? 1))));
        double? preferred = format.Width switch
        {
            { Unit: WidthUnit.Points, Value: > 0 } points => points.Value,
            { Unit: WidthUnit.Percent, Value: > 0 } percent => available * percent.Value / 100,
            _ => null,
        };
        if (table.Grid is { Count: > 0 } grid && grid.Sum() > 0)
        {
            double[] widths = [.. grid];
            if (widths.Length < columns)
            {
                widths = [.. widths, .. Enumerable.Repeat(widths.Average(), columns - widths.Length)];
            }

            if (preferred is double target && format.Width?.Unit == WidthUnit.Percent && Math.Abs(widths.Sum() - target) > 0.5)
            {
                double scale = target / widths.Sum();
                widths = [.. widths.Select(w => w * scale)];
            }

            return widths;
        }

        // A table without a grid spans the text width as Word's own new tables do; its columns share it by their content.
        return AutoFit(table, columns, preferred ?? available, fill: true);
    }

    /// <summary>
    /// Column widths fitted to content, as browsers and Word's autofit do: each column between its narrowest content (the
    /// longest word) and its widest (no wrapping), the space shared out in proportion.
    /// </summary>
    private double[] AutoFit(Table table, int columns, double width, bool fill)
    {
        double[] min = new double[columns];
        double[] max = new double[columns];
        foreach (Row row in table.Rows)
        {
            int column = 0;
            foreach (Cell cell in row.Cells)
            {
                CellFormat cellFormat = cell.GetEffectiveFormat();
                int span = Math.Max(1, cellFormat.GridSpan ?? 1);
                double margins = (cellFormat.MarginLeft ?? 5.4) + (cellFormat.MarginRight ?? 5.4);
                (double cellMin, double cellMax) = Intrinsic(cell);
                if (cellFormat.Width is { Unit: WidthUnit.Points, Value: > 0 } given)
                {
                    cellMax = Math.Max(cellMin, given.Value - margins);
                }

                if (span == 1 && column < columns)
                {
                    min[column] = Math.Max(min[column], cellMin + margins);
                    max[column] = Math.Max(max[column], cellMax + margins);
                }

                column += span;
            }
        }

        double minSum = min.Sum();
        double maxSum = max.Sum();
        double[] widths = new double[columns];
        if (maxSum <= width && !fill)
        {
            return max;
        }

        if (maxSum <= width)
        {
            double extra = width - maxSum;
            for (int i = 0; i < columns; i++)
            {
                widths[i] = max[i] + (maxSum > 0 ? extra * max[i] / maxSum : extra / columns);
            }

            return widths;
        }

        if (minSum >= width)
        {
            return min;
        }

        // Between the two: each column gets its minimum and a share of the rest in proportion to how much more it wants.
        double room = width - minSum;
        double wants = maxSum - minSum;
        for (int i = 0; i < columns; i++)
        {
            widths[i] = min[i] + (wants > 0 ? room * (max[i] - min[i]) / wants : room / columns);
        }

        return widths;
    }

    /// <summary>A cell's narrowest and widest content widths.</summary>
    private (double Min, double Max) Intrinsic(Cell cell)
    {
        double min = 0;
        double max = 0;
        foreach (Paragraph paragraph in cell.Blocks.OfType<Paragraph>())
        {
            ParagraphContent content = ContentOf(paragraph);
            (double contentMin, double contentMax) = content.Intrinsic();
            double indents = content.Format.LeftIndent + content.Format.RightIndent + Math.Max(0, content.Format.FirstLineIndent);
            min = Math.Max(min, contentMin + indents);
            max = Math.Max(max, contentMax + indents);
        }

        foreach (Table nested in cell.Blocks.OfType<Table>())
        {
            min = Math.Max(min, 36);
            max = Math.Max(max, nested.Grid?.Sum() ?? 144);
        }

        return (min, max);
    }
}

/// <summary>A cell's four borders as the layout resolved them; null where there is none.</summary>
/// <param name="Top">The top border.</param>
/// <param name="Left">The left border.</param>
/// <param name="Bottom">The bottom border.</param>
/// <param name="Right">The right border.</param>
public sealed record CellBorders(Border? Top, Border? Left, Border? Bottom, Border? Right);
