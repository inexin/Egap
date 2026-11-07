namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// Rows or columns put into or taken out of a sheet: <see cref="Count"/> of them from <see cref="At"/>, zero-based.
/// </summary>
internal readonly record struct SheetEdit(bool Rows, int At, int Count, bool Insert)
{
    /// <summary>The last row or column a delete takes out.</summary>
    public int End => At + Count - 1;

    /// <summary>How many rows or columns a sheet has in the edit's direction.</summary>
    public int Limit => Rows ? CellAddress.MaxRows : CellAddress.MaxColumns;

    /// <summary>
    /// Where a row or column goes, or -1 when a delete takes it out or an insert pushes it off the sheet.
    /// </summary>
    public int Moved(int index)
    {
        if (Insert)
        {
            int moved = index >= At ? index + Count : index;
            return moved < Limit ? moved : -1;
        }

        return index < At ? index : index > End ? index - Count : -1;
    }

    /// <summary>
    /// Where a stretch of rows or columns goes, as Excel moves a reference: an insert inside it widens it, a delete narrows
    /// it, and one that takes all of it gives null. A stretch an insert pushes past the sheet's end is cut at the end; one
    /// that starts past it gives null.
    /// </summary>
    public (int First, int Last)? Moved(int first, int last)
    {
        if (Insert)
        {
            int f = first >= At ? first + Count : first;
            int l = last >= At ? last + Count : last;
            return f < Limit ? (f, Math.Min(l, Limit - 1)) : null;
        }

        if (first >= At && last <= End)
        {
            return null;
        }

        // A stretch to the sheet's end keeps its end, as the end gets new empty rows or columns.
        int end = last == Limit - 1 ? last : last < At ? last : last > End ? last - Count : At - 1;
        return (first < At ? first : first > End ? first - Count : At, end);
    }

    /// <summary>Where a range goes, or null when the edit takes all of it.</summary>
    public RangeAddress? Moved(RangeAddress range)
    {
        if (Rows)
        {
            return Moved(range.First.Row, range.Last.Row) is { } rows ? new RangeAddress(rows.First, range.First.Column, rows.Last, range.Last.Column) : null;
        }

        return Moved(range.First.Column, range.Last.Column) is { } columns ? new RangeAddress(range.First.Row, columns.First, range.Last.Row, columns.Last) : null;
    }

    /// <summary>Where a cell goes, or null when the edit takes it out or pushes it off the sheet.</summary>
    public CellAddress? Moved(CellAddress cell)
    {
        int moved = Moved(Rows ? cell.Row : cell.Column);
        return moved < 0 ? null : Rows ? new CellAddress(moved, cell.Column) : new CellAddress(cell.Row, moved);
    }

    /// <summary>
    /// Where a cell that moved here was before the edit: for a cell an insert made, the first row or column after the new
    /// ones.
    /// </summary>
    public CellAddress Before(CellAddress cell)
    {
        int index = Rows ? cell.Row : cell.Column;
        int before = Insert ? (index >= At + Count ? index - Count : Math.Min(index, At)) : index >= At ? index + Count : index;
        return Rows ? new CellAddress(before, cell.Column) : new CellAddress(cell.Row, before);
    }
}

/// <summary>
/// Moves the references of a formula's text as Excel does when rows or columns are put into or taken out of a sheet: a
/// reference to a cell that moves follows it, a range widens or narrows, and a reference to what is gone becomes
/// <c>#REF!</c>. The rest of the text, and references that do not change, stay as they were written.
/// </summary>
internal static class ReferenceShift
{
    /// <summary>
    /// The formula's text with its references to <paramref name="edited"/> moved; the text itself when none moves or it does
    /// not parse.
    /// </summary>
    /// <param name="formula">The formula's text, without its leading <c>=</c>.</param>
    /// <param name="origin">The cell the formula is read for, before the edit; relative references are resolved against it.</param>
    /// <param name="home">The sheet a reference without a sheet means, or null when such a reference never means the edited one.</param>
    /// <param name="edited">The sheet the rows or columns go into or out of.</param>
    /// <param name="edit">The edit.</param>
    /// <param name="lost">
    /// The tables the edit takes columns from, by name, with the names of the columns it takes; a table with null loses all
    /// of itself. Structured references to what is lost become <c>#REF!</c>.
    /// </param>
    public static string Apply(string formula, CellAddress origin, Worksheet? home, Worksheet edited, SheetEdit edit, Dictionary<string, HashSet<string>?>? lost = null)
    {
        bool mayLose = lost is { Count: > 0 };
        if ((!mayLose && !MayHoldReference(formula)) || FormulaParser.TryParse(formula, r1c1: false, origin) is not { } parsed)
        {
            return formula;
        }

        bool changed = false;
        Area? Moved(ReferenceNode reference)
        {
            Area? area = Means(reference.Prefix, home, edited) ? Shift(reference.Area, edit) : null;
            changed |= area is not null;
            return area;
        }

        bool Gone(FormulaNode node)
        {
            bool gone = Lost(node, origin, home, lost!);
            changed |= gone;
            return gone;
        }

        string text = FormulaPrinter.Print(parsed, Moved, mayLose ? Gone : null);
        return changed ? text : formula;
    }

    /// <summary>Whether a table name or structured reference names a table or column the edit takes out.</summary>
    private static bool Lost(FormulaNode node, CellAddress origin, Worksheet? home, Dictionary<string, HashSet<string>?> lost)
    {
        switch (node)
        {
            case NameNode { Prefix: null } name:
                return lost.TryGetValue(name.Name, out HashSet<string>? columns) && columns is null;
            case StructuredNode structured:
                string? table = structured.Table ?? home?.Tables.Find(origin)?.Name;
                if (table is null || !lost.TryGetValue(table, out HashSet<string>? taken))
                {
                    return false;
                }

                return taken is null || (structured.FirstColumn is { } first && taken.Contains(first)) || (structured.LastColumn is { } last && taken.Contains(last));
            default:
                return false;
        }
    }

    /// <summary>Whether text could hold a cell, row or column reference: they all have a digit or a colon.</summary>
    private static bool MayHoldReference(string text) => text.AsSpan().IndexOfAny(ReferenceChars) >= 0;

    private static readonly System.Buffers.SearchValues<char> ReferenceChars = System.Buffers.SearchValues.Create("0123456789:");

    /// <summary>Whether a reference with this prefix is to the edited sheet.</summary>
    private static bool Means(SheetPrefix? prefix, Worksheet? home, Worksheet edited)
    {
        if (prefix is null)
        {
            return home == edited;
        }

        // A reference to other books, or across sheets, stays: Excel moves those only when every sheet is edited.
        return prefix.Book is null && prefix.FirstSheet is { } first && (prefix.LastSheet is null || string.Equals(prefix.LastSheet, first, StringComparison.OrdinalIgnoreCase))
            && string.Equals(first, edited.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where a reference's area goes: null when it stays, an area of <see cref="AreaKind.Error"/> when it is gone.</summary>
    internal static Area? Shift(Area area, SheetEdit edit)
    {
        if (area.Kind == AreaKind.Error || (edit.Rows ? area.Kind == AreaKind.Columns : area.Kind == AreaKind.Rows))
        {
            return null;
        }

        int first = edit.Rows ? area.FirstRow : area.FirstColumn;
        int last = edit.Rows ? area.LastRow : area.LastColumn;
        AreaFlags flags = area.Flags;
        if (first > last)
        {
            (first, last) = (last, first);
            flags = Swapped(flags, edit.Rows);
        }

        (int First, int Last)? moved = area.Kind == AreaKind.Cell
            ? edit.Moved(first) is var cell and >= 0 ? (cell, cell) : null
            : edit.Moved(first, last);
        if (moved is not { } to)
        {
            return area with { Kind = AreaKind.Error };
        }

        if (to.First == first && to.Last == last && flags == area.Flags)
        {
            return null;
        }

        return edit.Rows
            ? area with { FirstRow = to.First, LastRow = to.Last, Flags = flags }
            : area with { FirstColumn = to.First, LastColumn = to.Last, Flags = flags };
    }

    /// <summary>The flags with the first and last row (or column) swapped, for a range written last corner first.</summary>
    private static AreaFlags Swapped(AreaFlags flags, bool rows)
    {
        AreaFlags first = rows ? AreaFlags.FirstRowAbsolute : AreaFlags.FirstColumnAbsolute;
        AreaFlags last = rows ? AreaFlags.LastRowAbsolute : AreaFlags.LastColumnAbsolute;
        AreaFlags rest = flags & ~(first | last);
        return rest | ((flags & first) != 0 ? last : 0) | ((flags & last) != 0 ? first : 0);
    }
}
