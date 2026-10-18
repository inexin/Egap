using Egap.Documents;

namespace Egap.Pdf.Layout;

internal sealed partial class ModelBuilder
{
    /// <summary>
    /// Writes a detected table (design Q5): its grid from where the columns start, cells spanning columns and rows, leading
    /// header rows repeated on each page, single rules for a ruled table and none for a borderless one, and each cell's text
    /// as paragraphs split where the lines leave a gap. A table that carries on one from the page before adds its rows to it,
    /// less the header it repeats; a part of a tagged table goes on its first part's grid, and a row the page broke in two,
    /// one TR in the tags, is one row again.
    /// </summary>
    private void WriteTable(BlockContainer body, TableDraft draft, Frame frame)
    {
        // A later part of a tagged table, on the first part's columns.
        Table? earlier = null;
        if (draft.Continues && draft.Tag is not null && tagged.TryGetValue(draft.Tag, out var first) && OnGrid(draft, first.Edges) is TableDraft mapped)
        {
            earlier = first.Table;
            draft = mapped;
        }
        else if (draft.Continues && lastTable is not null && lastTable.Grid?.Count == draft.Columns)
        {
            earlier = lastTable;
        }

        List<double> edges = Edges(draft);
        int headers = 0;
        while (headers < draft.Rows && draft.Cells.Where(c => c.Row == headers).All(c => c.Header) && draft.Cells.Any(c => c.Row == headers))
        {
            headers++;
        }

        Table table;
        int from = 0;
        if (earlier is not null)
        {
            table = earlier;

            // The rows that repeat the first part's header, word for word, are left out.
            while (draft.RepeatsHeader && from < draft.Rows && from < table.Rows.Count
                && Squash(string.Concat(draft.Cells.Where(c => c.Row == from).OrderBy(c => c.Column).Select(c => Paragraphs.Text(c.Lines)))) == Squash(table.Rows[from].GetText()))
            {
                from++;
            }

            // The rest of a row the page broke: the same TR as the earlier part's last row, or a piece of a row (Word's way).
            if (draft.Tag is not null && table.Rows.Count > 0 && from < draft.Rows
                && ((draft.RowTags?.ElementAtOrDefault(from) is TagNode split && tagged.TryGetValue(draft.Tag, out var before) && before.LastRow == split)
                    || draft.Pieces?.Contains(from) == true))
            {
                JoinRow(table.Rows[^1], draft.Cells.Where(c => c.Row == from));
                from++;
            }
        }
        else
        {
            table = new Table(document) { Grid = [.. edges.Zip(edges.Skip(1)).Select(p => Math.Round((p.Second - p.First) * 2) / 2)] };
            table.Format.Indent = StyleSheet.Indent(edges[0] - frame.Left);
            Border border = draft.IsRuled ? new Border(BorderStyle.Single, 0.5) : Border.None;
            table.Format.BorderTop = border;
            table.Format.BorderBottom = border;
            table.Format.BorderLeft = border;
            table.Format.BorderRight = border;
            table.Format.BorderInsideHorizontal = border;
            table.Format.BorderInsideVertical = border;
            table.Format.LookFirstRow = headers > 0;
            body.Blocks.Add(table);
            lists.Break();
        }

        // A cell that starts inside another's span (a grid nested in a merged cell) adds its lines to that cell.
        Dictionary<(int Row, int Column), CellDraft> covered = [];
        Dictionary<CellDraft, List<PageLine>> lines = new(ReferenceEqualityComparer.Instance);
        foreach (CellDraft cell in draft.Cells.OrderByDescending(c => c.RowSpan * c.ColumnSpan))
        {
            if (covered.GetValueOrDefault((cell.Row, cell.Column)) is CellDraft outer)
            {
                lines[outer].AddRange(cell.Lines);
                continue;
            }

            lines[cell] = [.. cell.Lines];
            for (int r = cell.Row; r < cell.Row + cell.RowSpan; r++)
            {
                for (int c = cell.Column; c < cell.Column + cell.ColumnSpan; c++)
                {
                    covered.TryAdd((r, c), cell);
                }
            }
        }

        for (int r = from; r < draft.Rows; r++)
        {
            Row row = new(document);
            row.Format.IsHeader = r < headers && from == 0 ? true : null;
            for (int c = 0; c < draft.Columns;)
            {
                Cell cell = new(document);
                CellDraft? source = covered.GetValueOrDefault((r, c));
                int span = source is null ? 1 : Math.Min(source.ColumnSpan, draft.Columns - c);
                if (source is not null && source.Column != c)
                {
                    // A spanned position left of which the cell started: Word has no cell there.
                    c++;
                    continue;
                }

                if (span > 1)
                {
                    cell.Format.GridSpan = span;
                }

                cell.Format.Width = TableWidth.Points(Math.Round((edges[Math.Min(c + span, edges.Count - 1)] - edges[c]) * 2) / 2);
                if (source is not null && source.RowSpan > 1)
                {
                    cell.Format.VerticalMerge = source.Row == r ? VerticalMerge.Restart : VerticalMerge.Continue;
                }

                if (source is not null && source.Row == r)
                {
                    WriteCell(cell, source, [.. lines[source].OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left)]);
                }
                else
                {
                    cell.Blocks.Add(new Paragraph(document) { Style = styles.Normal.Style });
                }

                row.Cells.Add(cell);
                c += span;
            }

            table.Rows.Add(row);
        }

        lastTable = table;
        last = null;
        if (draft.Tag is not null)
        {
            List<double> grid = earlier is not null && tagged.TryGetValue(draft.Tag, out var known) ? known.Edges : edges;
            tagged[draft.Tag] = (table, grid, draft.RowTags?.LastOrDefault());
        }
    }

    /// <summary>
    /// A part of a tagged table on its first part's columns: each of its column edges matched to the nearest first-part edge
    /// within a cell's padding, cells renumbered and their spans widened to match. Null when an edge has no match.
    /// </summary>
    private static TableDraft? OnGrid(TableDraft part, List<double> grid)
    {
        List<double> edges = Edges(part);
        int[] map = new int[edges.Count];
        for (int k = 0; k < edges.Count; k++)
        {
            int nearest = grid.Select((x, i) => (Distance: Math.Abs(x - edges[k]), Index: i)).MinBy(p => p.Distance).Index;
            if (Math.Abs(grid[nearest] - edges[k]) > 8 || (k > 0 && nearest <= map[k - 1]))
            {
                return part.Columns == grid.Count - 1 ? part : null;
            }

            map[k] = nearest;
        }

        List<CellDraft> cells = [.. part.Cells.Select(c => new CellDraft(c.Row, map[c.Column], c.RowSpan,
            map[Math.Min(c.Column + c.ColumnSpan, edges.Count - 1)] - map[c.Column], c.Header, c.Lines) { Box = c.Box, Tables = c.Tables })];
        return new TableDraft(cells, part.Rows, grid.Count - 1)
        {
            Box = part.Box,
            IsRuled = part.IsRuled,
            Continues = part.Continues,
            RepeatsHeader = part.RepeatsHeader,
            Order = part.Order,
            Confidence = part.Confidence,
            Tag = part.Tag,
            RowTags = part.RowTags,
            Pieces = part.Pieces,
        };
    }

    /// <summary>Adds a broken row's second part to its first: each cell's paragraphs to the cell at the same grid column.</summary>
    private void JoinRow(Row row, IEnumerable<CellDraft> rest)
    {
        foreach (CellDraft part in rest.Where(c => c.Lines.Count > 0))
        {
            int at = 0;
            Cell? target = null;
            foreach (Cell cell in row.Cells)
            {
                int span = cell.Format.GridSpan ?? 1;
                if (part.Column >= at && part.Column < at + span)
                {
                    target = cell;
                    break;
                }

                at += span;
            }

            if (target is null)
            {
                continue;
            }

            // A cell holding only the empty paragraph a cell must have takes the text in its place.
            if (target.Blocks.Count == 1 && target.Blocks[0] is Paragraph only && only.GetText().Length == 0)
            {
                target.Blocks.Clear();
            }

            Cell scratch = new(document);
            WriteCell(scratch, part, [.. part.Lines.OrderBy(l => l.Box.Top).ThenBy(l => l.Box.Left)]);
            foreach (Block block in scratch.Blocks.ToList())
            {
                scratch.Blocks.Remove(block);
                target.Blocks.Add(block);
            }
        }
    }

    /// <summary>A cell's lines as paragraphs, a new one where the gap between lines is wider than a line.</summary>
    private void WriteCell(Cell cell, CellDraft source, List<PageLine> lines)
    {
        List<List<PageLine>> paragraphs = [];
        foreach (PageLine line in lines)
        {
            if (paragraphs.Count == 0 || line.Box.Top - paragraphs[^1][^1].Box.Bottom > line.Size * 0.8)
            {
                paragraphs.Add([]);
            }

            paragraphs[^1].Add(line);
        }

        if (paragraphs.Count == 0 && source.Tables.Count == 0)
        {
            cell.Blocks.Add(new Paragraph(document) { Style = styles.Normal.Style });
            return;
        }

        // Tables nested in the cell go between its paragraphs where they stand; a cell ends with a paragraph, as Word's do.
        List<TableDraft> nested = [.. source.Tables.OrderBy(t => t.Box.Top)];
        Frame frame = new(source.Box.Left, source.Box.Right);
        foreach (List<PageLine> part in paragraphs)
        {
            while (nested.Count > 0 && nested[0].Box.Top < part[0].Box.Top)
            {
                WriteNested(cell, nested[0], frame);
                nested.RemoveAt(0);
            }

            Paragraph paragraph = new(document) { Style = styles.Normal.Style };
            paragraph.Format.SpaceBefore = 0;
            paragraph.Format.SpaceAfter = 0;
            paragraph.Format.FirstLineIndent = 0;
            paragraph.Format.LeftIndent = 0;
            double center = (frame.Left + frame.Right) / 2;
            if (part.All(l => Math.Abs(((l.Box.Left + l.Box.Right) / 2) - center) < 2) && part.Min(l => l.Box.Left) - frame.Left > 6)
            {
                paragraph.Format.Alignment = ParagraphAlignment.Center;
            }
            else if (part.All(l => frame.Right - l.Box.Right < 4) && part.Min(l => l.Box.Left) - frame.Left > frame.Width * 0.25)
            {
                paragraph.Format.Alignment = ParagraphAlignment.Right;
            }

            writer.Write(paragraph, part, styles.Normal.Font);
            cell.Blocks.Add(paragraph);
        }

        foreach (TableDraft table in nested)
        {
            WriteNested(cell, table, frame);
        }

        if (cell.Blocks.Count == 0 || cell.Blocks[^1] is Table)
        {
            cell.Blocks.Add(new Paragraph(document) { Style = styles.Normal.Style });
        }
    }

    /// <summary>Writes a table nested in a cell, leaving the outer table's place in the page's flow as it was.</summary>
    private void WriteNested(Cell cell, TableDraft table, Frame frame)
    {
        // A cell or table with no text on the page has no place; it measures from the left edge.
        if (!double.IsFinite(frame.Left) || !double.IsFinite(frame.Right))
        {
            frame = double.IsFinite(table.Box.Left) && double.IsFinite(table.Box.Right) ? new Frame(table.Box.Left, table.Box.Right) : new Frame(0, 0);
        }

        (Table? outer, Paragraph? before) = (lastTable, last);
        WriteTable(cell, table, frame);
        (lastTable, last) = (outer, before);
    }

    /// <summary>The x of each grid line: where cells starting in each column start, the table's right after the last.</summary>
    private static List<double> Edges(TableDraft table)
    {
        List<double> edges = [];
        for (int c = 0; c <= table.Columns; c++)
        {
            IEnumerable<double> starts = c < table.Columns ? table.Cells.Where(x => x.Column == c && !x.Box.IsEmpty).Select(x => x.Box.Left)
                : table.Cells.Where(x => x.Column + x.ColumnSpan == c && !x.Box.IsEmpty).Select(x => x.Box.Right);
            edges.Add(starts.DefaultIfEmpty(double.NaN).Min());
        }

        // Columns no cell starts in sit evenly between their neighbours.
        double left = double.IsFinite(table.Box.Left) ? table.Box.Left : 0, right = double.IsFinite(table.Box.Right) && table.Box.Right > left ? table.Box.Right : left + (table.Columns * 36);
        if (!double.IsFinite(edges[0]))
        {
            edges[0] = left;
        }

        if (!double.IsFinite(edges[^1]) || edges[^1] <= edges[0])
        {
            edges[^1] = right;
        }

        for (int c = 1; c < edges.Count; c++)
        {
            if (double.IsNaN(edges[c]))
            {
                int next = edges.FindIndex(c, e => !double.IsNaN(e));
                edges[c] = edges[c - 1] + ((edges[next] - edges[c - 1]) / (next - c + 1));
            }

            edges[c] = double.IsFinite(edges[c]) ? Math.Max(edges[c], edges[c - 1] + 1) : edges[c - 1] + 36;
        }

        return edges;
    }

    private static string Squash(string text)
    {
        return string.Concat(text.Where(char.IsLetterOrDigit));
    }
}
