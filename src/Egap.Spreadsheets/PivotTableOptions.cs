namespace Egap.Spreadsheets;

/// <summary>How a pivot table summarizes the values of a field (ECMA-376 Part 1, 18.18.67).</summary>
public enum PivotFunction
{
    /// <summary>The sum of the numbers.</summary>
    Sum,

    /// <summary>How many values there are, text included.</summary>
    Count,

    /// <summary>The average of the numbers.</summary>
    Average,

    /// <summary>The largest number.</summary>
    Max,

    /// <summary>The smallest number.</summary>
    Min,

    /// <summary>The numbers multiplied together.</summary>
    Product,

    /// <summary>How many numbers there are.</summary>
    CountNumbers,

    /// <summary>The standard deviation of the numbers as a sample.</summary>
    StandardDeviation,

    /// <summary>The standard deviation of the numbers as the whole population.</summary>
    StandardDeviationPopulation,

    /// <summary>The variance of the numbers as a sample.</summary>
    Variance,

    /// <summary>The variance of the numbers as the whole population.</summary>
    VariancePopulation,
}

/// <summary>How a pivot table lays out its row fields, as Excel's Report Layout does.</summary>
public enum PivotTableLayout
{
    /// <summary>The row fields share one column, each indented under the one before; Excel's default.</summary>
    Compact,

    /// <summary>A column for each row field, each item on a row of its own above the items under it.</summary>
    Outline,

    /// <summary>A column for each row field, an item on the same row as the first item under it.</summary>
    Tabular,
}

/// <summary>A field a pivot table summarizes in its values area, and how.</summary>
public sealed class PivotValueField
{
    /// <summary>A field of the source to summarize.</summary>
    /// <param name="field">The field's name: its header in the source.</param>
    /// <param name="function">How to summarize it.</param>
    /// <exception cref="ArgumentException">The field's name is empty.</exception>
    public PivotValueField(string field, PivotFunction function = PivotFunction.Sum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        Field = field;
        Function = function;
    }

    /// <summary>The field's name: its header in the source.</summary>
    public string Field { get; }

    /// <summary>How the field is summarized.</summary>
    public PivotFunction Function { get; }

    /// <summary>The name the values show, or null for Excel's, such as <c>Sum of Revenue</c>.</summary>
    public string? Name { get; init; }

    /// <summary>The number format the values show with, such as <c>#,##0.00</c>, or null for the cells' own.</summary>
    public string? NumberFormat { get; init; }
}

/// <summary>
/// What a new pivot table shows: the source's fields it puts on rows, columns and filters, the values it summarizes, and
/// how it looks. Fields are named by their headers in the source, case ignored.
/// </summary>
public sealed class PivotTableOptions
{
    /// <summary>The fields whose items make the rows, outermost first.</summary>
    public IList<string> Rows { get; } = new List<string>();

    /// <summary>The fields whose items make the columns, outermost first.</summary>
    public IList<string> Columns { get; } = new List<string>();

    /// <summary>The fields that filter the whole table, each on a row above it, showing all items until filtered.</summary>
    public IList<string> Filters { get; } = new List<string>();

    /// <summary>The fields summarized in the values area. A pivot table needs at least one row, column or value field.</summary>
    public IList<PivotValueField> Values { get; } = new List<PivotValueField>();

    /// <summary>How the row fields are laid out; <see cref="PivotTableLayout.Compact"/> by default, as in Excel.</summary>
    public PivotTableLayout Layout { get; set; }

    /// <summary>
    /// Whether two or more value fields go down the rows, each on a row of its own under each row item, rather than across
    /// the columns, where Excel puts them by default.
    /// </summary>
    public bool ValuesOnRows { get; set; }

    /// <summary>Whether the table ends with a grand total row; true by default.</summary>
    public bool GrandTotalRow { get; set; } = true;

    /// <summary>Whether the table ends with a grand total column; true by default.</summary>
    public bool GrandTotalColumn { get; set; } = true;

    /// <summary>
    /// The pivot table style, a built-in one such as <c>PivotStyleLight16</c>, Excel's default, or <c>PivotStyleMedium9</c>;
    /// null for none.
    /// </summary>
    public string? StyleName { get; set; } = "PivotStyleLight16";

    /// <summary>Whether the style marks the row headers; true by default.</summary>
    public bool ShowRowHeaders { get; set; } = true;

    /// <summary>Whether the style marks the column headers; true by default.</summary>
    public bool ShowColumnHeaders { get; set; } = true;

    /// <summary>Whether the style bands the rows.</summary>
    public bool ShowRowStripes { get; set; }

    /// <summary>Whether the style bands the columns.</summary>
    public bool ShowColumnStripes { get; set; }
}
