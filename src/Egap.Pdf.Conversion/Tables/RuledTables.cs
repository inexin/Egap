namespace Egap.Pdf.Conversion;

/// <summary>
/// A set of horizontal rules that could be a partly ruled table (rules above, under the header and below, no verticals),
/// passed to the borderless detector as a hint (Build Plan task 7): the box the rules span and the height of each rule.
/// </summary>
internal sealed record TableHint(PageBox Box, IReadOnlyList<double> Rules);

/// <summary>
/// Ruled tables from vector paths (design Q5, Build Plan task 7). Horizontal and vertical strokes, the edges of stroked
/// rectangles and thin filled bars become rule segments; collinear pieces are merged; rules that touch form a grid whose
/// distinct heights and offsets are its rows and columns; a cell joins its neighbour where the rule between them is
/// missing, which gives spans; and the text inside goes to the cells. A grid of one cell is a framed box, not a table.
/// Stacks of horizontal rules without verticals are returned as hints for the borderless detector.
/// </summary>
internal static class RuledTables
{
    /// <summary>
    /// The widest word gap, in ems, the check between rules assumes: a page of tables (columns of figures) has a median gap
    /// of several ems, which would make every table row read as one piece of prose.
    /// </summary>
    private const double MaxWordGap = 0.5;

    /// <summary>How close, in points, two rules must be to meet or to be one line.</summary>
    private const double Tolerance = 1.5;

    /// <summary>A rule: horizontal (a constant y from <see cref="From"/> to <see cref="To"/> in x) or vertical.</summary>
    internal sealed record Rule(bool Horizontal, double At, double From, double To);

    /// <summary>The ruled tables among <paramref name="glyphs"/> and the page's paths, with hints for partly ruled ones.</summary>
    public static (List<TableDraft> Tables, List<TableHint> Hints) Find(IReadOnlyList<PagePath> paths, IReadOnlyList<PageGlyph> glyphs, IReadOnlyList<PageImage>? images = null)
    {
        List<Rule> rules = Merge(Rules(paths));
        List<TableDraft> tables = Nested([.. Candidates(rules, glyphs).Where(t => Rejection(t, paths, glyphs, images) is null)]);
        List<TableHint> hints = [];

        // Horizontal rules no grid used, stacked over the same span with text between them: a partly ruled table's rules.
        HashSet<Rule> used = [.. tables.SelectMany(t => rules.Where(r => Inside(r, t.Box)))];
        double? wordGap = null;
        foreach (IGrouping<(int, int), Rule> stack in rules.Where(r => r.Horizontal && !used.Contains(r) && r.To - r.From > 40)
            .GroupBy(r => ((int)Math.Round(r.From / 4), (int)Math.Round(r.To / 4))))
        {
            // A stack splits where the text between two rules is not a table's: a caption, or body text set right across.
            List<Rule> rows = [];
            foreach (Rule rule in stack.OrderBy(r => r.At))
            {
                if (rows.Count > 0 && Between(rows[^1], rule, glyphs, wordGap ??= Math.Min(BlockBuilder.WordGap(PageReader.Lines(glyphs)), MaxWordGap)))
                {
                    Hint(rows, glyphs, hints);
                    rows = [];
                }

                rows.Add(rule);
            }

            Hint(rows, glyphs, hints);
        }

        return (tables, hints);
    }

    /// <summary>
    /// Grids inside grids (a table in a frame, a small table in a big one's cell): each glyph belongs to the smallest grid
    /// around it, and a bigger grid left with fewer than two cells of text is dropped.
    /// </summary>
    private static List<TableDraft> Nested(List<TableDraft> tables)
    {
        HashSet<PageGlyph> claimed = new(ReferenceEqualityComparer.Instance);
        List<TableDraft> kept = [];
        foreach (TableDraft table in tables.OrderBy(t => t.Box.Area))
        {
            if (claimed.Count > 0 && table.Cells.SelectMany(c => c.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs).Any(claimed.Contains))
            {
                for (int i = 0; i < table.Cells.Count; i++)
                {
                    CellDraft cell = table.Cells[i];
                    List<PageGlyph> own = [.. cell.Lines.SelectMany(l => l.Words).SelectMany(w => w.Glyphs).Where(g => !claimed.Contains(g))];
                    table.Cells[i] = new CellDraft(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan, cell.Header, PageReader.Lines(own)) { Box = cell.Box };
                }

                if (table.Cells.Count(c => c.Lines.Count > 0) < 2)
                {
                    continue;
                }
            }

            foreach (PageGlyph glyph in table.Cells.SelectMany(c => c.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs))
            {
                claimed.Add(glyph);
            }

            kept.Add(table);
        }

        return [.. kept.OrderBy(t => t.Box.Top)];
    }

    /// <summary>Every grid the rules draw, before the checks that tell tables from charts and frames.</summary>
    internal static List<TableDraft> Candidates(List<Rule> rules, IReadOnlyList<PageGlyph> glyphs, bool empty = false)
    {
        List<TableDraft> grids = [];
        foreach (List<Rule> group in Stacked(Groups(rules)))
        {
            List<Rule> horizontal = [.. group.Where(r => r.Horizontal)], vertical = [.. group.Where(r => !r.Horizontal)];
            if (horizontal.Count >= 2 && vertical.Count >= 2 && Grid(horizontal, vertical, glyphs, empty) is TableDraft table)
            {
                grids.Add(table);
            }
        }

        return grids;
    }

    /// <summary>Adds the hint a stack of two rules or more with text between them makes.</summary>
    private static void Hint(List<Rule> rows, IReadOnlyList<PageGlyph> glyphs, List<TableHint> hints)
    {
        if (rows.Count >= 2)
        {
            PageBox box = new(rows.Min(r => r.From), rows[0].At, rows.Max(r => r.To), rows[^1].At);
            if (glyphs.Any(g => !g.IsSpace && box.Contains(g.Box.CenterX, g.Box.CenterY)))
            {
                hints.Add(new TableHint(box, [.. rows.Select(r => r.At)]));
            }
        }
    }

    /// <summary>
    /// Whether the text between two stacked rules ends one table and starts another: it holds a caption ("Table 2"), or three
    /// lines or more of body text that run right across the rules' span without a gap.
    /// </summary>
    private static bool Between(Rule upper, Rule lower, IReadOnlyList<PageGlyph> glyphs, double wordGap)
    {
        PageBox gap = new(Math.Max(upper.From, lower.From), upper.At, Math.Min(upper.To, lower.To), lower.At);
        List<PageLine> lines = PageReader.Lines(glyphs.Where(g => gap.Contains(g.Box.CenterX, g.Box.CenterY)));
        if (lines.Any(l => l.Text.TrimStart().StartsWith("Table", StringComparison.OrdinalIgnoreCase) && l.Text.Length > 6 && char.IsDigit(l.Text.TrimStart()[6..].TrimStart().FirstOrDefault())))
        {
            return true;
        }

        int prose = lines.Count(l => l.Box.Width > gap.Width * 0.8 && BlockBuilder.Cut(l, wordGap, LayoutThresholds.Default).Count() == 1);
        return prose >= 3;
    }

    /// <summary>
    /// Rule segments from paths: each straight piece of a stroked path that runs horizontal or vertical, and filled bars
    /// thinner than 2.5 points; the four edges of a filled box count when the box is no bigger than a table cell row.
    /// </summary>
    internal static List<Rule> Rules(IReadOnlyList<PagePath> paths)
    {
        List<Rule> rules = [];
        foreach (PagePath path in paths)
        {
            if (path.Filled && !path.Stroked)
            {
                PageBox b = path.Bounds;
                if (b.Height <= 2.5 && b.Width > b.Height)
                {
                    rules.Add(new Rule(true, b.CenterY, b.Left, b.Right));
                }
                else if (b.Width <= 2.5 && b.Height > b.Width)
                {
                    rules.Add(new Rule(false, b.CenterX, b.Top, b.Bottom));
                }
                else if (IsBox(path))
                {
                    // A shaded cell or row: its edges divide the table like rules do.
                    rules.Add(new Rule(true, b.Top, b.Left, b.Right));
                    rules.Add(new Rule(true, b.Bottom, b.Left, b.Right));
                    rules.Add(new Rule(false, b.Left, b.Top, b.Bottom));
                    rules.Add(new Rule(false, b.Right, b.Top, b.Bottom));
                }

                continue;
            }

            if (!path.Stroked)
            {
                continue;
            }

            PdfPoint start = default, current = default;
            foreach (PdfPathSegment segment in path.Segments)
            {
                switch (segment.Verb)
                {
                    case PdfPathVerb.MoveTo:
                        start = current = segment.Point;
                        break;
                    case PdfPathVerb.LineTo:
                        Add(rules, current, segment.Point);
                        current = segment.Point;
                        break;
                    case PdfPathVerb.CurveTo:
                        current = segment.Point;
                        break;
                    case PdfPathVerb.Close:
                        Add(rules, current, start);
                        current = start;
                        break;
                }
            }
        }

        return rules;
    }

    /// <summary>Collinear rules that overlap or nearly touch, merged into one.</summary>
    internal static List<Rule> Merge(List<Rule> rules)
    {
        List<Rule> merged = [];
        foreach (IGrouping<bool, Rule> direction in rules.GroupBy(r => r.Horizontal))
        {
            List<Rule> sorted = [.. direction.OrderBy(r => r.At).ThenBy(r => r.From)];
            List<List<Rule>> lines = [];
            foreach (Rule rule in sorted)
            {
                List<Rule>? line = lines.LastOrDefault(l => Math.Abs(l[0].At - rule.At) <= Tolerance);
                if (line is null)
                {
                    lines.Add([rule]);
                }
                else
                {
                    line.Add(rule);
                }
            }

            foreach (List<Rule> line in lines)
            {
                double at = line.Average(r => r.At);
                List<Rule> spans = [.. line.OrderBy(r => r.From)];
                double from = spans[0].From, to = spans[0].To;
                foreach (Rule rule in spans.Skip(1))
                {
                    if (rule.From <= to + (Tolerance * 2))
                    {
                        to = Math.Max(to, rule.To);
                    }
                    else
                    {
                        merged.Add(new Rule(direction.Key, at, from, to));
                        (from, to) = (rule.From, rule.To);
                    }
                }

                merged.Add(new Rule(direction.Key, at, from, to));
            }
        }

        return merged;
    }

    /// <summary>Rules joined where a horizontal one meets a vertical one; each group of two rules or more.</summary>
    private static List<List<Rule>> Groups(List<Rule> rules)
    {
        int[] parent = [.. Enumerable.Range(0, rules.Count)];
        for (int i = 0; i < rules.Count; i++)
        {
            for (int j = i + 1; j < rules.Count; j++)
            {
                if (rules[i].Horizontal != rules[j].Horizontal && Meet(rules[i], rules[j]))
                {
                    parent[Find(parent, j)] = Find(parent, i);
                }
            }
        }

        return [.. Enumerable.Range(0, rules.Count).GroupBy(i => Find(parent, i)).Where(g => g.Count() >= 2).Select(g => g.Select(i => rules[i]).ToList())];
    }

    /// <summary>
    /// Groups stacked one above the other with the same columns joined into one: a table whose rows are ruled or shaded one
    /// by one, with plain rows or small gaps between them. The columns' rules are carried across the gaps.
    /// </summary>
    private static List<List<Rule>> Stacked(List<List<Rule>> groups)
    {
        List<(List<Rule> Rules, List<double> Xs, PageBox Box)> stack = [.. groups.Select(g => (g, Distinct(g.Where(r => !r.Horizontal).Select(r => r.At)), Bounds(g)))
            .OrderBy(g => g.Item3.Top)];
        for (int i = 0; i < stack.Count; i++)
        {
            for (int j = i + 1; j < stack.Count; j++)
            {
                (List<Rule> a, List<double> ax, PageBox ab) = stack[i];
                (List<Rule> b, List<double> bx, PageBox bb) = stack[j];
                double gap = bb.Top - ab.Bottom, row = Distinct(a.Where(r => r.Horizontal).Select(r => r.At)) is { Count: >= 2 } ys ? ys.Zip(ys.Skip(1)).Max(p => p.Second - p.First) : 0;
                bool sameColumns = ax.Count >= 2 && ax.Count == bx.Count && ax.Zip(bx).All(p => Math.Abs(p.First - p.Second) <= Tolerance * 2);
                if (sameColumns && gap >= -Tolerance && gap <= Math.Max(row * 1.5, Tolerance * 2))
                {
                    List<Rule> joined = [.. a, .. b, .. ax.Select(x => new Rule(false, x, ab.Bottom, bb.Top)), new Rule(true, ab.Bottom, ab.Left, ab.Right), new Rule(true, bb.Top, bb.Left, bb.Right)];
                    stack[i] = (Merge(joined), ax, ab.Union(bb));
                    stack.RemoveAt(j);
                    j = i;
                }
            }
        }

        return [.. stack.Select(s => s.Rules)];
    }

    private static PageBox Bounds(List<Rule> rules)
    {
        return PageBox.Around(rules.Select(r => r.Horizontal ? new PageBox(r.From, r.At, r.To, r.At) : new PageBox(r.At, r.From, r.At, r.To)));
    }

    private static bool Meet(Rule a, Rule b)
    {
        (Rule h, Rule v) = a.Horizontal ? (a, b) : (b, a);
        return v.At >= h.From - Tolerance && v.At <= h.To + Tolerance && h.At >= v.From - Tolerance && h.At <= v.To + Tolerance;
    }

    /// <summary>
    /// The table a group of rules draws: rows and columns between its distinct heights and offsets, cells joined across
    /// missing rules, and the text inside each cell. Null for a single cell, or when no cell holds text.
    /// </summary>
    private static TableDraft? Grid(List<Rule> horizontal, List<Rule> vertical, IReadOnlyList<PageGlyph> glyphs, bool empty = false)
    {
        List<double> ys = Distinct(horizontal.Select(r => r.At)), xs = Distinct(vertical.Select(r => r.At));
        int rows = ys.Count - 1, columns = xs.Count - 1;
        if (rows < 1 || columns < 1 || rows * columns < 2)
        {
            return null;
        }

        // Each grid square joins the one to its right when no rule runs between them, and the one below likewise.
        int[] parent = [.. Enumerable.Range(0, rows * columns)];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                if (c + 1 < columns && !Covered(vertical, xs[c + 1], ys[r], ys[r + 1]))
                {
                    parent[Find(parent, (r * columns) + c + 1)] = Find(parent, (r * columns) + c);
                }

                if (r + 1 < rows && !Covered(horizontal, ys[r + 1], xs[c], xs[c + 1]))
                {
                    parent[Find(parent, ((r + 1) * columns) + c)] = Find(parent, (r * columns) + c);
                }
            }
        }

        PageBox box = new(xs[0], ys[0], xs[^1], ys[^1]);
        List<PageGlyph> inside = [.. glyphs.Where(g => box.Contains(g.Box.CenterX, g.Box.CenterY))];

        // The first row is a header when its text is bold and the rest of the table's is not.
        bool header = rows > 1 && inside.Where(g => !g.IsSpace && g.Box.CenterY < ys[1]).ToList() is { Count: > 0 } first && first.All(FontTraits.IsBold)
            && inside.Any(g => !g.IsSpace && g.Box.CenterY > ys[1] && !FontTraits.IsBold(g));
        // Each glyph goes to the merged cell of the grid square its centre is in, so no glyph lands in two cells even where a
        // missing rule leaves a cell that is not a rectangle.
        ILookup<int, PageGlyph> byCell = inside.ToLookup(g => Find(parent, (Index(ys, g.Box.CenterY) * columns) + Index(xs, g.Box.CenterX)));
        List<CellDraft> cells = [];
        foreach (IGrouping<int, int> merged in Enumerable.Range(0, rows * columns).GroupBy(i => Find(parent, i)))
        {
            int top = merged.Min(i => i / columns), bottom = merged.Max(i => i / columns), left = merged.Min(i => i % columns), right = merged.Max(i => i % columns);
            PageBox area = new(xs[left], ys[top], xs[right + 1], ys[bottom + 1]);
            cells.Add(new CellDraft(top, left, bottom - top + 1, right - left + 1, header && top == 0, PageReader.Lines(byCell[merged.Key])) { Box = area });
        }

        // A grid whose squares all merged is a framed box; a grid with no text is a drawing.
        if (cells.Count < 2 || (!empty && cells.All(c => c.Lines.Count == 0)))
        {
            return null;
        }

        cells.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Column.CompareTo(b.Column));
        return new TableDraft(cells, rows, columns) { Box = box, IsRuled = true, Confidence = 0.95 };
    }

    /// <summary>
    /// Why a grid is not a table but a chart, a diagram or a frame around the page: big enough; two cells with
    /// text or more, and a fifth of its cells or more once it has over six; more than one row unless three cells or more all
    /// hold text; not four cells or fewer with five lines or more each on average (a layout frame); hardly a word cut by a rule; and not many
    /// curves inside, not a sparse grid of tick marks, and no picture inside (a figure). Null when it reads as a table.
    /// </summary>
    internal static string? Rejection(TableDraft table, IReadOnlyList<PagePath> paths, IReadOnlyList<PageGlyph> glyphs, IReadOnlyList<PageImage>? images = null)
    {
        PageBox box = table.Box;
        List<CellDraft> text = [.. table.Cells.Where(c => c.Lines.Count > 0)];
        if (box.Width < 20 || box.Height < 10)
        {
            return "small";
        }

        if (text.Count < 2 || (table.Cells.Count > 6 && text.Count * 5 < table.Cells.Count))
        {
            return "little text";
        }

        if (table.Rows == 1 && (table.Columns < 3 || text.Count < table.Columns))
        {
            return "one row";
        }

        // Tick marks and plot frames draw many grid lines that hardly divide anything: few cells for the grid's size.
        if (table.Rows * table.Columns > 12 && table.Cells.Count * 5 < table.Rows * table.Columns)
        {
            return "sparse";
        }

        if (table.Cells.Count <= 4 && text.Average(c => c.Lines.Count) >= 5)
        {
            return "frame";
        }

        // A word whose glyphs fall in two cells has a rule through it: the lines are drawn over text, not around it.
        int words = 0, cut = 0;
        foreach (PageWord word in PageReader.Lines(glyphs.Where(g => box.Contains(g.Box.CenterX, g.Box.CenterY))).SelectMany(l => l.Words))
        {
            words++;
            cut += word.Glyphs.Select(g => table.Cells.FindIndex(c => c.Box.Contains(g.Box.CenterX, g.Box.CenterY))).Distinct().Count() > 1 ? 1 : 0;
        }

        if (cut * 10 > words)
        {
            return "cut words";
        }

        int curves = paths.Count(p => box.Contains(p.Bounds.CenterX, p.Bounds.CenterY) && p.Segments.Any(s => s.Verb == PdfPathVerb.CurveTo));
        if (curves > Math.Max(4, table.Cells.Count / 2))
        {
            return "curves";
        }

        // A picture inside makes the grid a figure's frame or panels.
        return images?.Any(i => i.Bounds.Overlap(box) > box.Area * 0.1) == true ? "picture" : null;
    }

    /// <summary>The grid row or column a coordinate falls in, given the sorted rule positions (clamped to the grid).</summary>
    private static int Index(List<double> edges, double at)
    {
        int i = edges.BinarySearch(at);
        i = i < 0 ? ~i - 1 : i;
        return Math.Clamp(i, 0, edges.Count - 2);
    }

    /// <summary>Whether rules at <paramref name="at"/> cover most of the span from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static bool Covered(List<Rule> rules, double at, double from, double to)
    {
        double covered = 0;
        foreach (Rule rule in rules.Where(r => Math.Abs(r.At - at) <= Tolerance))
        {
            covered += Math.Max(0, Math.Min(rule.To, to) - Math.Max(rule.From, from));
        }

        return covered >= (to - from) * 0.7;
    }

    /// <summary>Values within the tolerance of each other taken as one, sorted.</summary>
    private static List<double> Distinct(IEnumerable<double> values)
    {
        List<double> distinct = [];
        foreach (double value in values.Order())
        {
            if (distinct.Count == 0 || value - distinct[^1] > Tolerance)
            {
                distinct.Add(value);
            }
        }

        return distinct;
    }

    private static void Add(List<Rule> rules, PdfPoint a, PdfPoint b)
    {
        double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
        if (dy <= 0.5 && dx > 2)
        {
            rules.Add(new Rule(true, (a.Y + b.Y) / 2, Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
        }
        else if (dx <= 0.5 && dy > 2)
        {
            rules.Add(new Rule(false, (a.X + b.X) / 2, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
        }
    }

    /// <summary>Whether a path is one axis-aligned rectangle (a <c>re</c>, or four lines that close).</summary>
    private static bool IsBox(PagePath path)
    {
        List<PdfPoint> points = [.. path.Segments.Where(s => s.Verb is PdfPathVerb.MoveTo or PdfPathVerb.LineTo).Select(s => s.Point)];
        if (points.Count is < 4 or > 5 || path.Segments.Any(s => s.Verb == PdfPathVerb.CurveTo) || path.Segments.Count(s => s.Verb == PdfPathVerb.MoveTo) != 1)
        {
            return false;
        }

        PageBox b = path.Bounds;
        return points.All(p => (Math.Abs(p.X - b.Left) < 0.5 || Math.Abs(p.X - b.Right) < 0.5) && (Math.Abs(p.Y - b.Top) < 0.5 || Math.Abs(p.Y - b.Bottom) < 0.5));
    }

    private static bool Inside(Rule rule, PageBox box)
    {
        return rule.Horizontal
            ? rule.At >= box.Top - Tolerance && rule.At <= box.Bottom + Tolerance && rule.From >= box.Left - Tolerance && rule.To <= box.Right + Tolerance
            : rule.At >= box.Left - Tolerance && rule.At <= box.Right + Tolerance && rule.From >= box.Top - Tolerance && rule.To <= box.Bottom + Tolerance;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }
}
