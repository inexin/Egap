using System.Xml.Linq;

using Egap.Spreadsheets.Formulas;
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
    private readonly bool linked;
    private Workbook? workbook;

    // The first cells of the sheet's shared formulas by shared index, found when first asked for; null when stale.
    private Dictionary<string, (CellAddress Cell, string Text)>? sharedMasters;

    /// <summary>A sheet of the workbook, or with <paramref name="linked"/> one holding a linked book's kept values, which is not on a tab and may have any name.</summary>
    internal Worksheet(Workbook workbook, string name, bool linked = false)
    {
        this.linked = linked;
        this.workbook = workbook;
        Name = name;
        Cells = new WorksheetCells(this);
        Rows = new SheetRowCollection(this);
        Columns = new SheetColumnCollection(this);
        Tables = new SheetTableCollection(this);
        PageSetup = new PageSetup(this);
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
            if (!linked)
            {
                workbook?.Worksheets.CheckName(value, this);
            }
            field = value;
        }
    }

    /// <summary>The sheet's tab position in its workbook.</summary>
    public int Index => Workbook.Worksheets.IndexOf(this);

    /// <summary>The cells by zero-based row and column, and every cell that holds a value or a style.</summary>
    public WorksheetCells Cells { get; }

    /// <summary>The rows by zero-based index: height, hidden, outline level and style.</summary>
    public SheetRowCollection Rows { get; }

    /// <summary>The columns by zero-based index or A1 name: width, hidden, outline level and style.</summary>
    public SheetColumnCollection Columns { get; }

    /// <summary>The merged ranges.</summary>
    public MergedCellCollection MergedCells { get; } = new();

    /// <summary>The hyperlinks on cells and ranges.</summary>
    public HyperlinkCollection Hyperlinks { get; } = new();

    /// <summary>The data validation rules: what cells allow, drop-down lists and input messages.</summary>
    public DataValidationCollection DataValidations { get; } = new();

    /// <summary>The conditional formatting.</summary>
    public ConditionalFormatCollection ConditionalFormats { get; } = new();

    /// <summary>The Excel tables on the sheet.</summary>
    public SheetTableCollection Tables { get; }

    /// <summary>The notes on cells.</summary>
    public CellCommentCollection Comments { get; } = new();

    /// <summary>The pictures, shapes, charts and other objects on the sheet's drawing layer.</summary>
    public SheetDrawingCollection Drawings { get; } = new();

    /// <summary>How the sheet shows on screen: gridlines, zoom, frozen panes and so on.</summary>
    public SheetView View { get; } = new();

    /// <summary>How the sheet prints: paper, orientation, scaling, margins, header and footer.</summary>
    public PageSetup PageSetup { get; }

    /// <summary>Whether the sheet's tab shows, is hidden, or is hidden so that only code can show it again.</summary>
    public SheetVisibility Visibility { get; set; }

    /// <summary>The colour of the sheet's tab, or null for none.</summary>
    public CellColor? TabColor { get; set; }

    /// <summary>The height in points of rows that set none, or null for Excel's default from the default font (15 for Calibri 11).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is outside 0 to 409.</exception>
    public double? DefaultRowHeight
    {
        get;
        set
        {
            field = value is null or (>= 0 and <= 409) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A row height is from 0 to 409 points.");
            DefaultRowFollowsFont = false;
        }
    }

    /// <summary>
    /// Whether the file gave <see cref="DefaultRowHeight"/> without marking it custom: Excel then takes the default from the
    /// normal font, whatever the file says.
    /// </summary>
    internal bool DefaultRowFollowsFont { get; set; }

    /// <summary>The width of columns that set none, in characters as <see cref="SheetColumn.Width"/>, or null for Excel's default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is outside 0 to 255.</exception>
    public double? DefaultColumnWidth
    {
        get;
        set => field = value is null or (>= 0 and <= 255) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A column width is from 0 to 255.");
    }

    /// <summary>Whether the file's filter on this sheet has criteria: Excel then treats every hidden row as filtered out.</summary>
    internal bool HasFilterCriteria { get; set; }

    /// <summary>Whether this sheet holds a linked book's kept values.</summary>
    internal bool IsLinked => linked;

    /// <summary>For a linked book's sheet: Excel could not refresh it, so a cell it doesn't hold is #REF!, not blank.</summary>
    internal bool LinkRefreshFailed { get; set; }

    internal Dictionary<int, RowRecord> RowRecords { get; } = [];

    internal ColumnSpans ColumnSpans { get; } = new();

    /// <summary>The file's c elements, emptied of what the model holds, for cells with attributes or children it does not; written back while the cell keeps its value.</summary>
    internal Dictionary<long, XElement> CellExtras { get; } = [];

    /// <summary>What the sheet's part held that the model does not, written back on save; null for a sheet made in code.</summary>
    internal Xlsx.SheetSource? Source { get; set; }

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

    /// <summary>
    /// Evaluates a formula as if it were the formula of a cell of this sheet, and gives the value that cell would show. Other
    /// cells give the values they hold now; formula cells give their last results, which this does not recalculate.
    /// </summary>
    /// <param name="formula">The formula in Excel's stored form, with or without the leading <c>=</c>, such as <c>SUM(A1:A3)*2</c>.</param>
    /// <param name="cell">
    /// The cell to evaluate it for, A1 when null: a range meets this cell's row or column where a single value is needed
    /// (implicit intersection), and ROW() gives its row.
    /// </param>
    /// <exception cref="ArgumentNullException">The formula is null.</exception>
    /// <exception cref="FormulaException">The formula does not follow Excel's grammar.</exception>
    public CellValue Evaluate(string formula, CellAddress? cell = null)
    {
        ArgumentNullException.ThrowIfNull(formula);
        CellAddress at = cell ?? default;
        string text = formula.StartsWith('=') ? formula[1..] : formula;
        ParsedFormula parsed = FormulaParser.Parse(text, r1c1: false, at);
        EvalContext context = new(this, at.Row, at.Column);
        return FormulaEvaluator.Evaluate(FormulaCompiler.Compile(parsed), context);
    }

    /// <summary>The sheet's name.</summary>
    public override string ToString() => Name;

    /// <summary>
    /// Writes the sheet as comma- or tab-separated text, from A1 to the last used cell: a tab for a <c>.tsv</c> or
    /// <c>.tab</c> path, otherwise a comma, unless the options name one. Values are written as their cells show them; see
    /// <see cref="CsvOptions"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The path is null.</exception>
    public void SaveCsv(string path, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.Create(path);
        Csv.CsvWriter.Write(this, stream, path, options);
    }

    /// <summary>Writes the sheet as comma- or tab-separated text to a stream, a comma unless the options name another separator.</summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    public void SaveCsv(Stream stream, CsvOptions? options = null) => Csv.CsvWriter.Write(this, stream, null, options);

    /// <summary>
    /// The sheet laid out on pages as Excel prints it: its print area or used cells, at its scale or fitted to its pages,
    /// broken at its page breaks, with its titles, headings, gridlines, header and footer as its <see cref="PageSetup"/> says.
    /// </summary>
    public SheetPrintLayout Paginate(SheetPrintOptions? options = null) => SheetPrintLayout.Create(Workbook, [this], options);

    /// <summary>Writes the sheet's printed pages to a PDF file.</summary>
    /// <exception cref="ArgumentException">The path is null or empty.</exception>
    public void SaveAsPdf(string path, SheetPdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Printing.SheetPdf.Save(Workbook, [this], path, null, options);
    }

    /// <summary>Writes the sheet's printed pages as a PDF to a stream, which is left open.</summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    public void SaveAsPdf(Stream stream, SheetPdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Printing.SheetPdf.Save(Workbook, [this], null, stream, options);
    }

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
        Changed(row, column);
        ref readonly CellRecord old = ref store.Get(row, column);
        CellRecord record = new() { Style = old.Style };
        if (old.Kind == CellKind.Formula)
        {
            Unshare(row, column, (int)old.Payload);
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
        Changed(row, column);
        ref readonly CellRecord old = ref store.Get(row, column);
        if (old.Kind == CellKind.Formula)
        {
            Unshare(row, column, (int)old.Payload);
            formulas.Remove((int)old.Payload);
        }

        store.Set(row, column, new CellRecord { Kind = CellKind.Number, Payload = BitConverter.DoubleToInt64Bits(number), Style = old.Style });
    }

    internal string? GetFormula(int row, int column)
    {
        ref readonly CellRecord record = ref store.Get(row, column);
        if (record.Kind != CellKind.Formula)
        {
            return null;
        }

        FormulaEntry entry = formulas[(int)record.Payload];
        return entry.Text.Length > 0 ? entry.Text : SharedText(row, column, entry);
    }

    internal bool HasFormula(int row, int column) => store.Get(row, column).Kind == CellKind.Formula;

    internal void SetFormula(int row, int column, string? formula, CellValue result, XAttribute[]? attributes = null)
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

        Changed(row, column);
        ref readonly CellRecord old = ref store.Get(row, column);
        if (old.Kind == CellKind.Formula)
        {
            Unshare(row, column, (int)old.Payload);
            FormulaEntry entry = formulas[(int)old.Payload];
            entry.Text = text;
            entry.Result = result;
            entry.Attributes = attributes;
            return;
        }

        int style = old.Style;
        store.Set(row, column, new CellRecord { Kind = CellKind.Formula, Payload = formulas.Add(text, result, attributes), Style = style });
    }

    /// <summary>
    /// Stores a value a calculation gave a cell of an array formula's range, without noting a change: the formula owns the
    /// value. A cell holding a formula of its own keeps it.
    /// </summary>
    internal void SetCalculated(int row, int column, CellValue value)
    {
        ref readonly CellRecord old = ref store.Get(row, column);
        if (old.Kind == CellKind.Formula)
        {
            return;
        }

        CellRecord record = new() { Style = old.Style };
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

    /// <summary>
    /// The first cell and text of the shared formula with a shared index, the cell that holds the text the others share; null
    /// when no cell holds it any more. Found once and kept until a cell changes.
    /// </summary>
    internal (CellAddress Cell, string Text)? SharedMaster(string sharedIndex)
    {
        if (sharedMasters is null)
        {
            Dictionary<string, (CellAddress, string)> found = [];
            foreach ((int index, FormulaEntry entry) in formulas.Indexed())
            {
                if (entry.Text.Length > 0 && entry.SharedIndex is { } si && !found.ContainsKey(si) && FindFormula(index, entry) is { } cell)
                {
                    found[si] = (cell, entry.Text);
                }
            }

            sharedMasters = found;
        }

        return sharedMasters.TryGetValue(sharedIndex, out (CellAddress Cell, string Text) master) ? master : null;
    }

    /// <summary>The cell holding a formula, looked for within its range, where the first cell of a shared formula is.</summary>
    private CellAddress? FindFormula(int index, FormulaEntry entry)
    {
        RangeAddress? range = RangeAddress.TryParse(entry.Attribute("ref"), out RangeAddress area) ? area : store.Bounds();
        if (range is null)
        {
            return null;
        }

        foreach ((int row, int column) in store.Occupied(range.Value))
        {
            ref readonly CellRecord record = ref store.Get(row, column);
            if (record.Kind == CellKind.Formula && record.Payload == index)
            {
                return new CellAddress(row, column);
            }
        }

        return null;
    }

    /// <summary>The text a cell sharing another's formula has: the first cell's text with its relative references moved here.</summary>
    private string? SharedText(int row, int column, FormulaEntry entry)
    {
        if (entry.SharedIndex is not { } si || SharedMaster(si) is not { } master)
        {
            return null;
        }

        return FormulaParser.TryParse(master.Text, r1c1: false, master.Cell) is { } parsed
            ? FormulaPrinter.Print(parsed, ReferenceStyle.A1, new CellAddress(row, column))
            : null;
    }

    /// <summary>
    /// Before the first cell of a shared formula changes, each cell sharing it gets the formula as its own, moved to it, so
    /// the others keep their formulas as Excel does.
    /// </summary>
    private void Unshare(int row, int column, int index)
    {
        FormulaEntry master = formulas[index];
        if (master.Text.Length == 0 || master.SharedIndex is not { } si
            || FormulaParser.TryParse(master.Text, r1c1: false, new CellAddress(row, column)) is not { } parsed)
        {
            return;
        }

        RangeAddress? range = RangeAddress.TryParse(master.Attribute("ref"), out RangeAddress area) ? area : store.Bounds();
        if (range is null)
        {
            return;
        }

        foreach ((int r, int c) in store.Occupied(range.Value))
        {
            ref readonly CellRecord record = ref store.Get(r, c);
            if (record.Kind == CellKind.Formula && record.Payload != index && formulas[(int)record.Payload] is { Text.Length: 0 } follower
                && follower.SharedIndex == si)
            {
                follower.Text = FormulaPrinter.Print(parsed, ReferenceStyle.A1, new CellAddress(r, c));
                follower.Attributes = null;
            }
        }

        sharedMasters = null;
    }

    /// <summary>The range of the array formula a cell is part of, or null.</summary>
    internal RangeAddress? ArrayRangeAt(int row, int column)
    {
        ref readonly CellRecord own = ref store.Get(row, column);
        if (own.Kind == CellKind.Formula && formulas[(int)own.Payload].ArrayRange is { } range)
        {
            return range;
        }

        CellAddress cell = new(row, column);
        foreach (FormulaEntry entry in formulas.Entries)
        {
            if (entry.ArrayRange is { } other && other.Contains(cell))
            {
                return other;
            }
        }

        return null;
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
        Changed(row, column);
        ref readonly CellRecord old = ref store.Get(row, column);
        if (old.Kind == CellKind.Formula)
        {
            Unshare(row, column, (int)old.Payload);
            formulas.Remove((int)old.Payload);
        }

        store.Set(row, column, default);
    }

    /// <summary>Stores a cell read from a file as it is: its record, with its formula when it has one.</summary>
    internal void SetRead(int row, int column, CellRecord record, FormulaEntry? formula)
    {
        if (formula is not null)
        {
            record.Kind = CellKind.Formula;
            record.Payload = formulas.Add(formula.Text, formula.Result, formula.Attributes);
        }

        store.Set(row, column, record);
    }

    internal static long Key(int row, int column) => ((long)row << 14) | (uint)column;

    /// <summary>Notes that a cell's value or formula changes: the workbook needs calculating, and the cell's file extras no longer apply.</summary>
    private void Changed(int row, int column)
    {
        if (workbook is not null && !linked)
        {
            workbook.ChangeCount++;
            workbook.Calculator?.Noted(this, row, column);
        }

        sharedMasters = null;

        if (CellExtras.Count != 0)
        {
            CellExtras.Remove(Key(row, column));
        }
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

/// <summary>Whether a sheet's tab shows.</summary>
public enum SheetVisibility
{
    /// <summary>The tab shows.</summary>
    Visible,

    /// <summary>The tab is hidden; Excel's Unhide shows it again.</summary>
    Hidden,

    /// <summary>The tab is hidden and Excel's Unhide does not list it; only code shows it again.</summary>
    VeryHidden,
}
