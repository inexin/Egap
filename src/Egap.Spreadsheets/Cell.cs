namespace Egap.Spreadsheets;

/// <summary>
/// A cell of a sheet: a small handle that reads and writes the sheet's store, made by <c>sheet["B4"]</c> or
/// <c>sheet.Cells[row, column]</c>. The sheet keeps no handles; a cell with no value and the default style takes no room.
/// Two handles for the same cell are equal. For bulk work, <see cref="WorksheetCells.GetValue"/> and
/// <see cref="WorksheetCells.SetValue"/> read and write values without a handle.
/// </summary>
public sealed class Cell : IEquatable<Cell>
{
    private readonly Worksheet sheet;

    internal Cell(Worksheet sheet, int row, int column)
    {
        this.sheet = sheet;
        Row = row;
        Column = column;
    }

    /// <summary>The sheet the cell is on.</summary>
    public Worksheet Worksheet => sheet;

    /// <summary>The zero-based row: 0 is row 1.</summary>
    public int Row { get; }

    /// <summary>The zero-based column: 0 is column A.</summary>
    public int Column { get; }

    /// <summary>The cell's address, such as <c>B4</c>.</summary>
    public CellAddress Address => new(Row, Column);

    /// <summary>
    /// The cell's value: a number, text, TRUE or FALSE, an error, or empty. A formula cell gives the result it last had.
    /// Setting a value replaces any formula and keeps the cell's style.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A number set is NaN or infinite.</exception>
    /// <exception cref="ArgumentException">Text set is longer than 32,767 characters.</exception>
    public CellValue Value
    {
        get => sheet.GetValue(Row, Column);
        set => sheet.SetValue(Row, Column, value);
    }

    /// <summary>What kind of value the cell holds; for a formula, the kind of its last result.</summary>
    public CellValueKind ValueKind => sheet.GetKind(Row, Column);

    /// <summary>
    /// The cell's formula in Excel's stored form, English function names and commas, without the leading <c>=</c>; null
    /// when the cell holds none. A leading <c>=</c> given is dropped. Setting a formula keeps the cell's style; its result
    /// is empty until the workbook is calculated. Setting null or an empty string removes the formula and its result. A cell
    /// that shares the formula of another cell (a shared formula in an XLSX file) gives null here but has
    /// <see cref="HasFormula"/> set; its formula and result are kept and saved.
    /// </summary>
    /// <exception cref="ArgumentException">The formula is longer than 8,192 characters.</exception>
    public string? Formula
    {
        get => sheet.GetFormula(Row, Column);
        set => sheet.SetFormula(Row, Column, value, CellValue.Empty);
    }

    /// <summary>Whether the cell holds a formula.</summary>
    public bool HasFormula => sheet.HasFormula(Row, Column);

    /// <summary>Whether the cell holds no value and no formula; it may still have a style.</summary>
    public bool IsEmpty => sheet.GetKind(Row, Column) == CellValueKind.Empty && !HasFormula;

    /// <summary>The cell's style. Setting one keeps the value; the workbook keeps each distinct style once.</summary>
    /// <exception cref="ArgumentNullException">The style set is null.</exception>
    public CellStyle Style
    {
        get => sheet.Workbook.Styles[sheet.GetStyleIndex(Row, Column)];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            sheet.SetStyleIndex(Row, Column, sheet.Workbook.Styles.Add(value));
        }
    }

    /// <summary>
    /// The cell's number format code, such as <c>0.00</c> or <c>yyyy-mm-dd</c>; the same as <c>Style.NumberFormat</c>.
    /// Setting it changes only the number format of the cell's style.
    /// </summary>
    public string NumberFormat
    {
        get => Style.NumberFormat;
        set => Style = Style with { NumberFormat = value };
    }

    /// <summary>
    /// The value as Excel shows it in the cell, through the cell's number format and the workbook's
    /// <see cref="Workbook.Culture"/>: <c>1,234.50</c> for 1234.5 with <c>#,##0.00</c>, <c>2024-03-15</c> for a date. A formula
    /// shows its last calculated value. The text is not cut to the column's width, as Excel's <c>###</c> is.
    /// </summary>
    public string Text => NumberFormatCode.Get(NumberFormat).Format(Value, sheet.Workbook.Culture);

    /// <summary>Sets the cell's value to a number; the same as setting <see cref="Value"/>, without building a <see cref="CellValue"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The number is NaN or infinite.</exception>
    public void SetNumber(double number)
    {
        if (!double.IsFinite(number))
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "A cell holds finite numbers only.");
        }

        sheet.SetNumber(Row, Column, number == 0 ? 0 : number);
    }

    /// <summary>Changes the cell's style with a function of the style it has, such as <c>s =&gt; s with { NumberFormat = "0.00" }</c>.</summary>
    public void UpdateStyle(Func<CellStyle, CellStyle> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Style = change(Style);
    }

    /// <summary>Removes the cell's value, formula and style, so it takes no room.</summary>
    public void Clear() => sheet.Clear(Row, Column);

    /// <summary>Whether both handles are for the same cell of the same sheet.</summary>
    public bool Equals(Cell? other) => other is not null && ReferenceEquals(sheet, other.sheet) && Row == other.Row && Column == other.Column;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Cell other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(sheet, Row, Column);

    /// <summary>Whether both handles are for the same cell.</summary>
    public static bool operator ==(Cell? left, Cell? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Whether the handles are for different cells.</summary>
    public static bool operator !=(Cell? left, Cell? right) => !(left == right);

    /// <summary>The cell's address and value, such as <c>B4: 42</c>.</summary>
    public override string ToString() => $"{Address}: {Value}";
}
