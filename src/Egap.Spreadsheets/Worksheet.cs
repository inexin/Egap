using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets;

/// <summary>
/// One sheet of a workbook: up to 1,048,576 rows by 16,384 columns of cells, stored only where cells hold a value or a
/// style. Reach a cell by its A1 address, <c>sheet["B4"]</c>, or by zero-based row and column, <c>sheet.Cells[3, 1]</c>,
/// and a rectangle of cells with <c>sheet.Range("A1:D20")</c>.
/// </summary>
public sealed class Worksheet
{
    private readonly CellStore store = new();
    private readonly FormulaTable formulas = new();
    private Workbook? workbook;

    internal Worksheet(Workbook workbook, string name)
    {
        this.workbook = workbook;
        Name = name;
        Cells = new WorksheetCells(this);
    }

    /// <summary>The workbook the sheet is in.</summary>
    /// <exception cref="InvalidOperationException">The sheet was removed from its workbook.</exception>
    public Workbook Workbook => workbook ?? throw new InvalidOperationException("The sheet was removed from its workbook.");

    /// <summary>
    /// The sheet's name on its tab: 1 to 31 characters, none of <c>: \ / ? * [ ]</c>, no apostrophe at either end, and unique
    /// in the workbook, case ignored.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not one Excel allows, or another sheet has it.</exception>
    public string Name
    {
        get;
        set
        {
            workbook?.Worksheets.CheckName(value, this);
            field = value;
        }
    }

    /// <summary>The sheet's tab position in its workbook.</summary>
    public int Index => Workbook.Worksheets.IndexOf(this);

    /// <summary>The cells by zero-based row and column, and every cell that holds a value or a style.</summary>
    public WorksheetCells Cells { get; }

    /// <summary>The cell at an A1 address such as <c>B4</c>.</summary>
    /// <exception cref="FormatException">The text is not a cell reference.</exception>
    public Cell this[string address] => this[CellAddress.Parse(address)];

    /// <summary>The cell at an address.</summary>
    public Cell this[CellAddress address] => new(this, address.Row, address.Column);

    /// <summary>The cell at a zero-based row and column.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The row or column is off the sheet.</exception>
    public Cell this[int row, int column]
    {
        get
        {
            CellAddress.CheckRow(row);
            CellAddress.CheckColumn(column);
            return new Cell(this, row, column);
        }
    }

    /// <summary>
    /// The smallest range that holds every cell with a value, a formula or a style, as SpreadsheetML's sheet dimension; null
    /// when the sheet has none. It is worked out from the cells each time.
    /// </summary>
    public CellRange? UsedRange => store.Bounds() is { } bounds ? new CellRange(this, bounds) : null;

    /// <summary>How many 32-by-32 blocks of cells the sheet holds.</summary>
    internal int BlockCount => store.BlockCount;

    internal CellStore Store => store;

    internal FormulaTable Formulas => formulas;

    /// <summary>A range from an A1 reference such as <c>A1:D20</c>, <c>B4</c>, <c>A:C</c> or <c>2:5</c>.</summary>
    /// <exception cref="FormatException">The text is not a range.</exception>
    public CellRange Range(string address) => new(this, RangeAddress.Parse(address));

    /// <summary>A range from its address.</summary>
    public CellRange Range(RangeAddress address) => new(this, address);

    /// <summary>A range from zero-based corner rows and columns, in any order.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A row or column is off the sheet.</exception>
    public CellRange Range(int firstRow, int firstColumn, int lastRow, int lastColumn) =>
        new(this, new RangeAddress(firstRow, firstColumn, lastRow, lastColumn));

    /// <summary>The sheet's name.</summary>
    public override string ToString() => Name;

    internal void Detach() => workbook = null;

    internal CellValue GetValue(int row, int column)
    {
        ref readonly CellRecord record = ref store.Get(row, column);
        return record.Kind switch
        {
            CellKind.Number => CellValue.NumberUnchecked(BitConverter.Int64BitsToDouble(record.Payload)),
            CellKind.SharedString => CellValue.TextUnchecked(Workbook.SharedStrings[(int)record.Payload]),
            CellKind.Boolean => CellValue.FromBoolean(record.Payload != 0),
            CellKind.Error => CellValue.FromError((CellError)record.Payload),
            CellKind.Formula => formulas[(int)record.Payload].Result,
            _ => CellValue.Empty,
        };
    }

    internal void SetValue(int row, int column, CellValue value)
    {
        ref readonly CellRecord old = ref store.Get(row, column);
        CellRecord record = new() { Style = old.Style };
        if (old.Kind == CellKind.Formula)
        {
            formulas.Remove((int)old.Payload);
        }

        switch (value.Kind)
        {
            case CellValueKind.Number:
                record.Kind = CellKind.Number;
                record.Payload = BitConverter.DoubleToInt64Bits(value.Number);
                break;
            case CellValueKind.Text:
                record.Kind = CellKind.SharedString;
                record.Payload = Workbook.SharedStrings.Add(value.Text);
                break;
            case CellValueKind.Boolean:
                record.Kind = CellKind.Boolean;
                record.Payload = value.Boolean ? 1 : 0;
                break;
            case CellValueKind.Error:
                record.Kind = CellKind.Error;
                record.Payload = (long)value.Error;
                break;
        }

        store.Set(row, column, record);
    }

    /// <summary>Stores a number without the checks <see cref="CellValue"/> makes; the fast path for <see cref="Cell.SetNumber"/>.</summary>
    internal void SetNumber(int row, int column, double number)
    {
        ref readonly CellRecord old = ref store.Get(row, column);
        if (old.Kind == CellKind.Formula)
        {
            formulas.Remove((int)old.Payload);
        }

        store.Set(row, column, new CellRecord { Kind = CellKind.Number, Payload = BitConverter.DoubleToInt64Bits(number), Style = old.Style });
    }

    internal string? GetFormula(int row, int column)
    {
        ref readonly CellRecord record = ref store.Get(row, column);
        return record.Kind == CellKind.Formula ? formulas[(int)record.Payload].Text : null;
    }

    internal void SetFormula(int row, int column, string? formula, CellValue result)
    {
        if (string.IsNullOrEmpty(formula) || formula == "=")
        {
            if (store.Get(row, column).Kind == CellKind.Formula)
            {
                SetValue(row, column, CellValue.Empty);
            }

            return;
        }

        string text = formula[0] == '=' ? formula[1..] : formula;
        if (text.Length > 8192)
        {
            throw new ArgumentException("A formula is at most 8,192 characters.", nameof(formula));
        }

        ref readonly CellRecord old = ref store.Get(row, column);
        if (old.Kind == CellKind.Formula)
        {
            FormulaEntry entry = formulas[(int)old.Payload];
            entry.Text = text;
            entry.Result = result;
            return;
        }

        int style = old.Style;
        store.Set(row, column, new CellRecord { Kind = CellKind.Formula, Payload = formulas.Add(text, result), Style = style });
    }

    internal int GetStyleIndex(int row, int column) => store.Get(row, column).Style;

    internal void SetStyleIndex(int row, int column, int style)
    {
        CellRecord record = store.Get(row, column);
        if (record.Style != style)
        {
            record.Style = style;
            store.Set(row, column, record);
        }
    }

    internal void Clear(int row, int column)
    {
        ref readonly CellRecord old = ref store.Get(row, column);
        if (old.Kind == CellKind.Formula)
        {
            formulas.Remove((int)old.Payload);
        }

        store.Set(row, column, default);
    }

    internal CellValueKind GetKind(int row, int column)
    {
        ref readonly CellRecord record = ref store.Get(row, column);
        return record.Kind switch
        {
            CellKind.Number => CellValueKind.Number,
            CellKind.SharedString => CellValueKind.Text,
            CellKind.Boolean => CellValueKind.Boolean,
            CellKind.Error => CellValueKind.Error,
            CellKind.Formula => formulas[(int)record.Payload].Result.Kind,
            _ => CellValueKind.Empty,
        };
    }
}
