using System.Text;

namespace Egap.Pdf.Conversion;

/// <summary>
/// Detected tables as CSV (RFC 4180; Build Plan task 19): one CSV per table, a table that runs on across pages written
/// once with its repeated header left out, merged cells repeated across their span or left empty, and the number of header
/// rows given with each so a reader can treat them as headers.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("report.pdf");
/// foreach (TableCsvFile table in TableCsv.Export(PdfLayoutAnalyzer.Analyze(pdf)))
/// {
///     File.WriteAllText($"table-{table.Index + 1}.csv", table.Text);
/// }
/// </code>
/// </example>
public static class TableCsv
{
    /// <summary>One table as CSV.</summary>
    public static string ToCsv(LayoutTable table, TableCsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        options ??= TableCsvOptions.Default;
        List<string?[]> rows = Grid(table, options);
        return Write(rows, Headers(table), options);
    }

    /// <summary>Every table of the pages as CSV, in reading order; a table carried on from the page before joins the one it carries on.</summary>
    public static IReadOnlyList<TableCsvFile> Export(IEnumerable<PdfPageLayout> pages, TableCsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        options ??= TableCsvOptions.Default;
        List<(int Page, int Headers, int Columns, List<string?[]> Rows)> tables = [];
        foreach (PdfPageLayout page in pages)
        {
            foreach (LayoutTable table in page.Tables)
            {
                List<string?[]> rows = Grid(table, options);
                if (table.ContinuesPrevious && tables.Count > 0 && tables[^1].Columns == table.ColumnCount)
                {
                    // The rows that repeat the first part's header are not written again.
                    int skip = table.RepeatsHeader ? Math.Max(Headers(table), 1) : 0;
                    tables[^1].Rows.AddRange(rows.Skip(Math.Min(skip, rows.Count)));
                    continue;
                }

                tables.Add((page.PageIndex, Headers(table), table.ColumnCount, rows));
            }
        }

        return [.. tables.Select((t, i) => new TableCsvFile(i, t.Page, t.Headers, Write(t.Rows, t.Headers, options)))];
    }

    /// <summary>The table's grid, a row of cell texts per row; a position a merged cell covers holds its text or null by the options.</summary>
    private static List<string?[]> Grid(LayoutTable table, TableCsvOptions options)
    {
        List<string?[]> rows = [.. Enumerable.Range(0, table.RowCount).Select(_ => new string?[table.ColumnCount])];
        foreach (LayoutTableCell cell in table.Cells)
        {
            for (int r = cell.Row; r < Math.Min(cell.Row + cell.RowSpan, table.RowCount); r++)
            {
                for (int c = cell.Column; c < Math.Min(cell.Column + cell.ColumnSpan, table.ColumnCount); c++)
                {
                    bool origin = r == cell.Row && c == cell.Column;
                    if (origin || options.RepeatMergedCells)
                    {
                        rows[r][c] ??= cell.Text;
                    }
                }
            }
        }

        return rows;
    }

    /// <summary>The leading rows whose cells are all header cells.</summary>
    private static int Headers(LayoutTable table)
    {
        int rows = 0;
        while (rows < table.RowCount && table.Cells.Where(c => c.Row == rows).ToList() is { Count: > 0 } cells && cells.All(c => c.IsHeader))
        {
            rows++;
        }

        return rows;
    }

    private static string Write(List<string?[]> rows, int headers, TableCsvOptions options)
    {
        StringBuilder csv = new();
        foreach (string?[] row in rows.Skip(options.IncludeHeaderRows ? 0 : Math.Min(headers, rows.Count)))
        {
            for (int c = 0; c < row.Length; c++)
            {
                if (c > 0)
                {
                    csv.Append(options.Separator);
                }

                Field(csv, row[c] ?? "", options.Separator);
            }

            csv.Append("\r\n");
        }

        return csv.ToString();
    }

    /// <summary>A field, quoted when it holds the separator, a quote or a line break, its quotes doubled (RFC 4180 2.6, 2.7).</summary>
    private static void Field(StringBuilder csv, string text, char separator)
    {
        if (text.AsSpan().IndexOfAny(['"', '\r', '\n', separator]) < 0)
        {
            csv.Append(text);
            return;
        }

        csv.Append('"').Append(text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}

/// <summary>How <see cref="TableCsv"/> writes tables.</summary>
public sealed record TableCsvOptions
{
    /// <summary>The defaults: commas, merged cells' text once at their first position, header rows written.</summary>
    public static TableCsvOptions Default { get; } = new();

    /// <summary>Whether a merged cell's text is repeated at every position it covers (better for a spreadsheet's filters) rather than written once with the rest left empty.</summary>
    public bool RepeatMergedCells { get; init; }

    /// <summary>Whether the header rows are written; the count of them is in <see cref="TableCsvFile.HeaderRows"/> either way.</summary>
    public bool IncludeHeaderRows { get; init; } = true;

    /// <summary>The field separator: a comma by default, a semicolon or a tab for software that wants one.</summary>
    /// <exception cref="ArgumentException">The separator is a quote or a line break.</exception>
    public char Separator
    {
        get;
        init => field = value is '"' or '\r' or '\n' ? throw new ArgumentException("A quote or a line break cannot separate fields.", nameof(value)) : value;
    } = ',';
}

/// <summary>A table written as CSV.</summary>
/// <param name="Index">The table's place among the document's tables, from 0.</param>
/// <param name="PageIndex">The page it starts on, from 0.</param>
/// <param name="HeaderRows">How many of its first rows are header rows (written or not, by <see cref="TableCsvOptions.IncludeHeaderRows"/>).</param>
/// <param name="Text">The CSV: CRLF line ends, fields quoted where RFC 4180 needs it.</param>
public sealed record TableCsvFile(int Index, int PageIndex, int HeaderRows, string Text);
