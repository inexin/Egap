using System.Collections;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>What a sheet keeps about one row: its height, visibility, outline and style. Null when the row has none of these.</summary>
internal sealed class RowRecord
{
    /// <summary>The height in points, or NaN for the sheet's default.</summary>
    public double Height = double.NaN;

    /// <summary>The height Excel worked out from the row's text and saved, or NaN: what the row prints at unless it has its own.</summary>
    public double FittedHeight = double.NaN;

    public bool Hidden;
    public int OutlineLevel;
    public bool Collapsed;

    /// <summary>The style index the row gives its empty cells, or -1 for none.</summary>
    public int Style = -1;

    /// <summary>Attributes of the file's row element this model does not hold, written back as they were.</summary>
    public XAttribute[]? Extra;

    public bool IsDefault => double.IsNaN(Height) && !Hidden && OutlineLevel == 0 && !Collapsed && Style < 0 && Extra is null;
}

/// <summary>A sheet's rows by zero-based index, and, when enumerated, the rows that have a height, are hidden, outlined or styled.</summary>
public sealed class SheetRowCollection : IEnumerable<SheetRow>
{
    private readonly Worksheet sheet;

    internal SheetRowCollection(Worksheet sheet)
    {
        this.sheet = sheet;
    }

    /// <summary>The row at a zero-based index: <c>Rows[0]</c> is row 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is off the sheet.</exception>
    public SheetRow this[int index]
    {
        get
        {
            CellAddress.CheckRow(index);
            return new SheetRow(sheet, index);
        }
    }

    /// <summary>The rows that have a height, are hidden, outlined or styled, top to bottom.</summary>
    public IEnumerator<SheetRow> GetEnumerator()
    {
        foreach (int index in sheet.RowRecords.Keys.Order().ToList())
        {
            yield return new SheetRow(sheet, index);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One row of a sheet: its height, whether it is hidden, its outline level and the style of its empty cells.</summary>
public sealed class SheetRow
{
    private readonly Worksheet sheet;

    internal SheetRow(Worksheet sheet, int index)
    {
        this.sheet = sheet;
        Index = index;
    }

    /// <summary>The zero-based index: 0 is row 1.</summary>
    public int Index { get; }

    /// <summary>The height in points, from 0 to 409, or null for the sheet's default height.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is outside 0 to 409.</exception>
    public double? Height
    {
        get => sheet.RowRecords.TryGetValue(Index, out RowRecord? row) && !double.IsNaN(row.Height) ? row.Height : null;
        set
        {
            if (value is { } height && !(height is >= 0 and <= 409))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A row height is from 0 to 409 points.");
            }

            Change(row => row.Height = value ?? double.NaN);
        }
    }

    /// <summary>Whether the row is hidden.</summary>
    public bool Hidden
    {
        get => sheet.RowRecords.TryGetValue(Index, out RowRecord? row) && row.Hidden;
        set => Change(row => row.Hidden = value);
    }

    /// <summary>The outline (grouping) level, from 0 for none to 7.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0 to 7.</exception>
    public int OutlineLevel
    {
        get => sheet.RowRecords.TryGetValue(Index, out RowRecord? row) ? row.OutlineLevel : 0;
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)value, 7u, nameof(value));
            Change(row => row.OutlineLevel = value);
        }
    }

    /// <summary>Whether the outline group this row closes is collapsed.</summary>
    public bool Collapsed
    {
        get => sheet.RowRecords.TryGetValue(Index, out RowRecord? row) && row.Collapsed;
        set => Change(row => row.Collapsed = value);
    }

    /// <summary>The style the row gives its empty cells, or null for none. Cells keep their own styles.</summary>
    public CellStyle? Style
    {
        get => sheet.RowRecords.TryGetValue(Index, out RowRecord? row) && row.Style >= 0 ? sheet.Workbook.Styles[row.Style] : null;
        set
        {
            int index = value is null ? -1 : sheet.Workbook.Styles.Add(value);
            Change(row => row.Style = index);
        }
    }

    private void Change(Action<RowRecord> change)
    {
        if (!sheet.RowRecords.TryGetValue(Index, out RowRecord? row))
        {
            row = new RowRecord();
            sheet.RowRecords[Index] = row;
        }

        change(row);
        if (row.IsDefault)
        {
            sheet.RowRecords.Remove(Index);
        }
    }
}
