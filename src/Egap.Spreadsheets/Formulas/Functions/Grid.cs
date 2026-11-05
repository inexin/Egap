namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// A rectangle of values a function walks: one area of cells, an array or a single value. Cells are read as they are needed,
/// so a whole column costs only the cells that hold something.
/// </summary>
internal readonly struct Grid
{
    private readonly SheetArea area;
    private readonly EvalArray? array;
    private readonly EvalValue scalar;
    private readonly bool isArea;

    private Grid(SheetArea area)
    {
        this.area = area;
        isArea = true;
        Rows = area.Range.RowCount;
        Columns = area.Range.ColumnCount;
    }

    private Grid(EvalArray array)
    {
        this.array = array;
        Rows = array.Rows;
        Columns = array.Columns;
    }

    private Grid(EvalValue scalar)
    {
        this.scalar = scalar;
        Rows = 1;
        Columns = 1;
    }

    public int Rows { get; }

    public int Columns { get; }

    public long Count => (long)Rows * Columns;

    /// <summary>The area when the grid is cells of a sheet.</summary>
    public SheetArea? Area => isArea ? area : null;

    /// <summary>The grid of an argument; null for a reference of more than one area.</summary>
    public static Grid? Of(in EvalValue value) => value.Kind switch
    {
        EvalKind.Reference => value.Areas.Length == 1 ? new Grid(value.Areas[0]) : null,
        EvalKind.Array => new Grid(value.Array),
        _ => new Grid(value),
    };

    /// <summary>The value at a row and column counted from the grid's first cell; blank past its end.</summary>
    public EvalValue this[int row, int column]
    {
        get
        {
            if (isArea)
            {
                int r = area.Range.First.Row + row, c = area.Range.First.Column + column;
                return r < CellAddress.MaxRows && c < CellAddress.MaxColumns ? EvalValue.FromCell(area.Sheet.GetValue(r, c)) : EvalValue.Blank;
            }

            if (array is not null)
            {
                return row < Rows && column < Columns ? array[row, column] : EvalValue.Blank;
            }

            return row == 0 && column == 0 ? scalar : EvalValue.Blank;
        }
    }

    /// <summary>The same first cell, stretched or cut to a size, as SUMIF lines its sum range up with the criteria range.</summary>
    public Grid Resize(int rows, int columns)
    {
        if (!isArea)
        {
            return this;
        }

        CellAddress first = area.Range.First;
        int lastRow = Math.Min(first.Row + rows - 1, CellAddress.MaxRows - 1);
        int lastColumn = Math.Min(first.Column + columns - 1, CellAddress.MaxColumns - 1);
        return new Grid(new SheetArea(area.Sheet, new RangeAddress(first.Row, first.Column, lastRow, lastColumn)));
    }

    /// <summary>The positions that may hold a value, row by row; a sheet area gives only the cells in use.</summary>
    public IEnumerable<(int Row, int Column)> Occupied()
    {
        if (isArea)
        {
            CellAddress first = area.Range.First;
            foreach ((int row, int column) in area.Sheet.Store.Occupied(area.Range))
            {
                yield return (row - first.Row, column - first.Column);
            }

            yield break;
        }

        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                yield return (row, column);
            }
        }
    }
}
