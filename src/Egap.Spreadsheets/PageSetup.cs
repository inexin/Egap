using System.Collections;
using System.Text;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>Which way a sheet prints on the paper.</summary>
public enum PrintOrientation
{
    /// <summary>The printer's default.</summary>
    Default,

    /// <summary>Upright.</summary>
    Portrait,

    /// <summary>Sideways.</summary>
    Landscape,
}

/// <summary>The order in which a sheet too big for one page prints its pages.</summary>
public enum PrintPageOrder
{
    /// <summary>Down the rows first, then over to the next columns, as Excel does by default.</summary>
    DownThenOver,

    /// <summary>Across the columns first, then down.</summary>
    OverThenDown,
}

/// <summary>
/// How a sheet prints: paper, orientation, scaling, margins, centring, gridlines, headings, page order, the header and
/// footer, the print area, the rows and columns repeated on each page and the manual page breaks. The print area and the
/// titles are kept as Excel keeps them, as the sheet's defined names <c>_xlnm.Print_Area</c> and <c>_xlnm.Print_Titles</c> in
/// <see cref="Workbook.Names"/>.
/// </summary>
public sealed class PageSetup
{
    private const string PrintAreaName = "_xlnm.Print_Area";
    private const string PrintTitlesName = "_xlnm.Print_Titles";

    private readonly Worksheet sheet;

    internal PageSetup(Worksheet sheet)
    {
        this.sheet = sheet;
    }

    /// <summary>
    /// The ranges that print, each starting on a new page, or empty to print the sheet's used cells. Set an empty list to
    /// clear it.
    /// </summary>
    public IReadOnlyList<RangeAddress> PrintArea
    {
        get => Ranges(PrintAreaName);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetName(PrintAreaName, value.Count == 0 ? null : string.Join(',', value.Select(Reference)));
        }
    }

    /// <summary>The rows printed at the top of every page, such as <c>1:2</c>, or null for none.</summary>
    /// <exception cref="ArgumentException">The range is not whole rows.</exception>
    public RangeAddress? PrintTitleRows
    {
        get
        {
            foreach (RangeAddress range in Ranges(PrintTitlesName))
            {
                if (range.IsWholeRows && !range.IsWholeColumns)
                {
                    return range;
                }
            }

            return null;
        }

        set
        {
            if (value is { IsWholeRows: false })
            {
                throw new ArgumentException("Title rows are whole rows, such as 1:2.", nameof(value));
            }

            SetTitles(PrintTitleColumns, value);
        }
    }

    /// <summary>The columns printed at the left of every page, such as <c>A:B</c>, or null for none.</summary>
    /// <exception cref="ArgumentException">The range is not whole columns.</exception>
    public RangeAddress? PrintTitleColumns
    {
        get
        {
            foreach (RangeAddress range in Ranges(PrintTitlesName))
            {
                if (range.IsWholeColumns && !range.IsWholeRows)
                {
                    return range;
                }
            }

            return null;
        }

        set
        {
            if (value is { IsWholeColumns: false })
            {
                throw new ArgumentException("Title columns are whole columns, such as A:B.", nameof(value));
            }

            SetTitles(value, PrintTitleRows);
        }
    }

    /// <summary>The manual page breaks between rows: each is the index of the row that starts a new page.</summary>
    public PageBreakCollection RowBreaks { get; } = new(CellAddress.MaxRows);

    /// <summary>The manual page breaks between columns: each is the index of the column that starts a new page.</summary>
    public PageBreakCollection ColumnBreaks { get; } = new(CellAddress.MaxColumns);

    /// <summary>Which way the sheet prints.</summary>
    public PrintOrientation Orientation { get; set; }

    /// <summary>The paper size as SpreadsheetML numbers them (1 Letter, 9 A4, 5 Legal and so on), or null for the default.</summary>
    public int? PaperSize { get; set; }

    /// <summary>The print scale in per cent, from 10 to 400, or null for 100; not used when <see cref="FitToPage"/> is set.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The scale is outside 10 to 400.</exception>
    public int? Scale
    {
        get;
        set => field = value is null or (>= 10 and <= 400) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A print scale is from 10 to 400 per cent.");
    }

    /// <summary>Whether the sheet is scaled to fit <see cref="FitToWidth"/> pages across and <see cref="FitToHeight"/> down.</summary>
    public bool FitToPage { get; set; }

    /// <summary>How many pages wide the sheet fits when <see cref="FitToPage"/> is set; 0 for as many as it needs. 1 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public int FitToWidth
    {
        get;
        set => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    } = 1;

    /// <summary>How many pages tall the sheet fits when <see cref="FitToPage"/> is set; 0 for as many as it needs. 1 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative.</exception>
    public int FitToHeight
    {
        get;
        set => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    } = 1;

    /// <summary>The number of the first page, or null to start at 1.</summary>
    public int? FirstPageNumber { get; set; }

    /// <summary>The order pages print in.</summary>
    public PrintPageOrder PageOrder { get; set; }

    /// <summary>The left margin in inches; Excel's default is 0.7.</summary>
    public double LeftMargin { get; set; } = 0.7;

    /// <summary>The right margin in inches; Excel's default is 0.7.</summary>
    public double RightMargin { get; set; } = 0.7;

    /// <summary>The top margin in inches; Excel's default is 0.75.</summary>
    public double TopMargin { get; set; } = 0.75;

    /// <summary>The bottom margin in inches; Excel's default is 0.75.</summary>
    public double BottomMargin { get; set; } = 0.75;

    /// <summary>The header's distance from the top edge in inches; Excel's default is 0.3.</summary>
    public double HeaderMargin { get; set; } = 0.3;

    /// <summary>The footer's distance from the bottom edge in inches; Excel's default is 0.3.</summary>
    public double FooterMargin { get; set; } = 0.3;

    /// <summary>Whether the sheet is centred across the page.</summary>
    public bool CenterHorizontally { get; set; }

    /// <summary>Whether the sheet is centred down the page.</summary>
    public bool CenterVertically { get; set; }

    /// <summary>Whether gridlines print.</summary>
    public bool PrintGridLines { get; set; }

    /// <summary>Whether row and column headings print.</summary>
    public bool PrintHeadings { get; set; }

    /// <summary>
    /// The header on each page in Excel's header code, such as <c>&amp;L&amp;D&amp;CReport&amp;RPage &amp;P of &amp;N</c>, or
    /// null for none.
    /// </summary>
    public string? Header { get; set; }

    /// <summary>The footer on each page in Excel's header code, or null for none.</summary>
    public string? Footer { get; set; }

    internal XElement? SourcePageSetup { get; set; }

    internal XElement? SourceMargins { get; set; }

    internal XElement? SourcePrintOptions { get; set; }

    internal XElement? SourceHeaderFooter { get; set; }

    /// <summary>The file's rowBreaks and colBreaks, written back as they were while the breaks are the same.</summary>
    internal XElement? SourceRowBreaks { get; set; }

    internal XElement? SourceColumnBreaks { get; set; }

    private List<RangeAddress> Ranges(string name)
    {
        List<RangeAddress> ranges = [];
        if (!sheet.Workbook.Names.TryGet(name, sheet, out DefinedName? defined))
        {
            return ranges;
        }

        foreach (string part in SplitReferences(defined.Formula))
        {
            int bang = part.LastIndexOf('!');
            if (RangeAddress.TryParse(part[(bang + 1)..].Replace("$", "", StringComparison.Ordinal), out RangeAddress range))
            {
                ranges.Add(range);
            }
        }

        return ranges;
    }

    private void SetTitles(RangeAddress? columns, RangeAddress? rows)
    {
        List<string> parts = [];
        if (columns is { } c)
        {
            parts.Add(Reference(c));
        }

        if (rows is { } r)
        {
            parts.Add(Reference(r));
        }

        SetName(PrintTitlesName, parts.Count == 0 ? null : string.Join(',', parts));
    }

    private void SetName(string name, string? formula)
    {
        DefinedNameCollection names = sheet.Workbook.Names;
        bool exists = names.TryGet(name, sheet, out DefinedName? defined);
        if (formula is null)
        {
            if (exists)
            {
                names.Remove(defined!);
            }
        }
        else if (exists)
        {
            defined!.Formula = formula;
        }
        else
        {
            names.Add(name, formula, sheet);
        }
    }

    /// <summary>The range on this sheet in a name's stored form, such as <c>'My sheet'!$A$1:$D$20</c>.</summary>
    private string Reference(RangeAddress range)
    {
        StringBuilder text = new();
        string name = sheet.Name;
        bool plain = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.')
            && !CellAddress.TryParse(name, out _) && !RangeAddress.TryParse(name + ":" + name, out _);
        text.Append(plain ? name : "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'").Append('!');
        if (range.IsWholeRows && !range.IsWholeColumns)
        {
            text.Append('$').Append(range.First.Row + 1).Append(":$").Append(range.Last.Row + 1);
        }
        else if (range.IsWholeColumns && !range.IsWholeRows)
        {
            text.Append('$').Append(CellAddress.ColumnName(range.First.Column)).Append(":$").Append(CellAddress.ColumnName(range.Last.Column));
        }
        else
        {
            text.Append('$').Append(CellAddress.ColumnName(range.First.Column)).Append('$').Append(range.First.Row + 1);
            if (range.CellCount > 1)
            {
                text.Append(":$").Append(CellAddress.ColumnName(range.Last.Column)).Append('$').Append(range.Last.Row + 1);
            }
        }

        return text.ToString();
    }

    /// <summary>The references of a union such as <c>Sheet1!$A:$B,Sheet1!$1:$2</c>, split at the commas outside quotes.</summary>
    private static List<string> SplitReferences(string formula)
    {
        List<string> parts = [];
        int start = 0;
        bool quoted = false;
        for (int i = 0; i < formula.Length; i++)
        {
            if (formula[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (formula[i] == ',' && !quoted)
            {
                parts.Add(formula[start..i].Trim().TrimStart('('));
                start = i + 1;
            }
        }

        parts.Add(formula[start..].Trim().TrimStart('(').TrimEnd(')'));
        return parts;
    }
}

/// <summary>
/// A sheet's manual page breaks in one direction, in order: each is the index of the row or column that starts a new page.
/// </summary>
public sealed class PageBreakCollection : ICollection<int>
{
    private readonly SortedSet<int> breaks = [];
    private readonly int limit;

    internal PageBreakCollection(int limit)
    {
        this.limit = limit;
    }

    /// <summary>How many breaks there are.</summary>
    public int Count => breaks.Count;

    bool ICollection<int>.IsReadOnly => false;

    /// <summary>Adds a break before row or column <paramref name="item"/>; adding one that is there already does nothing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is 0 (no page comes before it) or past the sheet's last row or column.</exception>
    public void Add(int item)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(item);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(item, limit);
        breaks.Add(item);
    }

    /// <summary>Removes the break before <paramref name="item"/>.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Remove(int item) => breaks.Remove(item);

    /// <summary>Removes every break.</summary>
    public void Clear() => breaks.Clear();

    /// <summary>Whether there is a break before <paramref name="item"/>.</summary>
    public bool Contains(int item) => breaks.Contains(item);

    /// <inheritdoc/>
    public void CopyTo(int[] array, int arrayIndex) => breaks.CopyTo(array, arrayIndex);

    /// <summary>The breaks in order.</summary>
    public IEnumerator<int> GetEnumerator() => breaks.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
