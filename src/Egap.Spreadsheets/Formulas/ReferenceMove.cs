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
        if (sheet == move.From && move.Source.Intersects(range))
        {
            RangeAddress source = move.Source;
            if (source.Contains(range))
            {
                return (With(area, move.Offset(range)), move.Across);
            }

            return (Edge(range, move) is { } moved ? With(area, moved) : null, false);
        }

        if (sheet == move.To && move.Overwrites(range))
        {
            return (area with { Kind = AreaKind.Error }, false);
        }

        return (null, false);
    }

    /// <summary>
    /// Where a range goes that has only a strip along one edge in the cut, all the way across it: as Excel moves it, it loses
    /// the strip when the cells go to another sheet; on the same sheet, when the strip goes straight on, away from the rest of
    /// the range or onto it, the range stretches over the rest and the strip where it lands. Null when it stays.
    /// </summary>
    private static RangeAddress? Edge(RangeAddress range, CellMove move)
    {
        RangeAddress source = move.Source;
        int firstRow = Math.Max(range.First.Row, source.First.Row);
        int lastRow = Math.Min(range.Last.Row, source.Last.Row);
        int firstColumn = Math.Max(range.First.Column, source.First.Column);
        int lastColumn = Math.Min(range.Last.Column, source.Last.Column);
        RangeAddress strip = new(firstRow, firstColumn, lastRow, lastColumn);

        bool across = firstColumn == range.First.Column && lastColumn == range.Last.Column;
        bool down = firstRow == range.First.Row && lastRow == range.Last.Row;
        RangeAddress rest;
        bool outward;
        if (across && (firstRow == range.First.Row || lastRow == range.Last.Row))
        {
            bool top = firstRow == range.First.Row;
            rest = top
                ? new RangeAddress(lastRow + 1, range.First.Column, range.Last.Row, range.Last.Column)
                : new RangeAddress(range.First.Row, range.First.Column, firstRow - 1, range.Last.Column);
            if (move.Across)
            {
                return rest;
            }

            if (move.ColumnOffset != 0)
            {
                return null;
            }

            outward = top ? move.RowOffset < 0 : move.RowOffset > 0;
        }
        else if (down && (firstColumn == range.First.Column || lastColumn == range.Last.Column))
        {
            bool left = firstColumn == range.First.Column;
            rest = left
                ? new RangeAddress(range.First.Row, lastColumn + 1, range.Last.Row, range.Last.Column)
                : new RangeAddress(range.First.Row, range.First.Column, range.Last.Row, firstColumn - 1);
            if (move.Across)
            {
                return rest;
            }

            if (move.RowOffset != 0)
            {
                return null;
            }

            outward = left ? move.ColumnOffset < 0 : move.ColumnOffset > 0;
        }
        else
        {
            return null;
        }

        RangeAddress landed = move.Offset(strip);
        if (!outward && !landed.Intersects(rest))
        {
            return null;
        }

        return new RangeAddress(
            Math.Min(rest.First.Row, landed.First.Row), Math.Min(rest.First.Column, landed.First.Column),
            Math.Max(rest.Last.Row, landed.Last.Row), Math.Max(rest.Last.Column, landed.Last.Column));
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
