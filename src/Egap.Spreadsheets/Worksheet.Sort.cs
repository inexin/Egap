using System.Globalization;

using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets;

/// <summary>Sorting ranges of cells, as Excel's Sort does.</summary>
public sealed partial class Worksheet
{
    /// <summary>
    /// Sorts the rows (or columns) of a range by the values of key cells. Each row moves as if copied: its values, styles,
    /// notes and links go with it, and its formulas' relative references move as copying moves them. Hidden rows stay where
    /// they are, as in Excel.
    /// </summary>
    internal void SortRange(RangeAddress range, SortOptions options, SortField[] keys)
    {
        bool rows = options.Orientation == SortOrientation.Rows;
        int width = rows ? range.ColumnCount : range.RowCount;
        if (keys.Length == 0)
        {
            throw new ArgumentException("A sort needs at least one key.", nameof(keys));
        }

        foreach (SortField key in keys)
        {
            if (key.Index < 0 || key.Index >= width)
            {
                throw new ArgumentOutOfRangeException(nameof(keys), key.Index, $"A key must be in the range: 0 to {width - 1}.");
            }
        }

        // Excel sorts merges only when every cell of the range is one of a grid of equal merges, so Egap refuses any in the
        // part that moves; a header's merges stay where they are.
        RangeAddress moved = !options.HasHeader ? range
            : rows ? new RangeAddress(range.First.Row + 1, range.First.Column, range.Last.Row, range.Last.Column)
            : new RangeAddress(range.First.Row, range.First.Column + 1, range.Last.Row, range.Last.Column);
        if ((rows ? range.RowCount : range.ColumnCount) > 1 && MergedCells.Any(moved.Intersects))
        {
            throw new InvalidOperationException("The range has merged cells; Excel sorts them only when all are the same size.");
        }

        if (Arrays(this).Any(a => a.Intersects(range) && a.First != a.Last))
        {
            throw new InvalidOperationException("Part of an array formula can't be changed.");
        }

        foreach (RangeAddress pivot in Xlsx.SheetObjectReader.PivotTables(this).Where(range.Intersects))
        {
            throw new InvalidOperationException($"The sort would change the pivot table at {pivot}, which Egap does not change.");
        }

        int first = rows ? range.First.Row : range.First.Column, last = rows ? range.Last.Row : range.Last.Column;
        if (options.HasHeader)
        {
            first++;
        }

        List<int> lines = [];
        for (int line = first; line <= last; line++)
        {
            if (rows ? !IsRowHidden(line) : !IsColumnHidden(line))
            {
                lines.Add(line);
            }
        }

        if (lines.Count < 2)
        {
            return;
        }

        CompareInfo compare = Workbook.Culture.CompareInfo;
        CellValue[][] values = new CellValue[lines.Count][];
        for (int i = 0; i < lines.Count; i++)
        {
            values[i] = new CellValue[keys.Length];
            for (int k = 0; k < keys.Length; k++)
            {
                values[i][k] = rows
                    ? GetValue(lines[i], range.First.Column + keys[k].Index)
                    : GetValue(range.First.Row + keys[k].Index, lines[i]);
            }
        }

        int[] order = [.. Enumerable.Range(0, lines.Count)];
        Array.Sort(order, (x, y) =>
        {
            for (int k = 0; k < keys.Length; k++)
            {
                int result = CompareForSort(values[x][k], values[y][k], keys[k].Descending, options.MatchCase, compare);
                if (result != 0)
                {
                    return result;
                }
            }

            return x.CompareTo(y);
        });

        if (order.Select((source, slot) => source == slot).All(same => same))
        {
            return;
        }

        UnshareAll(false);
        RangeAddress Line(int line) => rows
            ? new RangeAddress(line, range.First.Column, line, range.Last.Column)
            : new RangeAddress(range.First.Row, line, range.Last.Row, line);

        // Each line is taken as a copy to the slot it sorts into, before any is written.
        List<(List<TakenCell> Cells, RangeAddress To, List<CellComment> Notes, List<Hyperlink> Links)> moves = [];
        for (int slot = 0; slot < lines.Count; slot++)
        {
            int from = lines[order[slot]], to = lines[slot];
            RangeAddress source = Line(from);
            CellMove copy = rows ? new(this, source, this, to - from, 0) : new(this, source, this, 0, to - from);
            moves.Add((Take(source, copy, moving: false, sorting: true), Line(to),
                [.. Comments.Where(c => source.Contains(c.Cell))], [.. Hyperlinks.Where(l => source.Contains(l.Range))]));
        }

        foreach (int line in lines)
        {
            ClearCells(Line(line));
        }

        foreach ((List<TakenCell> cells, RangeAddress to, List<CellComment> notes, List<Hyperlink> links) in moves)
        {
            Put(cells, to.First);
            foreach (CellComment note in notes)
            {
                note.Cell = rows ? new CellAddress(to.First.Row, note.Cell.Column) : new CellAddress(note.Cell.Row, to.First.Column);
            }

            foreach (Hyperlink link in links)
            {
                link.Range = rows
                    ? new RangeAddress(to.First.Row, link.Range.First.Column, to.First.Row, link.Range.Last.Column)
                    : new RangeAddress(link.Range.First.Row, to.First.Column, link.Range.Last.Row, to.First.Column);
            }
        }

        sharedMasters = null;
        Workbook.Recompile();
    }

    /// <summary>
    /// A formula's text for its cell's new place in a sort, as Excel writes it: references without a sheet name shift with
    /// the cell, while those with one, even the cell's own sheet, stay. A cell shifted off the sheet becomes <c>#REF!</c>; a
    /// range with one edge off is cut back to the sheet's edge, and one with both off becomes <c>#REF!</c>.
    /// </summary>
    private static string Sorted(ParsedFormula parsed, int rowOffset, int columnOffset) =>
        FormulaPrinter.Print(parsed, reference => reference.Prefix is null ? Shifted(reference.Area, rowOffset, columnOffset) : null, reference => null);

    private static Area? Shifted(Area area, int rowOffset, int columnOffset)
    {
        if (area.Kind == AreaKind.Error)
        {
            return null;
        }

        bool rows = area.Kind != AreaKind.Columns, columns = area.Kind != AreaKind.Rows;
        int firstRow = area.FirstRow + (rows && !area.FirstRowAbsolute ? rowOffset : 0);
        int lastRow = area.LastRow + (rows && !area.LastRowAbsolute ? rowOffset : 0);
        int firstColumn = area.FirstColumn + (columns && !area.FirstColumnAbsolute ? columnOffset : 0);
        int lastColumn = area.LastColumn + (columns && !area.LastColumnAbsolute ? columnOffset : 0);
        if (firstRow == area.FirstRow && lastRow == area.LastRow && firstColumn == area.FirstColumn && lastColumn == area.LastColumn)
        {
            return null;
        }

        static bool Off(int value, int count) => value < 0 || value >= count;
        bool rowsOff = Off(firstRow, CellAddress.MaxRows), lastRowOff = Off(lastRow, CellAddress.MaxRows);
        bool columnsOff = Off(firstColumn, CellAddress.MaxColumns), lastColumnOff = Off(lastColumn, CellAddress.MaxColumns);
        if ((area.Kind == AreaKind.Cell && (rowsOff || columnsOff)) || (rowsOff && lastRowOff) || (columnsOff && lastColumnOff))
        {
            return area with { Kind = AreaKind.Error };
        }

        return area with
        {
            FirstRow = Math.Clamp(firstRow, 0, CellAddress.MaxRows - 1),
            LastRow = Math.Clamp(lastRow, 0, CellAddress.MaxRows - 1),
            FirstColumn = Math.Clamp(firstColumn, 0, CellAddress.MaxColumns - 1),
            LastColumn = Math.Clamp(lastColumn, 0, CellAddress.MaxColumns - 1),
        };
    }

    private bool IsRowHidden(int row) => RowRecords.TryGetValue(row, out var record) && record.Hidden;

    private bool IsColumnHidden(int column) => ColumnSpans.Find(column)?.Hidden == true;

    /// <summary>Empties cells, with their formulas and file extras, leaving merges, notes and links.</summary>
    private void ClearCells(RangeAddress range)
    {
        foreach ((int row, int column) in store.Occupied(range).ToList())
        {
            ref readonly CellRecord old = ref store.Get(row, column);
            if (old.Kind == CellKind.Formula)
            {
                spills.Remove(formulas[(int)old.Payload]);
                formulas.Remove((int)old.Payload);
            }

            store.Set(row, column, default);
            CellExtras.Remove(Key(row, column));
            Changed(row, column);
        }
    }

    /// <summary>
    /// Compares two key values as Excel's Sort orders them: numbers, then text, then FALSE and TRUE, then errors, which keep
    /// their order; descending turns that around. Empty cells come last either way.
    /// </summary>
    internal static int CompareForSort(CellValue a, CellValue b, bool descending, bool matchCase, CompareInfo compare)
    {
        bool emptyA = a.Kind == CellValueKind.Empty, emptyB = b.Kind == CellValueKind.Empty;
        if (emptyA || emptyB)
        {
            return emptyA == emptyB ? 0 : emptyA ? 1 : -1;
        }

        int rankA = SortRank(a.Kind), rankB = SortRank(b.Kind);
        int result = rankA != rankB ? rankA.CompareTo(rankB) : a.Kind switch
        {
            CellValueKind.Number => a.Number.CompareTo(b.Number),
            CellValueKind.Text => SortText.Compare(a.Text, b.Text, matchCase, compare),
            CellValueKind.Boolean => a.Boolean.CompareTo(b.Boolean),
            _ => 0,
        };
        return descending ? -result : result;
    }

    private static int SortRank(CellValueKind kind) => kind switch
    {
        CellValueKind.Number => 0,
        CellValueKind.Text => 1,
        CellValueKind.Boolean => 2,
        _ => 3,
    };
}

/// <summary>
/// Excel's order for text in a sort, which is Windows' word sort: hyphens, dashes and apostrophes are left out but for
/// breaking a tie, where text without them comes first; spaces and other symbols come before digits and letters, in
/// Windows' order; runs of digits and letters compare by the culture's rules, without case first and with it only when
/// asked and all else is equal.
/// </summary>
internal static class SortText
{
    /// <summary>The symbols in the order Windows' word sort gives them, all before digits and letters.</summary>
    private const string Symbols = "\t\n\r \u00A0!\"#$%&()*,./:;?@[\\]^_`{|}~\u00B4\u2018\u2019\u201C\u201D+<=>\u00B1\u00AB\u00BB\u00A7\u00B0";

    /// <summary>How much of a text a sort looks at: Excel's sort ignores the rest.</summary>
    private const int Compared = 255;

    public static int Compare(string a, string b, bool matchCase, CompareInfo compare)
    {
        a = a.Length > Compared ? a[..Compared] : a;
        b = b.Length > Compared ? b[..Compared] : b;
        string strippedA = Strip(a), strippedB = Strip(b);
        int result = Runs(strippedA, strippedB, compare, CompareOptions.IgnoreCase);
        if (result == 0 && matchCase)
        {
            result = Runs(strippedA, strippedB, compare, CompareOptions.None);
        }

        return result != 0 ? result : Marks(a, b);
    }

    /// <summary>
    /// Breaks a tie by the left-out marks: at the first place they differ, the text whose mark comes later, or that has no
    /// more, sorts first, as "CT13-4" before "CT1-34" and "coop" before "co-op"; then an apostrophe before a hyphen.
    /// </summary>
    private static int Marks(string a, string b)
    {
        int i = -1, j = -1;
        while (true)
        {
            i = a.AsSpan(i + 1).IndexOfAny(Ignored) is int x and >= 0 ? i + 1 + x : -1;
            j = b.AsSpan(j + 1).IndexOfAny(Ignored) is int y and >= 0 ? j + 1 + y : -1;
            if (i < 0 || j < 0)
            {
                return i < 0 && j < 0 ? 0 : i < 0 ? -1 : 1;
            }

            // Both texts have had the same number of marks so far, so their places in the kept text differ as these do.
            if (i != j)
            {
                return i > j ? -1 : 1;
            }

            if (a[i] != b[j])
            {
                return a[i].CompareTo(b[j]);
            }

        }
    }

    /// <summary>Compares symbol by symbol, and runs of other characters by the culture.</summary>
    private static int Runs(string a, string b, CompareInfo compare, CompareOptions options)
    {
        int i = 0, j = 0;
        while (true)
        {
            while (i < a.Length && a[i] == Joiner)
            {
                i++;
            }

            while (j < b.Length && b[j] == Joiner)
            {
                j++;
            }

            if (i == a.Length || j == b.Length)
            {
                return (i == a.Length ? 0 : 1) - (j == b.Length ? 0 : 1);
            }

            int rankA = Symbols.IndexOf(a[i], StringComparison.Ordinal), rankB = Symbols.IndexOf(b[j], StringComparison.Ordinal);
            if (rankA >= 0 || rankB >= 0)
            {
                if (rankA < 0 || rankB < 0)
                {
                    return rankA >= 0 ? -1 : 1;
                }

                if (rankA != rankB)
                {
                    return rankA.CompareTo(rankB);
                }

                i++;
                j++;
                continue;
            }

            int endA = RunEnd(a, i), endB = RunEnd(b, j);
            int result = compare.Compare(a, i, endA - i, b, j, endB - j, options);
            if (result != 0)
            {
                return Math.Sign(result);
            }

            i = endA;
            j = endB;
        }
    }

    private static int RunEnd(string text, int start)
    {
        int end = start;
        while (end < text.Length && Symbols.IndexOf(text[end], StringComparison.Ordinal) < 0)
        {
            end++;
        }

        return end;
    }

    /// <summary>
    /// The text with each left-out mark turned into the combining grapheme joiner, which sorts as nothing but still parts the
    /// letters on either side, as "a-a" is not the "aa" that some cultures sort as one letter.
    /// </summary>
    private static string Strip(string text)
    {
        if (text.AsSpan().IndexOfAny(Ignored) < 0)
        {
            return text;
        }

        System.Text.StringBuilder kept = new(text.Length);
        foreach (char c in text)
        {
            kept.Append(Ignored.Contains(c, StringComparison.Ordinal) ? Joiner : c);
        }

        return kept.ToString();
    }

    /// <summary>The combining grapheme joiner, which stands for a left-out mark.</summary>
    private const char Joiner = '\u034F';

    /// <summary>The characters word sort leaves out: apostrophe, hyphen, soft hyphen, en and em dash.</summary>
    private const string Ignored = "'-\u00AD\u2013\u2014";
}
