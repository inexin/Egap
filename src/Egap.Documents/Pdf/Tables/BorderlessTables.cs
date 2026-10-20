using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// Borderless tables from text alignment (design Q5, Build Plan task 8), after Nurminen's approach that Tabula and Camelot's
/// Stream follow. Lines are cut into pieces at gaps much wider than a word gap; a run of three or more lines that each hold
/// two pieces or more, stepping down at about one line pitch, is a table candidate; its columns are the stretches of the
/// page its pieces cover, with gutters of whitespace between them that run through every row. The candidate is kept only
/// when it scores clearly as a table and not as text set in columns (short cells, or cells that are mostly numbers), so a
/// list of short lines or a two-column page is never taken for one. Rows above it that line up with its columns are its
/// header; a row whose first column is empty carries the cell above it on. A partly ruled table's rules (a hint from task 7)
/// or an explicit area from the caller bound the table instead of the search.
/// </summary>
internal static class BorderlessTables
{
    /// <summary>The widest a column gap may need to be, in ems, inside an area known to be a table.</summary>
    private const double BoundedGutter = 0.9;

    /// <summary>A line piece with its row (line index) in the region.</summary>
    private sealed record Piece(PageLine Line, int Row);

    /// <summary>
    /// Tables found among <paramref name="glyphs"/>, bounded by <paramref name="hints"/> and <paramref name="areas"/> where
    /// given; with <paramref name="search"/> off, only the areas and hints are tables.
    /// </summary>
    public static List<TableDraft> Find(IReadOnlyList<PageGlyph> glyphs, IReadOnlyList<TableHint> hints, IReadOnlyList<PageBox> areas, LayoutThresholds thresholds,
        bool search = true)
    {
        List<TableDraft> tables = [];
        HashSet<PageGlyph> used = new(ReferenceEqualityComparer.Instance);

        // The page's word gap, from all its text: inside a table most gaps are column gaps.
        List<PageLine> lines = PageReader.Lines(glyphs);
        double wordGap = BlockBuilder.WordGap(lines);

        // Areas the caller names, and areas a partly ruled table's rules bound, are tables whatever they score: they need only
        // two columns (and the rules' area two rows). Inside them a column gap can be as narrow as a word processor's cell
        // padding, under an em, so they cut lines at narrower gaps than the page's gutters.
        LayoutThresholds bounded = thresholds with { GutterSize = Math.Min(thresholds.GutterSize, BoundedGutter) };
        foreach (PageBox area in areas)
        {
            if (Build(Inside(glyphs, area, used), wordGap, bounded, minimumRows: 1) is TableDraft table)
            {
                table.Box = area;
                Claim(table, used);
                tables.Add(table);
            }
        }

        // The smallest areas first: a band of rules round the whole page holds the tables inside it, not the other way round.
        foreach (TableHint hint in hints.OrderBy(h => h.Box.Area))
        {
            PageBox area = hint.Box with { Top = hint.Box.Top - 1, Bottom = hint.Box.Bottom + 1 };
            if (Build(Inside(glyphs, area, used), wordGap, bounded, minimumRows: 2) is TableDraft table)
            {
                table.Box = area;
                table.Confidence = 0.8;
                MarkHeader(table, hint);
                Claim(table, used);
                tables.Add(table);
            }
        }

        foreach (List<PageLine> run in search ? Runs(used.Count == 0 ? lines : PageReader.Lines(glyphs.Where(g => !used.Contains(g))), wordGap, thresholds) : [])
        {
            if (Prose(run, wordGap, thresholds))
            {
                continue;
            }

            if (Build([.. run.SelectMany(l => l.Words).SelectMany(w => w.Glyphs)], wordGap, thresholds, minimumRows: 3) is TableDraft table && Scores(table))
            {
                table.Confidence = 0.6;
                MarkBoldHeader(table);
                Claim(table, used);
                tables.Add(table);
            }
        }

        return tables;
    }

    /// <summary>
    /// Runs of lines that could be a table: consecutive lines, each cut into two pieces or more, about a line pitch apart and
    /// overlapping side to side, extended up by the lines just above that line up with the run's gutters (its header).
    /// </summary>
    private static List<List<PageLine>> Runs(List<PageLine> found, double wordGap, LayoutThresholds thresholds)
    {
        List<PageLine> lines = [.. found.Where(l => l.IsHorizontal).OrderBy(l => l.Baseline).ThenBy(l => l.Box.Left)];

        // Lines on one baseline (two columns of a table written apart) are one row.
        List<PageLine> rows = [];
        foreach (PageLine line in lines)
        {
            if (rows.Count > 0 && Math.Abs(rows[^1].Baseline - line.Baseline) < line.Size * 0.3)
            {
                rows[^1] = new PageLine([.. rows[^1].Words.Concat(line.Words).OrderBy(w => w.Box.Left)]);
            }
            else
            {
                rows.Add(line);
            }
        }

        List<List<PageLine>> runs = [];
        List<PageLine> run = [];
        foreach (PageLine row in rows)
        {
            bool split = BlockBuilder.Cut(row, wordGap, thresholds).Count() >= 2;
            bool near = run.Count > 0 && row.Baseline - run[^1].Baseline <= row.Size * 2.6 && Math.Min(row.Box.Right, run[^1].Box.Right) > Math.Max(row.Box.Left, run[^1].Box.Left);
            if (split && (run.Count == 0 || near))
            {
                run.Add(row);
                continue;
            }

            if (run.Count >= 3)
            {
                runs.Add(run);
            }

            run = split ? [row] : [];
        }

        if (run.Count >= 3)
        {
            runs.Add(run);
        }

        return runs;
    }

    /// <summary>
    /// The table the glyphs make: columns from the gutters that run through all its rows, a cell for each stretch of a row
    /// within a column (spanning columns when a piece crosses a gutter), rows whose first column is empty joined to the row
    /// above. Null when there are fewer than two columns or <paramref name="minimumRows"/> rows.
    /// </summary>
    internal static TableDraft? Build(List<PageGlyph> glyphs, double wordGap, LayoutThresholds thresholds, int minimumRows)
    {
        List<PageLine> lines = [.. PageReader.Lines(glyphs.Where(g => !g.IsSpace)).Where(l => l.IsHorizontal)];
        if (lines.Count == 0)
        {
            return null;
        }
        List<List<PageLine>> rows = [];
        foreach (PageLine line in lines.OrderBy(l => l.Baseline))
        {
            if (rows.Count > 0 && Math.Abs(rows[^1][0].Baseline - line.Baseline) < line.Size * 0.3)
            {
                rows[^1].Add(line);
            }
            else
            {
                rows.Add([line]);
            }
        }

        List<List<PageLine>> pieces = [.. rows.Select(r => r.SelectMany(l => BlockBuilder.Cut(l, wordGap, thresholds)).OrderBy(p => p.Box.Left).ToList())];
        // Gutters come from the rows cut in two pieces or more: a title or a wrapped header word on a line of its own, set
        // across the columns, would close them.
        List<List<PageLine>> cut = [.. pieces.Where(p => p.Count >= 2)];
        List<(double Left, double Right)> columns = Columns((cut.Count >= minimumRows ? cut : pieces).SelectMany(p => p).Select(p => p.Box), lines.Max(l => l.Size));
        if (columns.Count < 2 || rows.Count < minimumRows)
        {
            return null;
        }

        // Each row's pieces placed in the columns they cover; a row with an empty first column carries the row above on.
        List<List<(int First, int Last, List<PageLine> Lines)>> grid = [];
        foreach (List<PageLine> row in pieces)
        {
            List<(int First, int Last, List<PageLine> Lines)> cells = [];
            foreach (PageLine piece in row)
            {
                int first = columns.FindIndex(c => c.Right >= piece.Box.Left - 1), last = columns.FindLastIndex(c => c.Left <= piece.Box.Right + 1);
                if (first < 0 || last < first)
                {
                    // A piece between columns goes to the nearest one, so no text is lost.
                    first = last = columns.Select((c, i) => (Distance: Math.Min(Math.Abs(c.Left - piece.Box.CenterX), Math.Abs(c.Right - piece.Box.CenterX)), i)).MinBy(c => c.Distance).i;
                }

                if (cells.Count > 0 && cells[^1].Last >= first)
                {
                    cells[^1] = (cells[^1].First, Math.Max(last, cells[^1].Last), [.. cells[^1].Lines, piece]);
                }
                else
                {
                    cells.Add((first, last, [piece]));
                }
            }

            bool carries = grid.Count > 0 && cells.Count > 0 && cells[0].First > 0 && grid[^1].Count > 0 && grid[^1][0].First == 0
                && cells.All(c => grid[^1].Any(p => p.First <= c.First && p.Last >= c.Last));
            if (carries)
            {
                foreach ((int first, int last, List<PageLine> add) in cells)
                {
                    int at = grid[^1].FindIndex(p => p.First <= first && p.Last >= last);
                    grid[^1][at] = (grid[^1][at].First, grid[^1][at].Last, [.. grid[^1][at].Lines, .. add]);
                }
            }
            else if (cells.Count > 0)
            {
                grid.Add(cells);
            }
        }

        if (grid.Count < minimumRows)
        {
            return null;
        }

        List<CellDraft> drafts = [];
        for (int r = 0; r < grid.Count; r++)
        {
            foreach ((int first, int last, List<PageLine> cellLines) in grid[r])
            {
                List<PageLine> joined = PageReader.Lines(cellLines.SelectMany(l => l.Words).SelectMany(w => w.Glyphs).OrderBy(g => g.Box.Top).ThenBy(g => g.Box.Left));
                drafts.Add(new CellDraft(r, first, 1, last - first + 1, false, joined) { Box = PageBox.Around(cellLines.Select(l => l.Box)) });
            }
        }

        return new TableDraft(drafts, grid.Count, columns.Count) { Box = PageBox.Around(drafts.Select(c => c.Box)) };
    }

    /// <summary>
    /// The columns of a set of pieces: the stretches of x their boxes cover, merged where they overlap or almost touch;
    /// a gutter is a gap of at least one text size that no piece crosses.
    /// </summary>
    private static List<(double Left, double Right)> Columns(IEnumerable<PageBox> boxes, double size)
    {
        List<(double Left, double Right)> columns = [];
        foreach (PageBox box in boxes.OrderBy(b => b.Left))
        {
            if (columns.Count > 0 && box.Left < columns[^1].Right + size)
            {
                columns[^1] = (columns[^1].Left, Math.Max(columns[^1].Right, box.Right));
            }
            else
            {
                columns.Add((box.Left, box.Right));
            }
        }

        return columns;
    }

    /// <summary>
    /// Whether a candidate is clearly a table and not text set in columns: two thirds of its rows fill two columns or more,
    /// and its cells are short (twenty characters or fewer on average, where a line of prose in a column runs longer) or
    /// mostly numbers.
    /// </summary>
    /// <summary>
    /// Whether a run is plainly prose in columns, its pieces long and mostly words, so no table built from it could score
    /// (<see cref="Scores"/> needs short cells or numbers): checked before building, which costs far more.
    /// </summary>
    private static bool Prose(List<PageLine> run, double wordGap, LayoutThresholds thresholds)
    {
        int pieces = 0, characters = 0, numeric = 0;
        foreach (PageLine row in run)
        {
            foreach (PageLine piece in BlockBuilder.Cut(row, wordGap, thresholds))
            {
                string text = piece.Text;
                pieces++;
                characters += text.Length;
                numeric += text.Count(char.IsDigit) * 2 >= text.Count(char.IsLetterOrDigit) ? 1 : 0;
            }
        }

        return pieces > 0 && characters > pieces * 40 && numeric * 3 < pieces;
    }

    private static bool Scores(TableDraft table)
    {
        int full = table.Cells.GroupBy(c => c.Row).Count(r => r.Count() >= 2);
        List<string> texts = [.. table.Cells.Select(c => c.Text).Where(t => t.Length > 0)];
        double characters = texts.Average(t => t.Length);
        double numeric = texts.Count(t => t.Count(char.IsDigit) * 2 >= t.Count(char.IsLetterOrDigit)) / (double)texts.Count;
        return full * 3 >= table.Rows * 2 && (characters <= 20 || numeric >= 0.5);
    }

    /// <summary>A hinted table's header: the rows above its second rule (the line under the header) when there are three rules or more.</summary>
    private static void MarkHeader(TableDraft table, TableHint hint)
    {
        if (hint.Rules.Count < 3)
        {
            return;
        }

        double under = hint.Rules[1];
        List<CellDraft> header = [.. table.Cells.Where(c => c.Box.Bottom <= under + 1)];
        foreach (CellDraft cell in header)
        {
            int at = table.Cells.IndexOf(cell);
            table.Cells[at] = new CellDraft(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan, true, cell.Lines) { Box = cell.Box };
        }
    }

    /// <summary>The first row is a header when its text is bold and the rest of the table's is not.</summary>
    internal static void MarkBoldHeader(TableDraft table)
    {
        List<PageGlyph> first = [.. table.Cells.Where(c => c.Row == 0).SelectMany(c => c.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs)];
        bool bodyPlain = table.Cells.Where(c => c.Row > 0).SelectMany(c => c.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs).Any(g => !FontTraits.IsBold(g));
        if (table.Rows < 2 || first.Count == 0 || !first.All(FontTraits.IsBold) || !bodyPlain)
        {
            return;
        }

        for (int i = 0; i < table.Cells.Count; i++)
        {
            CellDraft cell = table.Cells[i];
            if (cell.Row == 0)
            {
                table.Cells[i] = new CellDraft(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan, true, cell.Lines) { Box = cell.Box };
            }
        }
    }

    private static List<PageGlyph> Inside(IReadOnlyList<PageGlyph> glyphs, PageBox area, HashSet<PageGlyph> used)
    {
        return [.. glyphs.Where(g => !used.Contains(g) && area.Contains(g.Box.CenterX, g.Box.CenterY))];
    }

    private static void Claim(TableDraft table, HashSet<PageGlyph> used)
    {
        foreach (PageGlyph glyph in table.Cells.SelectMany(c => c.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs))
        {
            used.Add(glyph);
        }
    }
}
