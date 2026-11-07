using System.Xml.Linq;

using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets;

/// <summary>Putting rows and columns into a sheet and taking them out, with every reference to the cells that move fixed.</summary>
public sealed partial class Worksheet
{
    /// <summary>
    /// Puts empty rows in before a row, as Excel's Insert does: the cells from that row down move down, and every formula,
    /// name, rule, link, table, merge, note, drawing and chart of the workbook that refers to them follows. A range the new
    /// rows fall inside grows. The new rows take the formatting of the row above.
    /// </summary>
    /// <param name="row">The zero-based row the new rows go before.</param>
    /// <param name="count">How many rows to put in.</param>
    /// <exception cref="ArgumentOutOfRangeException">The row is off the sheet, or the count is below 1 or more than the sheet has room for.</exception>
    /// <exception cref="InvalidOperationException">
    /// Cells with a value or style would be pushed off the end of the sheet, the new rows would split an array formula, or a
    /// pivot table would move.
    /// </exception>
    /// <remarks>Sheet protection does not block the edit: it guards what a person does in Excel, not what code does, as for writing cells.</remarks>
    public void InsertRows(int row, int count = 1) => Edit(new SheetEdit(true, row, count, true), nameof(row), nameof(count));

    /// <summary>
    /// Takes rows out, as Excel's Delete does: their cells go, the cells below move up, and whatever refers to the cells that
    /// move follows. A reference to a cell that goes becomes <c>#REF!</c>, and a range loses the rows it had among them.
    /// </summary>
    /// <param name="row">The zero-based first row to take out.</param>
    /// <param name="count">How many rows to take out.</param>
    /// <exception cref="ArgumentOutOfRangeException">The row is off the sheet, or the count is below 1 or runs past the sheet's end.</exception>
    /// <exception cref="InvalidOperationException">The rows would take part of an array formula or a table's header row but not all of the table, or a pivot table would move or change.</exception>
    /// <remarks>Sheet protection does not block the edit: it guards what a person does in Excel, not what code does, as for writing cells.</remarks>
    public void DeleteRows(int row, int count = 1) => Edit(new SheetEdit(true, row, count, false), nameof(row), nameof(count));

    /// <summary>
    /// Puts empty columns in before a column, as <see cref="InsertRows"/> does rows. Columns put in inside a table become
    /// columns of the table, named <c>Column1</c>, <c>Column2</c> and so on. The new columns take the formatting of the column
    /// to their left.
    /// </summary>
    /// <param name="column">The zero-based column the new columns go before.</param>
    /// <param name="count">How many columns to put in.</param>
    /// <exception cref="ArgumentOutOfRangeException">The column is off the sheet, or the count is below 1 or more than the sheet has room for.</exception>
    /// <exception cref="InvalidOperationException">
    /// Cells with a value or style would be pushed off the end of the sheet, the new columns would split an array formula,
    /// or a pivot table would move.
    /// </exception>
    /// <remarks>Sheet protection does not block the edit: it guards what a person does in Excel, not what code does, as for writing cells.</remarks>
    public void InsertColumns(int column, int count = 1) => Edit(new SheetEdit(false, column, count, true), nameof(column), nameof(count));

    /// <summary>Puts empty columns in before a column given by its name, such as <c>C</c>; see <see cref="InsertColumns(int, int)"/>.</summary>
    /// <exception cref="FormatException">The text is not a column name.</exception>
    public void InsertColumns(string column, int count = 1) => InsertColumns(ColumnIndex(column), count);

    /// <summary>
    /// Takes columns out, as <see cref="DeleteRows"/> does rows. A table loses its columns among them, and a structured
    /// reference to one of those becomes <c>#REF!</c>.
    /// </summary>
    /// <param name="column">The zero-based first column to take out.</param>
    /// <param name="count">How many columns to take out.</param>
    /// <exception cref="ArgumentOutOfRangeException">The column is off the sheet, or the count is below 1 or runs past the sheet's end.</exception>
    /// <exception cref="InvalidOperationException">The columns would take part of an array formula, or a pivot table would move or change.</exception>
    /// <remarks>Sheet protection does not block the edit: it guards what a person does in Excel, not what code does, as for writing cells.</remarks>
    public void DeleteColumns(int column, int count = 1) => Edit(new SheetEdit(false, column, count, false), nameof(column), nameof(count));

    /// <summary>Takes columns out from a column given by its name, such as <c>C</c>; see <see cref="DeleteColumns(int, int)"/>.</summary>
    /// <exception cref="FormatException">The text is not a column name.</exception>
    public void DeleteColumns(string column, int count = 1) => DeleteColumns(ColumnIndex(column), count);

    private static int ColumnIndex(string column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return CellAddress.ColumnIndex(column);
    }

    /// <summary>The cells of the rows or columns from <paramref name="first"/> to <paramref name="last"/>.</summary>
    private static RangeAddress Band(SheetEdit edit, int first, int last) => edit.Rows
        ? new RangeAddress(first, 0, last, CellAddress.MaxColumns - 1)
        : new RangeAddress(0, first, CellAddress.MaxRows - 1, last);

    private void Edit(SheetEdit edit, string parameter, string countParameter)
    {
        if (edit.At < 0 || edit.At >= edit.Limit)
        {
            throw new ArgumentOutOfRangeException(parameter, edit.At, edit.Rows ? "The row is off the sheet." : "The column is off the sheet.");
        }

        if (edit.Count < 1 || edit.At + edit.Count > edit.Limit)
        {
            throw new ArgumentOutOfRangeException(countParameter, edit.Count, "The count is below 1 or runs past the sheet's end.");
        }

        Workbook book = Workbook;
        Check(edit);

        // Each cell gets its own formula, as the cells of a shared formula need not move alike.
        UnshareAll(false);
        foreach (Worksheet other in book.Worksheets)
        {
            if (other != this)
            {
                other.UnshareAll(true);
            }
        }

        Dictionary<string, HashSet<string>?> lost = LostTables(edit);
        book.CellsMoving(this, edit, lost);
        MoveCells(edit);
        MoveFormulaRanges(edit);
        MoveRanges(edit);
        MoveTables(edit);
        MoveObjects(edit);
        MoveKept(edit);
        if (edit.Insert && edit.At > 0)
        {
            CopyFormatting(edit);
        }

        sharedMasters = null;
    }

    /// <summary>Refuses an edit Excel refuses.</summary>
    private void Check(SheetEdit edit)
    {
        if (edit.Insert && store.Occupied(Band(edit, edit.Limit - edit.Count, edit.Limit - 1)).Any())
        {
            throw new InvalidOperationException("Cells with a value or style would be pushed off the end of the sheet.");
        }

        foreach (FormulaEntry entry in formulas.Entries)
        {
            if (entry.Text.Length > 0 && entry.Attribute("t") is "array" or "dataTable" && !IsDynamic(entry)
                && RangeAddress.TryParse(entry.Attribute("ref"), out RangeAddress range) && Splits(edit, range))
            {
                throw new InvalidOperationException("Part of an array formula can't be changed.");
            }
        }

        // Egap keeps pivot tables as they were read, so one the edit would move or change would be left in the wrong place.
        // Excel refuses an edit through a pivot table too.
        foreach (RangeAddress pivot in Xlsx.SheetObjectReader.PivotTables(this))
        {
            if ((edit.Rows ? pivot.Last.Row : pivot.Last.Column) >= edit.At)
            {
                throw new InvalidOperationException($"The edit would move or change the pivot table at {pivot}, which Egap does not change.");
            }
        }

        if (!edit.Insert && edit.Rows)
        {
            foreach (SheetTable table in Tables)
            {
                int header = table.Range.First.Row;
                if (table.ShowHeaderRow && header >= edit.At && header <= edit.End && edit.Moved(table.Range) is not null)
                {
                    throw new InvalidOperationException($"The rows take the header row of the table {table.Name} but not all of the table.");
                }
            }
        }
    }

    /// <summary>Whether an edit would change part of a range and not all of it.</summary>
    private static bool Splits(SheetEdit edit, RangeAddress range)
    {
        int first = edit.Rows ? range.First.Row : range.First.Column;
        int last = edit.Rows ? range.Last.Row : range.Last.Column;
        return edit.Insert
            ? first < edit.At && edit.At <= last
            : first <= edit.End && last >= edit.At && !(first >= edit.At && last <= edit.End);
    }

    /// <summary>
    /// Gives each cell of the sheet's shared formulas its own formula; with <paramref name="named"/>, only those whose
    /// formula names a sheet.
    /// </summary>
    private void UnshareAll(bool named)
    {
        foreach ((int index, FormulaEntry entry) in formulas.Indexed().ToList())
        {
            if (entry.Text.Length > 0 && entry.SharedIndex is not null && (!named || entry.Text.Contains('!', StringComparison.Ordinal))
                && FindFormula(index, entry) is { } cell)
            {
                Unshare(cell.Row, cell.Column, index);
                entry.Attributes = Without(entry.Attributes, "t", "ref", "si");
            }
        }

        sharedMasters = null;
    }

    private static XAttribute[]? Without(XAttribute[]? attributes, params string[] names)
    {
        XAttribute[] kept = [.. (attributes ?? []).Where(a => !names.Contains(a.Name.LocalName))];
        return kept.Length == 0 ? null : kept;
    }

    /// <summary>Calls <paramref name="rewrite"/> with the text and cell of each formula of the sheet, and keeps the text it gives.</summary>
    internal void RewriteCellFormulas(Func<string, CellAddress, string> rewrite)
    {
        if (formulas.Count == 0 || store.Bounds() is not { } bounds)
        {
            return;
        }

        foreach ((int row, int column) in store.Occupied(bounds))
        {
            ref readonly CellRecord record = ref store.Get(row, column);
            if (record.Kind == CellKind.Formula && formulas[(int)record.Payload] is { Text.Length: > 0 } entry)
            {
                entry.Text = rewrite(entry.Text, new CellAddress(row, column));
            }
        }
    }

    /// <summary>The tables the edit takes columns from, with the names of those columns, or null for a table it takes all of.</summary>
    private Dictionary<string, HashSet<string>?> LostTables(SheetEdit edit)
    {
        Dictionary<string, HashSet<string>?> lost = new(StringComparer.OrdinalIgnoreCase);
        if (edit.Insert)
        {
            return lost;
        }

        foreach (SheetTable table in Tables)
        {
            if (edit.Moved(table.Range) is null)
            {
                lost[table.Name] = null;
            }
            else if (!edit.Rows)
            {
                HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    int column = table.Range.First.Column + i;
                    if (column >= edit.At && column <= edit.End)
                    {
                        columns.Add(table.Columns[i].Name);
                    }
                }

                if (columns.Count > 0)
                {
                    lost[table.Name] = columns;
                }
            }
        }

        return lost;
    }

    /// <summary>Takes out the cells an edit deletes and moves the cells after them, with their file extras and row or column settings.</summary>
    private void MoveCells(SheetEdit edit)
    {
        if (!edit.Insert)
        {
            foreach ((int row, int column) in store.Occupied(Band(edit, edit.At, edit.End)).ToList())
            {
                ref readonly CellRecord old = ref store.Get(row, column);
                if (old.Kind == CellKind.Formula)
                {
                    spills.Remove(formulas[(int)old.Payload]);
                    formulas.Remove((int)old.Payload);
                }

                store.Set(row, column, default);
            }
        }

        int from = edit.Insert ? edit.At : edit.End + 1;
        if (from < edit.Limit)
        {
            List<(int Row, int Column, CellRecord Record)> moving = [];
            foreach ((int row, int column) in store.Occupied(Band(edit, from, edit.Limit - 1)))
            {
                moving.Add((row, column, store.Get(row, column)));
            }

            foreach ((int row, int column, _) in moving)
            {
                store.Set(row, column, default);
            }

            foreach ((int row, int column, CellRecord record) in moving)
            {
                CellAddress to = edit.Moved(new CellAddress(row, column))!.Value;
                store.Set(to.Row, to.Column, record);
            }
        }

        if (CellExtras.Count > 0)
        {
            List<(long Key, XElement Extra)> extras = [.. CellExtras.Select(p => (p.Key, p.Value))];
            CellExtras.Clear();
            foreach ((long key, XElement extra) in extras)
            {
                if (edit.Moved(new CellAddress((int)(key >> 14), (int)(key & 0x3FFF))) is { } to)
                {
                    CellExtras[Key(to.Row, to.Column)] = extra;
                }
            }
        }

        if (edit.Rows)
        {
            List<(int Row, RowRecord Record)> rows = [.. RowRecords.Select(p => (p.Key, p.Value))];
            RowRecords.Clear();
            foreach ((int row, RowRecord record) in rows)
            {
                if (edit.Moved(row) is var to and >= 0)
                {
                    RowRecords[to] = record;
                }
            }
        }
        else
        {
            List<ColumnSpan> spans = ColumnSpans.Spans;
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                if (edit.Moved(spans[i].First, spans[i].Last) is { } to)
                {
                    (spans[i].First, spans[i].Last) = to;
                }
                else
                {
                    spans.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>Moves the ranges of the sheet's array, data table and dynamic array formulas, and a data table's input cells.</summary>
    private void MoveFormulaRanges(SheetEdit edit)
    {
        foreach (FormulaEntry entry in formulas.Entries)
        {
            if (entry.Attributes is not { } attributes)
            {
                continue;
            }

            foreach (XAttribute attribute in attributes)
            {
                string name = attribute.Name.LocalName;
                if (name == "ref" && RangeAddress.TryParse(attribute.Value, out RangeAddress range))
                {
                    attribute.Value = (edit.Moved(range) ?? range).ToString();
                }
                else if (name is "r1" or "r2" && CellAddress.TryParse(attribute.Value, out CellAddress cell))
                {
                    attribute.Value = edit.Moved(cell) is { } to ? to.ToString() : "#REF!";
                }
            }
        }
    }

    /// <summary>Moves the merges, links, rules, page breaks and frozen panes.</summary>
    private void MoveRanges(SheetEdit edit)
    {
        List<RangeAddress> merges = [.. MergedCells];
        MergedCells.Clear();
        foreach (RangeAddress merge in merges)
        {
            if (edit.Moved(merge) is { } moved && moved.First != moved.Last)
            {
                MergedCells.AddRead(moved);
            }
        }

        foreach (Hyperlink link in Hyperlinks.ToList())
        {
            if (edit.Moved(link.Range) is { } moved)
            {
                link.Range = moved;
            }
            else
            {
                Hyperlinks.Remove(link);
            }
        }

        foreach (DataValidation rule in DataValidations.ToList())
        {
            if (!MoveAll(rule.Ranges, edit))
            {
                DataValidations.Remove(rule);
            }
        }

        foreach (ConditionalFormat format in ConditionalFormats.ToList())
        {
            if (!MoveAll(format.Ranges, edit))
            {
                ConditionalFormats.Remove(format);
            }
        }

        PageBreakCollection breaks = edit.Rows ? PageSetup.RowBreaks : PageSetup.ColumnBreaks;
        List<int> old = [.. breaks];
        breaks.Clear();
        foreach (int place in old)
        {
            if (edit.Moved(place) is var to and > 0)
            {
                breaks.Add(to);
            }
        }

        int frozen = edit.Rows ? View.FrozenRows : View.FrozenColumns;
        if (edit.At < frozen)
        {
            frozen = edit.Insert ? Math.Min(frozen + edit.Count, edit.Limit - 1) : frozen - (Math.Min(edit.End + 1, frozen) - edit.At);
            if (edit.Rows)
            {
                View.FrozenRows = frozen;
            }
            else
            {
                View.FrozenColumns = frozen;
            }
        }
    }

    /// <summary>Moves each range of a list, dropping those the edit takes; false when none is left.</summary>
    private static bool MoveAll(List<RangeAddress> ranges, SheetEdit edit)
    {
        for (int i = ranges.Count - 1; i >= 0; i--)
        {
            if (edit.Moved(ranges[i]) is { } moved)
            {
                ranges[i] = moved;
            }
            else
            {
                ranges.RemoveAt(i);
            }
        }

        return ranges.Count > 0;
    }

    /// <summary>Moves the tables: a table the edit takes goes, and columns put in or taken out inside a table are its columns.</summary>
    private void MoveTables(SheetEdit edit)
    {
        foreach (SheetTable table in Tables.ToList())
        {
            RangeAddress range = table.Range;
            if (edit.Moved(range) is not { } moved)
            {
                Tables.Remove(table);
                continue;
            }

            if (edit.Rows)
            {
                if (!edit.Insert && table.ShowTotalsRow && range.Last.Row >= edit.At && range.Last.Row <= edit.End)
                {
                    table.ShowTotalsRow = false;
                }

                table.Range = moved;
                continue;
            }

            int first = range.First.Column;
            if (edit.Insert && first < edit.At && edit.At <= range.Last.Column)
            {
                for (int i = 0; i < edit.Count; i++)
                {
                    int n = 1;
                    while (table.Columns.Exists(c => string.Equals(c.Name, "Column" + n, StringComparison.OrdinalIgnoreCase)))
                    {
                        n++;
                    }

                    table.Columns.Insert(edit.At - first + i, new SheetTableColumn("Column" + n));
                }
            }
            else if (!edit.Insert)
            {
                for (int i = table.Columns.Count - 1; i >= 0; i--)
                {
                    if (first + i >= edit.At && first + i <= edit.End)
                    {
                        table.Columns.RemoveAt(i);
                    }
                }
            }

            table.Range = moved;
            if (table.Columns.Count != moved.ColumnCount || edit.Insert)
            {
                table.SyncColumns();
            }
        }
    }

    /// <summary>Moves the notes and the drawing layer's objects; a note on a cell the edit takes goes.</summary>
    private void MoveObjects(SheetEdit edit)
    {
        foreach (CellComment comment in Comments.ToList())
        {
            if (edit.Moved(comment.Cell) is { } to)
            {
                if (to != comment.Cell)
                {
                    comment.Cell = to;
                    Comments.Changed = true;
                }
            }
            else
            {
                Comments.Remove(comment);
            }
        }

        foreach (SheetDrawing drawing in Drawings)
        {
            if (drawing.AnchorKind == DrawingAnchorKind.Absolute)
            {
                continue;
            }

            DrawingPosition from = Moved(drawing.From, edit);
            DrawingPosition to = drawing.AnchorKind == DrawingAnchorKind.TwoCell ? Moved(drawing.To, edit) : drawing.To;
            if (from != drawing.From || to != drawing.To)
            {
                drawing.From = from;
                drawing.To = to;
                Drawings.Changed = true;
            }
        }
    }

    /// <summary>Where a drawing's corner goes: with its cell, or to the start of what a delete takes when it takes the cell.</summary>
    private static DrawingPosition Moved(DrawingPosition position, SheetEdit edit)
    {
        if (edit.Moved(position.Cell) is { } cell)
        {
            return position with { Cell = cell };
        }

        return edit.Rows
            ? new DrawingPosition(new CellAddress(edit.At, position.Cell.Column), position.OffsetX, 0)
            : new DrawingPosition(new CellAddress(position.Cell.Row, edit.At), 0, position.OffsetY);
    }

    /// <summary>Moves the ranges in what the sheet's part held that the model does not, such as its filter and sort.</summary>
    private void MoveKept(SheetEdit edit)
    {
        if (Source is not { } source)
        {
            return;
        }

        for (int i = source.Kept.Count - 1; i >= 0; i--)
        {
            XElement kept = source.Kept[i].Element;
            foreach (XElement element in kept.DescendantsAndSelf().ToList())
            {
                foreach (XAttribute attribute in element.Attributes().Where(a => a.Name.LocalName is "ref" or "sqref").ToList())
                {
                    if (MovedList(attribute.Value, edit) is { } moved)
                    {
                        attribute.Value = moved;
                    }
                    else if (element == kept)
                    {
                        source.Kept.RemoveAt(i);
                    }
                    else
                    {
                        element.Remove();
                    }
                }

                if (element.Name.LocalName == "sqref" && !element.HasElements && element.Parent is not null)
                {
                    if (MovedList(element.Value, edit) is { } moved)
                    {
                        element.Value = moved;
                    }
                    else
                    {
                        element.Parent.Remove();
                    }
                }
            }
        }
    }

    /// <summary>A space-separated list of ranges moved, or null when the edit takes all of them; text that is no range list stays.</summary>
    private static string? MovedList(string text, SheetEdit edit)
    {
        string[] items = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> moved = [];
        foreach (string item in items)
        {
            if (!RangeAddress.TryParse(item, out RangeAddress range))
            {
                return text;
            }

            if (edit.Moved(range) is { } to)
            {
                moved.Add(to.ToString());
            }
        }

        return moved.Count == 0 && items.Length > 0 ? null : string.Join(' ', moved);
    }

    /// <summary>Gives the new rows the formatting of the row above, or the new columns that of the column to the left.</summary>
    private void CopyFormatting(SheetEdit edit)
    {
        int source = edit.At - 1;
        if (edit.Rows)
        {
            if (RowRecords.TryGetValue(source, out RowRecord? above) && above.Style >= 0)
            {
                for (int row = edit.At; row < edit.At + edit.Count; row++)
                {
                    RowRecords[row] = new RowRecord { Style = above.Style };
                }
            }
        }
        else if (ColumnSpans.Find(source) is { } left && left.Style >= 0)
        {
            for (int column = edit.At; column < edit.At + edit.Count; column++)
            {
                ColumnSpans.Change(column, span => span.Style = left.Style);
            }
        }

        foreach ((int row, int column) in store.Occupied(Band(edit, source, source)).ToList())
        {
            int style = store.Get(row, column).Style;
            if (style == 0)
            {
                continue;
            }

            for (int i = 0; i < edit.Count; i++)
            {
                int r = edit.Rows ? edit.At + i : row;
                int c = edit.Rows ? column : edit.At + i;
                store.Set(r, c, new CellRecord { Style = style });
            }
        }
    }
}
