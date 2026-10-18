namespace Egap.Pdf.Layout;

/// <summary>
/// Tables continued across pages (design Q5, Build Plan task 8): the last table on a page that runs to the foot of the text,
/// and the first table on the next page that starts at the head of the text, with the same number of columns at the same
/// places, are one table. When the later part's first row repeats the earlier part's header, it is marked as repeated.
/// </summary>
internal static class TableJoins
{
    /// <summary>
    /// Marks each table that carries on the last table of the page before it, in place. A tagged table carries on any
    /// table of the page before from the same Table element, wherever it stands, since the tags say so.
    /// </summary>
    public static void AcrossPages(IReadOnlyList<PageDraft> pages)
    {
        for (int i = 1; i < pages.Count; i++)
        {
            foreach (TableDraft part in pages[i].Tables.Where(t => t.Tag is not null))
            {
                if (pages[i - 1].Tables.FirstOrDefault(t => t.Tag == part.Tag) is TableDraft earlier)
                {
                    part.Continues = true;
                    List<string> heading = Row(earlier, earlier.Cells.Any(c => c.Header) ? earlier.Cells.Where(c => c.Header).Min(c => c.Row) : 0);
                    part.RepeatsHeader = heading.Count > 0 && Row(part, 0).SequenceEqual(heading);
                }
            }

            TableDraft? before = Last(pages[i - 1]), next = First(pages[i]);

            // Two tables the tags tell apart stay apart, however alike their columns.
            if (next?.Continues == true || (before?.Tag is not null && next?.Tag is not null))
            {
                continue;
            }

            if (before is null || next is null || before.Columns != next.Columns || !SameColumns(before, next))
            {
                continue;
            }

            next.Continues = true;
            List<string> header = Row(before, before.Cells.Any(c => c.Header) ? before.Cells.Where(c => c.Header).Min(c => c.Row) : 0);
            if (header.Count > 0 && Row(next, 0).SequenceEqual(header))
            {
                next.RepeatsHeader = true;
            }
        }
    }

    /// <summary>The page's lowest table when no body text follows it on the page.</summary>
    private static TableDraft? Last(PageDraft page)
    {
        TableDraft? last = page.Tables.MaxBy(t => t.Box.Bottom);
        return last is not null && !page.Blocks.Any(b => Body(b) && b.Box.Top > last.Box.Bottom) ? last : null;
    }

    /// <summary>The page's highest table when no body text comes before it on the page.</summary>
    private static TableDraft? First(PageDraft page)
    {
        TableDraft? first = page.Tables.MinBy(t => t.Box.Top);
        return first is not null && !page.Blocks.Any(b => Body(b) && b.Box.Bottom < first.Box.Top) ? first : null;
    }

    private static bool Body(BlockDraft block)
    {
        return block.Role is not (LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber or LayoutRole.Footnote or LayoutRole.Caption);
    }

    /// <summary>Whether two tables' columns start within a few points of each other.</summary>
    private static bool SameColumns(TableDraft a, TableDraft b)
    {
        for (int c = 0; c < a.Columns; c++)
        {
            double? left = Left(a, c), right = Left(b, c);
            if (left is double l && right is double r && Math.Abs(l - r) > 6)
            {
                return false;
            }
        }

        return true;
    }

    private static double? Left(TableDraft table, int column)
    {
        List<CellDraft> cells = [.. table.Cells.Where(c => c.Column == column && c.ColumnSpan == 1 && !c.Box.IsEmpty)];
        return cells.Count == 0 ? null : cells.Min(c => c.Box.Left);
    }

    private static List<string> Row(TableDraft table, int row)
    {
        return [.. table.Cells.Where(c => c.Row == row).OrderBy(c => c.Column).Select(c => c.Text)];
    }
}
