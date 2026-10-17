using Egap.Documents;

namespace Egap.Pdf.Layout;

internal sealed partial class ModelBuilder
{
    /// <summary>
    /// Writes a detected table (design Q5): its grid from where the columns start, cells spanning columns and rows, leading
    /// header rows repeated on each page, single rules for a ruled table and none for a borderless one, and each cell's text
    /// as paragraphs split where the lines leave a gap. A table that carries on one from the page before adds its rows to it,
    /// less the header it repeats.
    /// </summary>
    private void WriteTable(BlockContainer body, TableDraft draft, Frame frame)
    {
        List<double> edges = Edges(draft);
        int headers = 0;
        while (headers < draft.Rows && draft.Cells.Where(c => c.Row == headers).All(c => c.Header) && draft.Cells.Any(c => c.Row == headers))
        {
            headers++;
        }

        Table table;
        int from = 0;
        if (draft.Continues && lastTable is not null && lastTable.Grid?.Count == draft.Columns)
        {
            table = lastTable;

            // The rows that repeat the first part's header, word for word, are left out.
            while (draft.RepeatsHeader && from < draft.Rows && from < table.Rows.Count
                && Squash(string.Concat(draft.Cells.Where(c => c.Row == from).OrderBy(c => c.Column).Select(c => Paragraphs.Text(c.Lines)))) == Squash(table.Rows[from].GetText()))
            {
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

        if (paragraphs.Count == 0)
        {
            cell.Blocks.Add(new Paragraph(document) { Style = styles.Normal.Style });
            return;
        }

        Frame frame = new(source.Box.Left, source.Box.Right);
        foreach (List<PageLine> part in paragraphs)
        {
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
