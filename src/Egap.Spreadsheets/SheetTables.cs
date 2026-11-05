using System.Collections;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>The function a table's totals row shows for a column.</summary>
public enum TableTotalsFunction
{
    /// <summary>No total, or <see cref="SheetTableColumn.TotalsRowLabel"/>.</summary>
    None,

    /// <summary>The sum.</summary>
    Sum,

    /// <summary>The smallest value.</summary>
    Min,

    /// <summary>The largest value.</summary>
    Max,

    /// <summary>The average.</summary>
    Average,

    /// <summary>The number of cells that are not empty.</summary>
    Count,

    /// <summary>The number of cells that hold numbers.</summary>
    CountNumbers,

    /// <summary>The sample standard deviation.</summary>
    StandardDeviation,

    /// <summary>The sample variance.</summary>
    Variance,

    /// <summary>The formula in <see cref="SheetTableColumn.TotalsRowFormula"/>.</summary>
    Custom,
}

/// <summary>A column of a <see cref="SheetTable"/>: its name, which the header cell shows, and its totals.</summary>
public sealed class SheetTableColumn
{
    internal SheetTableColumn(string name)
    {
        Name = name;
    }

    /// <summary>The column's name, unique in the table; the header row shows it.</summary>
    public string Name { get; set; }

    /// <summary>What the totals row shows for the column.</summary>
    public TableTotalsFunction TotalsRowFunction { get; set; }

    /// <summary>Text the totals row shows for the column, such as <c>Total</c>, or null.</summary>
    public string? TotalsRowLabel { get; set; }

    /// <summary>The totals row's formula for <see cref="TableTotalsFunction.Custom"/>, without the leading <c>=</c>.</summary>
    public string? TotalsRowFormula { get; set; }

    /// <summary>The formula every data cell of the column holds, if the column is calculated; null otherwise.</summary>
    public string? CalculatedFormula { get; set; }

    /// <summary>The file's tableColumn element, for the attributes and children the model does not hold.</summary>
    internal XElement? Source { get; set; }
}

/// <summary>
/// An Excel table: a named range with a header row, optional totals row, filter buttons and a table style. Formulas can
/// refer to it by name, as in <c>SUM(Sales[Amount])</c>.
/// </summary>
public sealed class SheetTable
{
    internal SheetTable(Worksheet sheet, string name, RangeAddress range)
    {
        Worksheet = sheet;
        this.name = name;
        Range = range;
    }

    private string name;

    /// <summary>The sheet the table is on.</summary>
    public Worksheet Worksheet { get; }

    /// <summary>
    /// The table's name, used in formulas: letters, digits, underscores and periods, starting with a letter or underscore,
    /// unique among the workbook's tables, case ignored.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not one Excel allows, or another table has it.</exception>
    public string Name
    {
        get => name;
        set
        {
            if (value == name)
            {
                return;
            }

            SheetTableCollection.CheckName(Worksheet.Workbook, value, this);
            name = value;
        }
    }

    /// <summary>The cells of the table: header row, data and totals row.</summary>
    public RangeAddress Range { get; set; }

    /// <summary>The columns, one for each column of <see cref="Range"/>; the writer adds or drops columns to match.</summary>
    public List<SheetTableColumn> Columns { get; } = [];

    /// <summary>Whether the first row is a header row. Default true.</summary>
    public bool ShowHeaderRow { get; set; } = true;

    /// <summary>Whether the last row is a totals row.</summary>
    public bool ShowTotalsRow { get; set; }

    /// <summary>Whether the header row has filter buttons. Default true.</summary>
    public bool ShowFilterButtons { get; set; } = true;

    /// <summary>The table style, such as <c>TableStyleMedium2</c>, Excel's default; null for none.</summary>
    public string? StyleName { get; set; } = "TableStyleMedium2";

    /// <summary>Whether rows are banded. Default true.</summary>
    public bool ShowRowStripes { get; set; } = true;

    /// <summary>Whether columns are banded.</summary>
    public bool ShowColumnStripes { get; set; }

    /// <summary>Whether the first column is set apart.</summary>
    public bool ShowFirstColumn { get; set; }

    /// <summary>Whether the last column is set apart.</summary>
    public bool ShowLastColumn { get; set; }

    /// <summary>The file's table part and its element, for what the model does not hold; null for a table made in code.</summary>
    internal (string PartName, XElement Element, List<Egap.Documents.Docx.OpcRelationship> Relationships)? Source { get; set; }

    /// <summary>The relationship id the sheet's tableParts used for the table.</summary>
    internal string? RelationshipId { get; set; }

    /// <summary>What the model held when the file was read, to tell whether the part can be written back as it was.</summary>
    internal string? ReadState { get; set; }

    /// <summary>The columns' names made unique and as many as the range is wide, taking header cells' text where there is some and writing the names into the others.</summary>
    internal void SyncColumns()
    {
        int width = Range.ColumnCount;
        while (Columns.Count > width)
        {
            Columns.RemoveAt(Columns.Count - 1);
        }

        while (Columns.Count < width)
        {
            Columns.Add(new SheetTableColumn("Column" + (Columns.Count + 1)));
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < width; i++)
        {
            if (ShowHeaderRow && Worksheet.Cells.GetValue(Range.First.Row, Range.First.Column + i) is { Kind: CellValueKind.Text } header && header.Text.Length > 0)
            {
                Columns[i].Name = header.Text;
            }

            string unique = Columns[i].Name.Length == 0 ? "Column" + (i + 1) : Columns[i].Name;
            for (int n = 2; !seen.Add(unique); n++)
            {
                unique = Columns[i].Name + n;
            }

            Columns[i].Name = unique;
            if (ShowHeaderRow && Worksheet.Cells.GetValue(Range.First.Row, Range.First.Column + i) is var value && (value.Kind != CellValueKind.Text || value.Text != unique))
            {
                // Excel repairs a file whose header cells do not show the column names.
                Worksheet[Range.First.Row, Range.First.Column + i].Value = unique;
            }
        }
    }
}

/// <summary>A sheet's tables.</summary>
public sealed class SheetTableCollection : IReadOnlyList<SheetTable>
{
    private readonly List<SheetTable> tables = [];
    private readonly Worksheet sheet;

    internal SheetTableCollection(Worksheet sheet)
    {
        this.sheet = sheet;
    }

    /// <summary>How many tables the sheet has.</summary>
    public int Count => tables.Count;

    /// <summary>The table at a position.</summary>
    public SheetTable this[int index] => tables[index];

    /// <summary>The table of a name, case ignored.</summary>
    /// <exception cref="KeyNotFoundException">The sheet has no table of that name.</exception>
    public SheetTable this[string name] =>
        tables.Find(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"The sheet has no table named {name}.");

    /// <summary>
    /// Makes a table of a range. With a header row, header cells that are empty get the names <c>Column1</c>,
    /// <c>Column2</c> and so on, and the others name their columns.
    /// </summary>
    /// <param name="range">The cells, header row included; at least two rows with a header row.</param>
    /// <param name="name">The table's name, or null for the next free <c>TableN</c>.</param>
    /// <param name="hasHeaderRow">Whether the first row holds the column names.</param>
    /// <exception cref="ArgumentException">The name is not one Excel allows or is taken, the range is too small, or it overlaps another table.</exception>
    public SheetTable Add(RangeAddress range, string? name = null, bool hasHeaderRow = true)
    {
        if (hasHeaderRow && range.RowCount < 2)
        {
            throw new ArgumentException("A table with a header row has at least two rows.", nameof(range));
        }

        foreach (Worksheet other in sheet.Workbook.Worksheets)
        {
            foreach (SheetTable table in other.Tables)
            {
                if (other == sheet && table.Range.Intersects(range))
                {
                    throw new ArgumentException($"{range} overlaps the table {table.Name}.", nameof(range));
                }
            }
        }

        name ??= NextName(sheet.Workbook);
        CheckName(sheet.Workbook, name, null);
        SheetTable added = new(sheet, name, range) { ShowHeaderRow = hasHeaderRow };
        if (hasHeaderRow)
        {
            for (int i = 0; i < range.ColumnCount; i++)
            {
                Cell header = sheet[range.First.Row, range.First.Column + i];
                if (header.Value.Kind == CellValueKind.Empty)
                {
                    header.Value = "Column" + (i + 1);
                }
                else if (header.Value.Kind != CellValueKind.Text)
                {
                    header.Value = header.Text;
                }
            }
        }

        added.SyncColumns();
        tables.Add(added);
        return added;
    }

    /// <summary>Makes a table of a range given in A1 form.</summary>
    /// <exception cref="FormatException">The range is not an A1 reference.</exception>
    /// <exception cref="ArgumentException">The name is not one Excel allows or is taken, the range is too small, or it overlaps another table.</exception>
    public SheetTable Add(string range, string? name = null, bool hasHeaderRow = true) => Add(RangeAddress.Parse(range), name, hasHeaderRow);

    /// <summary>The table that holds a cell, or null.</summary>
    public SheetTable? Find(CellAddress cell) => tables.Find(t => t.Range.Contains(cell));

    /// <summary>Removes a table; its cells keep their values and styles.</summary>
    /// <returns>Whether the table was on the sheet.</returns>
    public bool Remove(SheetTable table) => tables.Remove(table);

    /// <inheritdoc/>
    public IEnumerator<SheetTable> GetEnumerator() => tables.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(SheetTable table) => tables.Add(table);

    internal static void CheckName(Workbook book, string name, SheetTable? renamed)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        bool valid = name.Length <= 255 && (char.IsLetter(name[0]) || name[0] is '_' or '\\')
            && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '\\')
            && !CellAddress.TryParse(name, out _) && !name.Equals("R", StringComparison.OrdinalIgnoreCase) && !name.Equals("C", StringComparison.OrdinalIgnoreCase);
        if (!valid)
        {
            throw new ArgumentException($"'{name}' is not a table name Excel allows: letters, digits, underscores and periods, starting with a letter or underscore, and not a cell reference.", nameof(name));
        }

        foreach (Worksheet sheet in book.Worksheets)
        {
            foreach (SheetTable table in sheet.Tables)
            {
                if (table != renamed && table.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"The workbook has a table named {table.Name} already.", nameof(name));
                }
            }
        }

        foreach (DefinedName defined in book.Names)
        {
            if (defined.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The workbook has a defined name {defined.Name} already.", nameof(name));
            }
        }
    }

    private static string NextName(Workbook book)
    {
        HashSet<string> taken = new(book.Worksheets.SelectMany(s => s.Tables).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        for (int i = 1; ; i++)
        {
            string name = "Table" + i;
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }
}
