using System.Collections;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>Settings shared by a run of columns, as SpreadsheetML's <c>col</c> element gives them for <c>min</c> to <c>max</c>.</summary>
internal sealed class ColumnSpan
{
    public int First;
    public int Last;

    /// <summary>The width in characters of the default font's widest digit, or NaN for the sheet's default.</summary>
    public double Width = double.NaN;
    public bool Hidden;
    public int OutlineLevel;
    public bool Collapsed;

    /// <summary>The style index the columns give their empty cells, or -1 for none.</summary>
    public int Style = -1;

    /// <summary>Attributes of the file's col element this model does not hold, written back as they were.</summary>
    public XAttribute[]? Extra;

    public bool IsDefault => double.IsNaN(Width) && !Hidden && OutlineLevel == 0 && !Collapsed && Style < 0 && Extra is null;

    public ColumnSpan CopyAs(int first, int last) => new()
    {
        First = first,
        Last = last,
        Width = Width,
        Hidden = Hidden,
        OutlineLevel = OutlineLevel,
        Collapsed = Collapsed,
        Style = Style,
        Extra = Extra,
    };
}

/// <summary>A sheet's column settings as runs of columns, sorted and not overlapping.</summary>
internal sealed class ColumnSpans
{
    public List<ColumnSpan> Spans { get; } = [];

    public ColumnSpan? Find(int column)
    {
        int low = 0, high = Spans.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            ColumnSpan span = Spans[middle];
            if (column < span.First)
            {
                high = middle - 1;
            }
            else if (column > span.Last)
            {
                low = middle + 1;
            }
            else
            {
                return span;
            }
        }

        return null;
    }

    /// <summary>Changes one column, splitting the run it is in so the others keep their settings.</summary>
    public void Change(int column, Action<ColumnSpan> change)
    {
        ColumnSpan? span = Find(column);
        if (span is null)
        {
            span = new ColumnSpan { First = column, Last = column };
            int at = Spans.FindIndex(s => s.First > column);
            Spans.Insert(at < 0 ? Spans.Count : at, span);
        }
        else if (span.First != column || span.Last != column)
        {
            int at = Spans.IndexOf(span);
            List<ColumnSpan> pieces = [];
            if (span.First < column)
            {
                pieces.Add(span.CopyAs(span.First, column - 1));
            }

            ColumnSpan single = span.CopyAs(column, column);
            pieces.Add(single);
            if (span.Last > column)
            {
                pieces.Add(span.CopyAs(column + 1, span.Last));
            }

            Spans.RemoveAt(at);
            Spans.InsertRange(at, pieces);
            span = single;
        }

        change(span);
        if (span.IsDefault)
        {
            Spans.Remove(span);
        }
    }

    /// <summary>Adds a run read from a file; a run that overlaps one already there is cut to the columns still free.</summary>
    public void AddRead(ColumnSpan span)
    {
        int at = Spans.Count;
        if (at > 0 && Spans[at - 1].Last >= span.First)
        {
            span.First = Spans[at - 1].Last + 1;
            if (span.First > span.Last)
            {
                return;
            }
        }

        Spans.Add(span);
    }
}

/// <summary>A sheet's columns by zero-based index, and, when enumerated, the columns that have a width, are hidden, outlined or styled.</summary>
public sealed class SheetColumnCollection : IEnumerable<SheetColumn>
{
    private readonly Worksheet sheet;

    internal SheetColumnCollection(Worksheet sheet)
    {
        this.sheet = sheet;
    }

    /// <summary>The column at a zero-based index: <c>Columns[0]</c> is column A.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The index is off the sheet.</exception>
    public SheetColumn this[int index]
    {
        get
        {
            CellAddress.CheckColumn(index);
            return new SheetColumn(sheet, index);
        }
    }

    /// <summary>The column with an A1 name such as <c>C</c>.</summary>
    /// <exception cref="FormatException">The name is not a column name.</exception>
    public SheetColumn this[string name] => this[CellAddress.ColumnIndex(name)];

    /// <summary>
    /// The columns that have a width, are hidden, outlined or styled, left to right. A setting given to a run of columns, as
    /// files often give the columns to the end of the sheet, yields each column of the run.
    /// </summary>
    public IEnumerator<SheetColumn> GetEnumerator()
    {
        foreach (ColumnSpan span in sheet.ColumnSpans.Spans.ToList())
        {
            for (int column = span.First; column <= span.Last; column++)
            {
                yield return new SheetColumn(sheet, column);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One column of a sheet: its width, whether it is hidden, its outline level and the style of its empty cells.</summary>
public sealed class SheetColumn
{
    private readonly Worksheet sheet;

    internal SheetColumn(Worksheet sheet, int index)
    {
        this.sheet = sheet;
        Index = index;
    }

    /// <summary>The zero-based index: 0 is column A.</summary>
    public int Index { get; }

    /// <summary>The column's A1 name, such as <c>C</c>.</summary>
    public string Name => CellAddress.ColumnName(Index);

    /// <summary>
    /// The width as Excel gives it, in characters of the default font's widest digit (with padding), from 0 to 255, or null for
    /// the sheet's default width.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is outside 0 to 255.</exception>
    public double? Width
    {
        get => Span is { } span && !double.IsNaN(span.Width) ? span.Width : null;
        set
        {
            if (value is { } width && !(width is >= 0 and <= 255))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A column width is from 0 to 255.");
            }

            sheet.ColumnSpans.Change(Index, span => span.Width = value ?? double.NaN);
        }
    }

    /// <summary>Whether the column is hidden.</summary>
    public bool Hidden
    {
        get => Span?.Hidden ?? false;
        set => sheet.ColumnSpans.Change(Index, span => span.Hidden = value);
    }

    /// <summary>The outline (grouping) level, from 0 for none to 7.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0 to 7.</exception>
    public int OutlineLevel
    {
        get => Span?.OutlineLevel ?? 0;
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)value, 7u, nameof(value));
            sheet.ColumnSpans.Change(Index, span => span.OutlineLevel = value);
        }
    }

    /// <summary>Whether the outline group this column closes is collapsed.</summary>
    public bool Collapsed
    {
        get => Span?.Collapsed ?? false;
        set => sheet.ColumnSpans.Change(Index, span => span.Collapsed = value);
    }

    /// <summary>The style the column gives its empty cells, or null for none. Cells keep their own styles.</summary>
    public CellStyle? Style
    {
        get => Span is { Style: >= 0 } span ? sheet.Workbook.Styles[span.Style] : null;
        set
        {
            int index = value is null ? -1 : sheet.Workbook.Styles.Add(value);
            sheet.ColumnSpans.Change(Index, span => span.Style = index);
        }
    }

    /// <summary>
    /// Fits the column to the widest text of its cells, as Excel's AutoFit does, and gives its new width; null, and the
    /// width left as it is, when no cell of the column has text. See <see cref="CellRange.AutoFitColumns"/> for how text
    /// is measured.
    /// </summary>
    /// <returns>The new width in characters, as <see cref="Width"/> gives it, or null.</returns>
    /// <example>
    /// <code>
    /// sheet.Columns["B"].AutoFit();
    /// </code>
    /// </example>
    public double? AutoFit() => sheet.AutoFitColumn(Index, 0, CellAddress.MaxRows - 1, new Printing.TextMeter(sheet.Workbook));

    private ColumnSpan? Span => sheet.ColumnSpans.Find(Index);
}
