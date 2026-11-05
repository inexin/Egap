using System.Collections;

namespace Egap.Spreadsheets;

/// <summary>
/// A rectangle of cells on a sheet, such as <c>sheet.Range("A1:D20")</c>: a cheap value that reads and writes the sheet's
/// cells. Enumerating it gives every cell in it, empty ones too, row by row; <see cref="UsedCells"/> gives only the cells
/// that hold something.
/// </summary>
public readonly struct CellRange : IEnumerable<Cell>, IEquatable<CellRange>
{
    private readonly Worksheet sheet;

    internal CellRange(Worksheet sheet, RangeAddress address)
    {
        this.sheet = sheet;
        Address = address;
    }

    /// <summary>The sheet the range is on.</summary>
    public Worksheet Worksheet => sheet;

    /// <summary>The range's address, such as <c>A1:D20</c>.</summary>
    public RangeAddress Address { get; }

    /// <summary>The number of rows.</summary>
    public int RowCount => Address.RowCount;

    /// <summary>The number of columns.</summary>
    public int ColumnCount => Address.ColumnCount;

    /// <summary>The cell at a zero-based row and column within the range: <c>[0, 0]</c> is the top-left cell.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the range.</exception>
    public Cell this[int row, int column]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)row, (uint)RowCount, nameof(row));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)column, (uint)ColumnCount, nameof(column));
            return new Cell(sheet, Address.First.Row + row, Address.First.Column + column);
        }
    }

    /// <summary>The cells in the range that hold a value, a formula or a style, row by row.</summary>
    public IEnumerable<Cell> UsedCells
    {
        get
        {
            Worksheet owner = sheet;
            return owner.Store.Occupied(Address).Select(cell => new Cell(owner, cell.Row, cell.Column));
        }
    }

    /// <summary>
    /// Makes the range an array formula, as Ctrl+Shift+Enter does in Excel: one formula, stored in the range's first cell,
    /// that works on arrays and gives each cell of the range one element of its result. A result smaller than the range
    /// gives #N/A past its end, and a single row or column is repeated across it. The cells' values are cleared until the
    /// workbook is calculated; their styles stay. Setting another formula or value in the first cell ends the array formula.
    /// </summary>
    /// <param name="formula">The formula, in Excel's stored form; a leading <c>=</c> is dropped.</param>
    /// <exception cref="ArgumentNullException">The formula is null.</exception>
    /// <exception cref="ArgumentException">The formula is empty or longer than 8,192 characters.</exception>
    /// <exception cref="FormulaException">The formula does not follow Excel's grammar.</exception>
    /// <example>
    /// <code>
    /// sheet.Range("C1:C3").SetArrayFormula("A1:A3*B1:B3");
    /// </code>
    /// </example>
    public void SetArrayFormula(string formula)
    {
        ArgumentNullException.ThrowIfNull(formula);
        string text = formula.StartsWith('=') ? formula[1..] : formula;
        ArgumentException.ThrowIfNullOrEmpty(text, nameof(formula));
        if (text.Length > 8192)
        {
            throw new ArgumentException("A formula is at most 8,192 characters.", nameof(formula));
        }

        CellAddress first = Address.First;
        Formulas.FormulaParser.Parse(text, r1c1: false, first);
        Worksheet owner = sheet;
        ForEach((row, column) => owner.SetValue(row, column, CellValue.Empty), occupiedOnly: true);
        owner.SetFormula(first.Row, first.Column, text, CellValue.Empty,
            [new System.Xml.Linq.XAttribute("t", "array"), new System.Xml.Linq.XAttribute("ref", Address.ToString())]);
    }

    /// <summary>Sets every cell in the range to one value, keeping their styles.</summary>
    public void SetValue(CellValue value)
    {
        Worksheet owner = sheet;
        ForEach((row, column) => owner.SetValue(row, column, value), value.IsEmpty);
    }

    /// <summary>
    /// The values of the range, row by row, as a <c>[row, column]</c> array; formula cells give their last results.
    /// </summary>
    /// <exception cref="InvalidOperationException">The range has more than 16,777,216 cells.</exception>
    public CellValue[,] GetValues()
    {
        CheckSize();
        CellValue[,] values = new CellValue[RowCount, ColumnCount];
        foreach ((int row, int column) in sheet.Store.Occupied(Address))
        {
            values[row - Address.First.Row, column - Address.First.Column] = sheet.GetValue(row, column);
        }

        return values;
    }

    /// <summary>
    /// Sets the cells from a <c>[row, column]</c> array, starting at the range's top-left cell; the array may be smaller than
    /// the range but not larger.
    /// </summary>
    /// <exception cref="ArgumentException">The array is larger than the range.</exception>
    public void SetValues(CellValue[,] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.GetLength(0) > RowCount || values.GetLength(1) > ColumnCount)
        {
            throw new ArgumentException($"The array is {values.GetLength(0)} by {values.GetLength(1)}; the range is {RowCount} by {ColumnCount}.", nameof(values));
        }

        for (int r = 0; r < values.GetLength(0); r++)
        {
            for (int c = 0; c < values.GetLength(1); c++)
            {
                sheet.SetValue(Address.First.Row + r, Address.First.Column + c, values[r, c]);
            }
        }
    }

    /// <summary>Gives every cell in the range one style, keeping their values.</summary>
    public void SetStyle(CellStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        int index = sheet.Workbook.Styles.Add(style);
        Worksheet owner = sheet;
        ForEach((row, column) => owner.SetStyleIndex(row, column, index), index == 0);
    }

    /// <summary>
    /// Changes the style of every cell in the range with a function of the style it has, such as
    /// <c>s =&gt; s with { Font = s.Font with { Bold = true } }</c>. The function runs once for each distinct style in the range.
    /// </summary>
    public void UpdateStyle(Func<CellStyle, CellStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Dictionary<int, int> changed = [];
        Worksheet owner = sheet;
        Storage.StyleTable styles = sheet.Workbook.Styles;
        ForEach((row, column) =>
        {
            int from = owner.GetStyleIndex(row, column);
            if (!changed.TryGetValue(from, out int to))
            {
                to = styles.Add(change(styles[from]));
                changed.Add(from, to);
            }

            owner.SetStyleIndex(row, column, to);
        }, false);
    }

    /// <summary>Removes the values, formulas and styles of every cell in the range.</summary>
    public void Clear()
    {
        Worksheet owner = sheet;
        foreach ((int row, int column) in sheet.Store.Occupied(Address).ToList())
        {
            owner.Clear(row, column);
        }
    }

    /// <summary>Removes the values and formulas of every cell in the range, keeping their styles.</summary>
    public void ClearContents()
    {
        Worksheet owner = sheet;
        foreach ((int row, int column) in sheet.Store.Occupied(Address).ToList())
        {
            owner.SetValue(row, column, CellValue.Empty);
        }
    }

    /// <summary>Every cell in the range, empty ones too, row by row and left to right.</summary>
    public IEnumerator<Cell> GetEnumerator()
    {
        for (int row = Address.First.Row; row <= Address.Last.Row; row++)
        {
            for (int column = Address.First.Column; column <= Address.Last.Column; column++)
            {
                yield return new Cell(sheet, row, column);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Whether both ranges are the same cells of the same sheet.</summary>
    public bool Equals(CellRange other) => ReferenceEquals(sheet, other.sheet) && Address == other.Address;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CellRange other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(sheet, Address);

    /// <summary>Whether both ranges are the same cells.</summary>
    public static bool operator ==(CellRange left, CellRange right) => left.Equals(right);

    /// <summary>Whether the ranges differ.</summary>
    public static bool operator !=(CellRange left, CellRange right) => !left.Equals(right);

    /// <summary>The sheet's name and the range's address, such as <c>Sheet1!A1:D20</c>.</summary>
    public override string ToString() => $"{sheet.Name}!{Address}";

    /// <summary>
    /// Runs <paramref name="action"/> on the rows and columns of the range's cells: only on the cells that hold something when
    /// <paramref name="occupiedOnly"/> is set (an empty value or the default style changes nothing elsewhere), else on all.
    /// </summary>
    private void ForEach(Action<int, int> action, bool occupiedOnly)
    {
        if (occupiedOnly)
        {
            foreach ((int row, int column) in sheet.Store.Occupied(Address).ToList())
            {
                action(row, column);
            }

            return;
        }

        for (int row = Address.First.Row; row <= Address.Last.Row; row++)
        {
            for (int column = Address.First.Column; column <= Address.Last.Column; column++)
            {
                action(row, column);
            }
        }
    }

    private void CheckSize()
    {
        if (Address.CellCount > 1 << 24)
        {
            throw new InvalidOperationException($"The range {Address} has {Address.CellCount:N0} cells, more than an array of values takes.");
        }
    }
}
