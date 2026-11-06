using System.Globalization;

using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Spreadsheets.Csv;

using DocumentCell = Egap.Documents.Cell;

namespace Egap.Spreadsheets;

/// <summary>How <see cref="Workbook.FromTables(Document, TableImportOptions?)"/> reads tables' text into cells.</summary>
public sealed class TableImportOptions
{
    /// <summary>
    /// Whether text becomes numbers, percentages, TRUE and FALSE, dates, times and errors, with a number format that shows them
    /// as written, as CSV reading does; when false, every cell is text. Default true.
    /// </summary>
    public bool DetectTypes { get; init; } = true;

    /// <summary>The culture numbers and dates are read in: <c>1.234,5</c> in German. The invariant culture is the default.</summary>
    /// <exception cref="ArgumentNullException">The culture set is null.</exception>
    public CultureInfo Culture
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = CultureInfo.InvariantCulture;
}

/// <summary>Puts tables on sheets.</summary>
internal static class TableImporter
{
    private const double MaxAutoWidth = 60;
    private const double DefaultWidth = 8.43;

    /// <summary>A workbook of one sheet per table in the body of each section, nested tables after the table holding them.</summary>
    public static Workbook FromDocument(Document document, TableImportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Build(document.Sections.SelectMany(section => section.Body.Descendants<Table>()).Select(GridOf), options);
    }

    /// <summary>
    /// A workbook of one sheet per table on the pages; a table carried on from the page before joins the sheet it carries on,
    /// without its repeated header.
    /// </summary>
    public static Workbook FromLayouts(IEnumerable<PdfPageLayout> pages, TableImportOptions? options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        List<TableGrid> grids = [];
        TableGrid? last = null;
        foreach (PdfPageLayout page in pages)
        {
            foreach (LayoutTable table in page.Tables)
            {
                TableGrid grid = GridOf(table);
                if (table.ContinuesPrevious && last is not null && last.Columns == grid.Columns)
                {
                    last.Join(grid, table.RepeatsHeader ? Math.Max(grid.HeaderRows, 1) : 0);
                }
                else
                {
                    grids.Add(grid);
                    last = grid;
                }

                AddNested(table, grids);
            }
        }

        return Build(grids, options);
    }

    private static void AddNested(LayoutTable table, List<TableGrid> grids)
    {
        foreach (LayoutTableCell cell in table.Cells)
        {
            foreach (LayoutTable nested in cell.Tables)
            {
                grids.Add(GridOf(nested));
                AddNested(nested, grids);
            }
        }
    }

    private static TableGrid GridOf(LayoutTable table)
    {
        TableGrid grid = new(table.ColumnCount, table.RowCount);
        foreach (LayoutTableCell cell in table.Cells)
        {
            // An empty paragraph drawn by Egap holds a zero-width space, which is no one's text.
            grid.Cells.Add(new GridCell(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan, cell.Text.Replace("\u200B", "", StringComparison.Ordinal).Trim(), cell.IsHeader));
        }

        return grid;
    }

    /// <summary>Finds each cell's grid position: a cell takes the columns of its grid span, and a continued cell lengthens the one above.</summary>
    private static TableGrid GridOf(Table table)
    {
        TableGrid grid = new(0, table.Rows.Count);
        Dictionary<int, int> open = [];
        int rowIndex = 0;
        foreach (Row row in table.Rows)
        {
            int column = 0;
            foreach (DocumentCell cell in row.Cells)
            {
                int span = Math.Max(cell.Format.GridSpan ?? 1, 1);
                if (cell.Format.VerticalMerge == VerticalMerge.Continue && open.TryGetValue(column, out int above))
                {
                    grid.Cells[above] = grid.Cells[above] with { RowSpan = rowIndex - grid.Cells[above].Row + 1 };
                }
                else
                {
                    open[column] = grid.Cells.Count;
                    string text = string.Join('\n', cell.Blocks.OfType<Paragraph>().Select(paragraph => paragraph.GetText())).Trim('\n');
                    grid.Cells.Add(new GridCell(rowIndex, column, 1, span, text, row.Format.IsHeader == true));
                }

                column = (int)Math.Min((long)column + span, CellAddress.MaxColumns);
            }

            grid.Columns = Math.Max(grid.Columns, column);
            rowIndex++;
        }

        return grid;
    }

    private static Workbook Build(IEnumerable<TableGrid> grids, TableImportOptions? options)
    {
        options ??= new TableImportOptions();
        Workbook book = Workbook.CreateEmpty();
        TypeDetector? detector = options.DetectTypes ? new TypeDetector(options.Culture, book) : null;
        foreach (TableGrid grid in grids)
        {
            Fill(book.Worksheets.Add($"Table {book.Worksheets.Count + 1}"), grid, detector);
        }

        if (book.Worksheets.Count == 0)
        {
            book.Worksheets.Add();
        }

        return book;
    }

    private static void Fill(Worksheet sheet, TableGrid grid, TypeDetector? detector)
    {
        Dictionary<int, int> widths = [];
        foreach (GridCell source in grid.Cells)
        {
            if (source.Row >= CellAddress.MaxRows || source.Column >= CellAddress.MaxColumns)
            {
                continue;
            }

            string text = source.Text.Length <= CellValue.MaxTextLength ? source.Text : source.Text[..CellValue.MaxTextLength];
            if (text.Length > 0)
            {
                if (detector?.Detect(text) is ({ } value, int style))
                {
                    sheet.SetValue(source.Row, source.Column, value);
                    if (style != 0)
                    {
                        sheet.SetStyleIndex(source.Row, source.Column, style);
                    }
                }
                else
                {
                    sheet.SetValue(source.Row, source.Column, CellValue.TextUnchecked(text));
                }
            }

            bool wrap = text.Contains('\n', StringComparison.Ordinal);
            if (source.Header || wrap)
            {
                sheet[source.Row, source.Column].UpdateStyle(style => style with
                {
                    Font = source.Header ? style.Font with { Bold = true } : style.Font,
                    Alignment = wrap ? style.Alignment with { WrapText = true } : style.Alignment,
                });
            }

            if (source.ColumnSpan == 1)
            {
                int longest = text.Split('\n').Max(line => line.Length);
                widths[source.Column] = Math.Max(widths.GetValueOrDefault(source.Column), longest);
            }

            int lastRow = (int)Math.Min((long)source.Row + Math.Max(source.RowSpan, 1) - 1, CellAddress.MaxRows - 1);
            int lastColumn = (int)Math.Min((long)source.Column + Math.Max(source.ColumnSpan, 1) - 1, CellAddress.MaxColumns - 1);
            RangeAddress range = new(source.Row, source.Column, lastRow, lastColumn);
            if (range.First != range.Last && !sheet.MergedCells.Any(merged => merged.Intersects(range)))
            {
                sheet.MergedCells.Add(range);
            }
        }

        foreach ((int column, int longest) in widths)
        {
            if (longest > DefaultWidth)
            {
                sheet.Columns[column].Width = Math.Min(longest + 1, MaxAutoWidth);
            }
        }
    }

    private readonly record struct GridCell(int Row, int Column, int RowSpan, int ColumnSpan, string Text, bool Header);

    /// <summary>A table's cells at their grid positions.</summary>
    private sealed class TableGrid(int columns, int rows)
    {
        public int Columns { get; set; } = columns;

        public int Rows { get; private set; } = rows;

        public List<GridCell> Cells { get; } = [];

        /// <summary>The leading rows whose cells are all header cells.</summary>
        public int HeaderRows
        {
            get
            {
                int headers = 0;
                while (headers < Rows && Cells.Where(cell => cell.Row == headers).ToList() is { Count: > 0 } cells && cells.All(cell => cell.Header))
                {
                    headers++;
                }

                return headers;
            }
        }

        /// <summary>Adds the rows of <paramref name="next"/> below these, leaving out its first <paramref name="skip"/>.</summary>
        public void Join(TableGrid next, int skip)
        {
            int offset = Rows - skip;
            Cells.AddRange(next.Cells.Where(cell => cell.Row >= skip).Select(cell => cell with { Row = cell.Row + offset }));
            Rows += Math.Max(next.Rows - skip, 0);
        }
    }
}
