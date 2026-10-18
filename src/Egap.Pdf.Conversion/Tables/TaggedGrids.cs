namespace Egap.Pdf.Conversion;

/// <summary>
/// Tagged tables placed on a grid (3.7 design point 1, Q3). Tags say which cells a table has and in what order, but
/// writers such as Word give no ColSpan or RowSpan, so a merged cell reads as one cell of a short row. Where a ruled grid
/// lies under the table, each tagged cell takes the grid cell its text sits in and its span; an empty cell takes the next
/// free grid cell of its row. Without rules, columns come from the rows that have every cell, and a cell of a short row
/// spans the columns its text crosses. When tags and grid cannot be matched the table keeps the tags' cells, spans aside.
/// </summary>
internal static class TaggedGrids
{
    /// <summary>The share of a tagged table's text that must lie inside a ruled grid for the grid to be its own.</summary>
    private const double Inside = 0.9;

    /// <summary>
    /// Replaces each tagged table of <paramref name="draft"/> by one placed on its grid, where one is found. A tagged table
    /// with no text takes the next empty ruled grid down the page with as many rows; without one it stays as tagged.
    /// </summary>
    public static void Place(PageDraft draft, PageContent content)
    {
        List<RuledTables.Rule>? rules = null;
        List<TableDraft>? empty = null;
        for (int i = 0; i < draft.Tables.Count; i++)
        {
            TableDraft table = draft.Tables[i];
            if (table.Tag is null)
            {
                continue;
            }

            rules ??= RuledTables.Merge(RuledTables.Rules(content.Paths));
            if (!Glyphs(table).Any(g => !g.IsSpace))
            {
                empty ??= EmptyGrids(rules, content);
                TableDraft? grid = empty.FirstOrDefault(g => g.Rows == table.Rows);
                if (grid is null)
                {
                    continue;
                }

                empty.Remove(grid);
                grid.Order = table.Order;
                grid.Tag = table.Tag;
                grid.RowTags = table.RowTags;
                grid.Pieces = table.Pieces;
                grid.Confidence = table.Confidence;
                grid.IsRuled = true;
                draft.Tables[i] = grid;
                continue;
            }

            PlaceNested(table, rules);
            if ((OnRules(table, rules) ?? OnText(table)) is TableDraft placed)
            {
                placed.Order = table.Order;
                placed.Tag = table.Tag;
                placed.RowTags = placed.Rows == table.Rows ? table.RowTags : null;
                placed.Pieces = placed.Rows == table.Rows ? table.Pieces : null;
                placed.Confidence = table.Confidence;
                draft.Tables[i] = placed;
            }
        }
    }

    /// <summary>How close, in points, two cell edges must be to be one grid line: within a cell's padding.</summary>
    private const double Snap = 8;

    /// <summary>
    /// The table on the ruled grid that holds its text, or null when no grid does or the cells do not fit it. Each tagged
    /// cell takes the grid cell most of its text is in; an empty one the next free grid cell level with its row. The rows
    /// are the tags' rows; the columns are the cells' left edges, snapped together across a cell's padding, so the slivers
    /// that shading inside a cell adds to the grid make no columns of their own.
    /// </summary>
    internal static TableDraft? OnRules(TableDraft table, List<RuledTables.Rule> rules)
    {
        List<PageGlyph> glyphs = [.. Glyphs(table)];
        List<PageGlyph> ink = [.. glyphs.Where(g => !g.IsSpace)];
        if (ink.Count == 0 || rules.Count < 4)
        {
            return null;
        }

        // The smallest grid that holds the text: a table's own, not the grid of a table it is nested in.
        TableDraft? grid = RuledTables.Candidates(rules, glyphs)
            .Where(c => ink.Count(g => c.Box.Contains(g.Box.CenterX, g.Box.CenterY)) >= ink.Count * Inside)
            .MinBy(c => c.Box.Area);
        if (grid is null)
        {
            return null;
        }

        // The grid cell each glyph is in.
        Dictionary<PageGlyph, CellDraft> home = new(ReferenceEqualityComparer.Instance);
        foreach (CellDraft cell in grid.Cells)
        {
            foreach (PageGlyph glyph in cell.Lines.SelectMany(l => l.Words).SelectMany(w => w.Glyphs))
            {
                home[glyph] = cell;
            }
        }

        // Each tagged cell with text on the grid cell that holds most of it; no grid cell holds two.
        HashSet<CellDraft> taken = new(ReferenceEqualityComparer.Instance);
        List<List<(CellDraft Tagged, PageBox? Box)>> rows = [];
        foreach (IGrouping<int, CellDraft> row in table.Cells.GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            List<(CellDraft, PageBox?)> placed = [];
            foreach (CellDraft cell in row.OrderBy(c => c.Column))
            {
                CellDraft? target = cell.Lines.SelectMany(l => l.Words).SelectMany(w => w.Glyphs).Where(g => !g.IsSpace)
                    .Select(g => home.GetValueOrDefault(g)).OfType<CellDraft>().GroupBy(c => c, ReferenceEqualityComparer.Instance)
                    .MaxBy(g => g.Count())?.Key as CellDraft;
                if (target is not null && !taken.Add(target))
                {
                    return null;
                }

                placed.Add((cell, target?.Box));
            }

            rows.Add(placed);
        }

        // A row's top is its highest cell with text; rows run down the page.
        double[] tops = new double[rows.Count];
        for (int r = 0; r < rows.Count; r++)
        {
            List<PageBox> boxes = [.. rows[r].Select(p => p.Box).OfType<PageBox>()];
            tops[r] = boxes.Count > 0 ? boxes.Min(b => b.Top) : double.NaN;
        }

        // Empty tagged cells take the free grid cells level with their row, left to right between the cells around them;
        // a row without text sits on the first free grid cells below the row before.
        List<CellDraft> free = [.. grid.Cells.Where(c => !taken.Contains(c) && c.Lines.Count == 0)];
        for (int r = 0; r < rows.Count; r++)
        {
            if (!double.IsFinite(tops[r]))
            {
                double below = r > 0 ? tops[r - 1] + Snap : grid.Box.Top - Snap;
                tops[r] = free.Where(c => c.Box.Top > below).Select(c => c.Box.Top).DefaultIfEmpty(double.NaN).Min();
                if (!double.IsFinite(tops[r]))
                {
                    return null;
                }
            }

            if (r > 0 && tops[r] < tops[r - 1] - Snap)
            {
                return null;
            }

            List<(CellDraft Tagged, PageBox? Box)> row = rows[r];
            for (int i = 0; i < row.Count; i++)
            {
                if (row[i].Box is not null)
                {
                    continue;
                }

                double left = row.Take(i).Select(p => p.Box).OfType<PageBox>().Select(b => b.Right).DefaultIfEmpty(double.NegativeInfinity).Max();
                double right = row.Skip(i + 1).Select(p => p.Box).OfType<PageBox>().Select(b => b.Left).DefaultIfEmpty(double.PositiveInfinity).Min();
                double top = tops[r];
                CellDraft? next = free.Where(c => Math.Abs(c.Box.Top - top) <= Snap && c.Box.Left >= left - Snap && c.Box.Right <= right + Snap)
                    .MinBy(c => c.Box.Left);
                if (next is not null)
                {
                    free.Remove(next);
                    row[i] = (row[i].Tagged, next.Box);
                }
            }
        }

        // Columns where cells start, snapped across padding.
        List<double> edges = [];
        foreach (double left in rows.SelectMany(r => r).Select(p => p.Box).OfType<PageBox>().Select(b => b.Left).Order())
        {
            if (edges.Count == 0 || left - edges[^1] > Snap)
            {
                edges.Add(left);
            }
        }

        int columns = edges.Count;
        List<CellDraft> cells = [];
        for (int r = 0; r < rows.Count; r++)
        {
            foreach ((CellDraft tagged, PageBox? place) in rows[r])
            {
                // An empty tagged cell left with no grid cell stands where a merged cell already covers.
                if (place is not PageBox box)
                {
                    continue;
                }

                int column = Math.Max(0, edges.Count(e => e <= box.Left + Snap) - 1);
                int span = Math.Min(columns - column, Math.Max(1, edges.Count(e => e > box.Left + Snap && e < box.Right - Snap) + 1));
                int rowSpan = 1;
                while (r + rowSpan < rows.Count && tops[r + rowSpan] < box.Bottom - Snap)
                {
                    rowSpan++;
                }

                cells.Add(new CellDraft(r, column, rowSpan, span, tagged.Header, tagged.Lines) { Box = box, Tables = tagged.Tables });
            }
        }

        return cells.Count == 0 ? null : new TableDraft(cells, rows.Count, columns) { Box = grid.Box, IsRuled = true };
    }

    /// <summary>
    /// The table with spans from its text when some rows have fewer cells than others: columns from the rows that have them
    /// all, each cell of a shorter row spanning the columns its text crosses, its empty cells filling the columns between.
    /// With <paramref name="everyCell"/> off (empty cells missing, not tagged), a short row's gaps stay empty cells instead.
    /// Null when every row is full or the cells do not fit the columns.
    /// </summary>
    internal static TableDraft? OnText(TableDraft table, bool everyCell = true)
    {
        List<List<CellDraft>> rows = [.. table.Cells.GroupBy(c => c.Row).OrderBy(g => g.Key).Select(g => g.OrderBy(c => c.Column).ToList())];
        int columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (columns < 2 || rows.All(r => r.Count == columns) || table.Cells.Any(c => c.ColumnSpan > 1 || c.RowSpan > 1))
        {
            return null;
        }

        // Each column's extent over the full rows' cells with text, and the edges halfway between neighbours.
        List<List<CellDraft>> full = [.. rows.Where(r => r.Count == columns)];
        double[] lefts = new double[columns], rights = new double[columns];
        for (int c = 0; c < columns; c++)
        {
            List<PageBox> boxes = [.. full.Select(r => r[c]).Where(x => x.Lines.Count > 0).Select(x => x.Box)];
            if (boxes.Count == 0)
            {
                return null;
            }

            lefts[c] = boxes.Min(b => b.Left);
            rights[c] = boxes.Max(b => b.Right);
        }

        double[] edges = new double[columns - 1];
        for (int c = 0; c < columns - 1; c++)
        {
            if (rights[c] > lefts[c + 1])
            {
                return null;
            }

            edges[c] = (rights[c] + lefts[c + 1]) / 2;
        }

        List<CellDraft> cells = [];
        foreach (List<CellDraft> row in rows)
        {
            if (row.Count == columns)
            {
                cells.AddRange(row.Select((x, c) => new CellDraft(x.Row, c, 1, 1, x.Header, x.Lines) { Box = x.Box, Tables = x.Tables }));
                continue;
            }

            // Where each cell with text starts and ends, by the columns its text crosses.
            int[] starts = new int[row.Count], ends = new int[row.Count];
            for (int i = 0; i < row.Count; i++)
            {
                starts[i] = ends[i] = -1;
                if (row[i].Lines.Count > 0)
                {
                    starts[i] = edges.Count(e => e < row[i].Box.Left);
                    ends[i] = edges.Count(e => e < row[i].Box.Right);
                }
            }

            // A row of one cell whose text crosses a column edge is a title across the table.
            if (row.Count == 1 && starts[0] >= 0 && ends[0] > starts[0])
            {
                cells.Add(new CellDraft(row[0].Row, 0, 1, columns, row[0].Header, row[0].Lines) { Box = row[0].Box, Tables = row[0].Tables });
                continue;
            }

            // A short row's cells in order: each cell with text from its own column (or right after the cell before),
            // each empty cell the column after the cell before; a cell's span runs to the column before the next cell.
            int[] at = new int[row.Count];
            int next = 0;
            for (int i = 0; i < row.Count; i++)
            {
                // With every cell tagged, a row's first cell starts at the first column; without, cells stand where their text is.
                int wanted = i == 0 && everyCell ? 0 : starts[i] >= 0 ? Math.Max(starts[i], next) : next;

                // Leave room for the cells still to come.
                wanted = Math.Min(wanted, columns - (row.Count - i));
                if (wanted < next)
                {
                    return null;
                }

                at[i] = wanted;
                next = Math.Max(wanted + 1, starts[i] >= 0 && i == row.Count - 1 ? ends[i] + 1 : wanted + 1);
            }

            for (int i = 0; i < row.Count; i++)
            {
                // Every cell tagged: a cell runs to the next one. Empty cells untagged: a cell spans only the columns its text crosses.
                int end = i + 1 < row.Count ? at[i + 1] : columns;
                if (!everyCell && starts[i] >= 0)
                {
                    end = Math.Min(end, Math.Max(at[i] + 1, ends[i] + 1));
                }

                cells.Add(new CellDraft(row[i].Row, at[i], 1, end - at[i], row[i].Header, row[i].Lines) { Box = row[i].Box, Tables = row[i].Tables });
            }
        }

        return new TableDraft(cells, table.Rows, columns) { Box = table.Box, IsRuled = table.IsRuled };
    }

    /// <summary>Places the tables nested in <paramref name="table"/>'s cells on their own grids, depth first.</summary>
    private static void PlaceNested(TableDraft table, List<RuledTables.Rule> rules)
    {
        foreach (CellDraft cell in table.Cells)
        {
            for (int i = 0; i < cell.Tables.Count; i++)
            {
                TableDraft inner = cell.Tables[i];
                PlaceNested(inner, rules);
                if ((OnRules(inner, rules) ?? OnText(inner)) is TableDraft placed)
                {
                    placed.Order = inner.Order;
                    placed.Tag = inner.Tag;
                    placed.Confidence = inner.Confidence;
                    cell.Tables[i] = placed;
                }
            }
        }
    }

    /// <summary>The page's ruled grids with no text inside, top to bottom.</summary>
    private static List<TableDraft> EmptyGrids(List<RuledTables.Rule> rules, PageContent content)
    {
        List<PageGlyph> ink = [.. content.Glyphs.Where(g => !g.IsSpace)];
        return [.. RuledTables.Candidates(rules, [], empty: true)
            .Where(g => !ink.Any(x => g.Box.Contains(x.Box.CenterX, x.Box.CenterY)))
            .OrderBy(g => g.Box.Top)];
    }

    private static IEnumerable<PageGlyph> Glyphs(TableDraft table)
    {
        return table.Cells.SelectMany(c => c.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs);
    }
}
