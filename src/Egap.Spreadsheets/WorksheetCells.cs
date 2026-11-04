using System.Collections;

namespace Egap.Spreadsheets;

/// <summary>
/// A sheet's cells: any cell by zero-based row and column, and, when enumerated, the cells that hold a value, a formula or a
/// style, row by row and left to right.
/// </summary>
public sealed class WorksheetCells : IEnumerable<Cell>
{
    private readonly Worksheet sheet;

    internal WorksheetCells(Worksheet sheet)
    {
        this.sheet = sheet;
    }

    /// <summary>How many cells hold a value, a formula or a style.</summary>
    public long Count => sheet.Store.Count;

    /// <summary>The cell at a zero-based row and column: <c>Cells[3, 1]</c> is B4.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The row or column is off the sheet.</exception>
    public Cell this[int row, int column] => sheet[row, column];

    /// <summary>The cell at an address.</summary>
    public Cell this[CellAddress address] => sheet[address];

    /// <summary>The value of the cell at a zero-based row and column, without making a <see cref="Cell"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The row or column is off the sheet.</exception>
    public CellValue GetValue(int row, int column)
    {
        CellAddress.CheckRow(row);
        CellAddress.CheckColumn(column);
        return sheet.GetValue(row, column);
    }

    /// <summary>
    /// Sets the value of the cell at a zero-based row and column, without making a <see cref="Cell"/>; the same as setting
    /// <see cref="Cell.Value"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The row or column is off the sheet.</exception>
    public void SetValue(int row, int column, CellValue value)
    {
        CellAddress.CheckRow(row);
        CellAddress.CheckColumn(column);
        sheet.SetValue(row, column, value);
    }

    /// <summary>The cells that hold a value, a formula or a style, row by row; cells may be changed while enumerating.</summary>
    public IEnumerator<Cell> GetEnumerator()
    {
        foreach ((int row, int column) in sheet.Store.Occupied(new RangeAddress(0, 0, CellAddress.MaxRows - 1, CellAddress.MaxColumns - 1)))
        {
            yield return new Cell(sheet, row, column);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
