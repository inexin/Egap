namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// Cells cut from <see cref="From"/> and pasted at <see cref="To"/>, as Excel's Cut and Paste move them: the cells of
/// <see cref="Source"/> go <see cref="RowOffset"/> rows and <see cref="ColumnOffset"/> columns on, onto
/// <see cref="Destination"/>, over what was there.
/// </summary>
internal readonly record struct CellMove(Worksheet From, RangeAddress Source, Worksheet To, int RowOffset, int ColumnOffset)
{
    /// <summary>Where the cells go.</summary>
    public RangeAddress Destination => Offset(Source);

    /// <summary>Whether the cells go to another sheet.</summary>
    public bool Across => From != To;

    /// <summary>A range moved by the offset.</summary>
    public RangeAddress Offset(RangeAddress range) =>
        new(range.First.Row + RowOffset, range.First.Column + ColumnOffset, range.Last.Row + RowOffset, range.Last.Column + ColumnOffset);

    /// <summary>A cell moved by the offset.</summary>
    public CellAddress Offset(CellAddress cell) => new(cell.Row + RowOffset, cell.Column + ColumnOffset);

    /// <summary>Whether a range of <see cref="To"/> is all in what the paste writes over and does not move away first.</summary>
    public bool Overwrites(RangeAddress range) => Destination.Contains(range) && (Across || !Source.Intersects(range));
}

/// <summary>
/// Moves the references of a formula's text as Excel does when cells are cut and pasted: a reference to cells that all move
/// follows them, one to cells the paste writes over becomes <c>#REF!</c>, and a range with an edge in the cut grows or
/// shrinks as Excel's does. The rest of the text, and references that do not change, stay as they were written.
/// </summary>
internal static class ReferenceMove
{
    /// <summary>The formula's text with its references moved; the text itself when none moves or it does not parse.</summary>
    /// <param name="formula">The formula's text, without its leading <c>=</c>.</param>
    /// <param name="origin">The cell the formula is read for; relative references are resolved against it.</param>
    /// <param name="home">The sheet a reference without a sheet means, or null when such a reference means no sheet.</param>
    /// <param name="move">The move.</param>
    /// <param name="relocated">
    /// Whether the formula itself goes to the other sheet, so that a reference without a sheet to a cell that stays gets the
    /// name of its sheet, as in Excel.
    /// </param>
    /// <param name="name">Whether the text is a defined name's, which may be a union without parentheses.</param>
    public static string Apply(string formula, CellAddress origin, Worksheet? home, CellMove move, bool relocated = false, bool name = false)
    {
        if ((!relocated && !MayHoldReference(formula)) || FormulaParser.TryParse(formula, r1c1: false, origin) is not { } parsed)
        {
            if (!name || !formula.Contains(',', StringComparison.Ordinal) || Apply("(" + formula + ")", origin, home, move, relocated) is not { } wrapped
                || wrapped.Length < 2 || wrapped[0] != '(' || wrapped[^1] != ')')
            {
                return formula;
            }

            return wrapped == "(" + formula + ")" ? formula : wrapped[1..^1];
        }

        bool changed = false;
        ReferenceNode? seen = null;
        Area? area = null;
        string? Prefixed(ReferenceNode reference)
        {
            seen = reference;
            area = null;
            Worksheet? sheet = Means(reference.Prefix, home, move.From.Workbook);
            if (sheet is null)
            {
                return null;
            }

            if (reference.Area.Kind is not (AreaKind.Cell or AreaKind.Range))
            {
                // Whole rows and columns stay where they are.
                changed |= relocated && reference.Prefix is null && sheet == move.From;
                return relocated && reference.Prefix is null && sheet == move.From ? SheetNames.Quoted(move.From.Name) + "!" : null;
            }

            (Area? to, bool across) = Moved(reference.Area, sheet, move);
            area = to;
            changed |= to is not null;
            if (across)
            {
                return SheetNames.Quoted(move.To.Name) + "!";
            }

            if (relocated && reference.Prefix is null && sheet == move.From && to is not { Kind: AreaKind.Error })
            {
                changed = true;
                return SheetNames.Quoted(move.From.Name) + "!";
            }

            return null;
        }

        Area? MovedArea(ReferenceNode reference) => reference == seen ? area : null;

        string text = FormulaPrinter.Print(parsed, MovedArea, Prefixed);
        return changed ? text : formula;
    }

    /// <summary>
    /// Where a reference's area goes: null when it stays, an area of <see cref="AreaKind.Error"/> when the paste writes over
    /// it; and whether it goes to the other sheet.
    /// </summary>
    internal static (Area? Area, bool Across) Moved(Area area, Worksheet sheet, CellMove move)
    {
        if (area.FirstRow > area.LastRow || area.FirstColumn > area.LastColumn)
        {
            return (null, false);
        }

        RangeAddress range = new(area.FirstRow, area.FirstColumn, area.LastRow, area.LastColumn);
        if (sheet == move.From && move.Source.Contains(range))
        {
            return (With(area, move.Offset(range)), move.Across);
        }

        if (sheet == move.From && move.Across)
        {
            return (move.Source.Intersects(range) && Trimmed(range, move.Source) is { } rest ? With(area, rest) : null, false);
        }

        if (sheet == move.From && Slid(range, move) is { } slid)
        {
            return (slid.Gone ? area with { Kind = AreaKind.Error } : With(area, slid.Range), false);
        }

        // A range wider than the band that moves loses a strip the paste writes over, unless the strip's outer edge is the
        // paste's own edge, as along the move.
        RangeAddress destination = move.Destination;
        if (sheet == move.From && !move.Source.Intersects(range) && destination.Intersects(range) && !destination.Contains(range)
            && !Slides(range, move) && Trimmed(range, destination) is { } kept && !OnEdge(range, kept, destination))
        {
            return (With(area, kept), false);
        }

        if (sheet == move.To && move.Overwrites(range))
        {
            return (area with { Kind = AreaKind.Error }, false);
        }

        return (null, false);
    }

    /// <summary>
    /// A range less a strip of the cut along one of its edges, all the way across it, as Excel leaves a range whose cells go
    /// to another sheet; null when the cut is not such a strip.
    /// </summary>
    private static RangeAddress? Trimmed(RangeAddress range, RangeAddress source)
    {
        int firstRow = Math.Max(range.First.Row, source.First.Row);
        int lastRow = Math.Min(range.Last.Row, source.Last.Row);
        int firstColumn = Math.Max(range.First.Column, source.First.Column);
        int lastColumn = Math.Min(range.Last.Column, source.Last.Column);
        if (firstColumn == range.First.Column && lastColumn == range.Last.Column)
        {
            if (firstRow == range.First.Row)
            {
                return new RangeAddress(lastRow + 1, range.First.Column, range.Last.Row, range.Last.Column);
            }

            if (lastRow == range.Last.Row)
            {
                return new RangeAddress(range.First.Row, range.First.Column, firstRow - 1, range.Last.Column);
            }
        }
        else if (firstRow == range.First.Row && lastRow == range.Last.Row)
        {
            if (firstColumn == range.First.Column)
            {
                return new RangeAddress(range.First.Row, lastColumn + 1, range.Last.Row, range.Last.Column);
            }

            if (lastColumn == range.Last.Column)
            {
                return new RangeAddress(range.First.Row, range.First.Column, range.Last.Row, firstColumn - 1);
            }
        }

        return null;
    }

    /// <summary>
    /// Where a range of the sheet goes when cells move straight along its rows or columns on that sheet and the range lies
    /// within the moved band across the move; null when it stays or the move is not such a move.
    /// </summary>
    private static (RangeAddress Range, bool Gone)? Slid(RangeAddress range, CellMove move)
    {
        RangeAddress source = move.Source;
        if (!Slides(range, move))
        {
            return null;
        }

        if (move.ColumnOffset == 0)
        {
            return Along(range.First.Row, range.Last.Row, source.First.Row, source.Last.Row, move.RowOffset) is { } rows
                ? (new RangeAddress(rows.First, range.First.Column, rows.Last, range.Last.Column), rows.Gone)
                : null;
        }

        return Along(range.First.Column, range.Last.Column, source.First.Column, source.Last.Column, move.ColumnOffset) is { } columns
            ? (new RangeAddress(range.First.Row, columns.First, range.Last.Row, columns.Last), columns.Gone)
            : null;
    }

    /// <summary>Whether the strip a range lost to <paramref name="kept"/> ends on the same edge of the paste as the range does.</summary>
    private static bool OnEdge(RangeAddress range, RangeAddress kept, RangeAddress destination) =>
        (kept.First.Row > range.First.Row && range.First.Row == destination.First.Row)
        || (kept.Last.Row < range.Last.Row && range.Last.Row == destination.Last.Row)
        || (kept.First.Column > range.First.Column && range.First.Column == destination.First.Column)
        || (kept.Last.Column < range.Last.Column && range.Last.Column == destination.Last.Column);

    /// <summary>Whether cells move straight along a range's rows or columns on its sheet, with the range within the band that moves.</summary>
    private static bool Slides(RangeAddress range, CellMove move) =>
        !move.Across && ((move.ColumnOffset == 0 && move.RowOffset != 0
            && move.Source.First.Column <= range.First.Column && range.Last.Column <= move.Source.Last.Column)
        || (move.RowOffset == 0 && move.ColumnOffset != 0
            && move.Source.First.Row <= range.First.Row && range.Last.Row <= move.Source.Last.Row));

    /// <summary>
    /// Excel's rule along the move for a range <paramref name="first"/> to <paramref name="last"/> when the cut
    /// <paramref name="from"/> to <paramref name="to"/> goes <paramref name="offset"/> on: an end in the cut goes with it, an
    /// end written over pulls back to the cells left, unless it is the end of what is written over; when that turns the range
    /// round, or one end goes and the other is written over, the range spans the cells left of it outside the cut and where
    /// its cut cells land. Null when it stays; Gone when all of it is written over.
    /// </summary>
    private static (int First, int Last, bool Gone)? Along(int first, int last, int from, int to, int offset)
    {
        int at = from + offset, end = to + offset;
        bool cut = first <= to && last >= from;
        if (!cut && at <= first && last <= end)
        {
            return (first, last, true);
        }

        bool firstCut = from <= first && first <= to, lastCut = from <= last && last <= to;
        bool firstOver = at <= first && first <= end, lastOver = at <= last && last <= end;
        int newFirst = firstCut ? first + offset : firstOver ? (first == at ? first : end + 1) : first;
        int newLast = lastCut ? last + offset : lastOver ? (last == end ? last : at - 1) : last;
        bool mixed = (firstCut && lastOver && !lastCut) || (lastCut && firstOver && !firstCut);
        if (newFirst <= newLast && !mixed)
        {
            return newFirst == first && newLast == last ? null : (newFirst, newLast, false);
        }

        bool over = first <= end && last >= at;
        if (!cut)
        {
            return over ? (first, last, true) : null;
        }

        if (!over)
        {
            return null;
        }

        // The cells outside the cut, and where the cut cells land.
        int low = Math.Min(first < from ? first : to + 1, Math.Max(first, from) + offset);
        int high = Math.Max(last > to ? last : from - 1, Math.Min(last, to) + offset);
        return (low, high, false);
    }

    private static Area With(Area area, RangeAddress range) =>
        area with { FirstRow = range.First.Row, FirstColumn = range.First.Column, LastRow = range.Last.Row, LastColumn = range.Last.Column };

    /// <summary>The sheet of the book a reference with this prefix means, or null for another book, sheets or a sheet the book lacks.</summary>
    private static Worksheet? Means(SheetPrefix? prefix, Worksheet? home, Workbook book)
    {
        if (prefix is null)
        {
            return home;
        }

        if (prefix.Book is not null || prefix.FirstSheet is not { } first
            || (prefix.LastSheet is not null && !string.Equals(prefix.LastSheet, first, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return book.Worksheets.FirstOrDefault(s => string.Equals(s.Name, first, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether text could hold a cell reference: they all have a digit.</summary>
    private static bool MayHoldReference(string text) => text.AsSpan().IndexOfAnyInRange('0', '9') >= 0;
}
