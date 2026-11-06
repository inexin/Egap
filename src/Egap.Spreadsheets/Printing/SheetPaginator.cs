using Egami.Fonts.Layout;

using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Printing;

/// <summary>A run of rows or columns that prints on one page, first to last.</summary>
internal readonly record struct Band(int First, int Last, WidePart? Part = null)
{
    /// <summary>How much of the first column earlier pages hold.</summary>
    public double Offset => Part?.Offset ?? 0;

    /// <summary>The width the band takes when it holds part of a column; 0 otherwise.</summary>
    public double Size => Part?.Size ?? 0;
}

/// <summary>The part of a column wider than the page that a band of columns starts with.</summary>
internal sealed record WidePart(double Offset, double Size);

/// <summary>What one page of a sheet prints: a band of rows by a band of columns of one print range, and the titles with them.</summary>
internal sealed record PagePlan(RangeAddress Area, Band Rows, Band Columns, bool TitleRows, bool TitleColumns);

/// <summary>
/// How a sheet prints, worked out once: the paper, the margins, the scale, the print ranges, the title rows and columns, and
/// the pages, as Excel lays them out (ECMA-376 Part 1, 18.3.1.63 and 18.3.1.62): a range too wide or tall for the paper's
/// printable area breaks before the column or row that does not fit and at each manual break, fit to pages shrinks the scale
/// to the largest whole percent that keeps the page count, and the pages go down then over unless the setup says over then down.
/// </summary>
internal sealed class SheetPaginator
{
    private const double Epsilon = 1e-6;

    /// <summary>Rows stop this far above the bottom margin, in points: half a point keeps the last row of Excel's PDF export on the next page.</summary>
    private const double PageBottomSlack = 0.5;

    /// <summary>The points of the page's width Excel keeps free when it fits a sheet to pages wide, as its PDFs measure.</summary>
    private const double FitSlack = 1.2;

    /// <summary>The room Excel leaves between a cell's edge and its text, in points.</summary>
    private const double TextPadding = 1.5;

    private readonly Dictionary<int, bool> showing = [];
    private readonly List<(int Row, int First, int Last)> runningText = [];
    private List<RangeAddress>? drawingCells;

    public SheetPaginator(Worksheet sheet, SheetGrid grid, bool ignorePrintAreas)
    {
        Sheet = sheet;
        Grid = grid;
        PageSetup setup = sheet.PageSetup;
        (double width, double height) = PaperSizes.Get(setup.PaperSize);
        if (setup.Orientation == PrintOrientation.Landscape)
        {
            (width, height) = (height, width);
        }

        PageWidth = width;
        PageHeight = height;
        PrintableWidth = Math.Max(1, width - ((setup.LeftMargin + setup.RightMargin) * 72));
        PrintableHeight = Math.Max(1, height - ((setup.TopMargin + setup.BottomMargin) * 72));
        Areas = PrintRanges(sheet, grid, ignorePrintAreas, runningText);
        TitleRows = setup.PrintTitleRows is { } rows ? new Band(rows.First.Row, rows.Last.Row) : null;
        TitleColumns = setup.PrintTitleColumns is { } columns ? new Band(columns.First.Column, columns.Last.Column) : null;
        if (setup.PrintHeadings)
        {
            int lastRow = Areas.Count == 0 ? 0 : Areas.Max(a => a.Last.Row);
            int digits = Math.Max(3, (lastRow + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            // The row numbers' digits and two thirds of a digit, in printer dots, as Excel's PDFs measure; the letters a default row.
            int digit = grid.Fonts.PrinterDigitWidth;
            HeadingWidth = ((digits * digit) + Math.Round(digit * 2 / 3.0)) * 72 / SheetFonts.PrinterDpi;
            HeadingHeight = grid.DefaultRow;
        }

        Scale = 1;
        if (setup.FitToPage)
        {
            Scale = FitScale(setup.FitToWidth, setup.FitToHeight) / 100.0;
        }
        else if (setup.Scale is { } percent)
        {
            Scale = percent / 100.0;
        }

        Grid.Scale = Scale;
        Pages = Paginate(Scale, useBreaks: !setup.FitToPage);
    }

    public Worksheet Sheet { get; }

    public SheetGrid Grid { get; }

    /// <summary>The paper's size in points, turned for landscape.</summary>
    public double PageWidth { get; }

    public double PageHeight { get; }

    /// <summary>The paper inside the margins, in points.</summary>
    public double PrintableWidth { get; }

    public double PrintableHeight { get; }

    /// <summary>The ranges that print, each starting a new page.</summary>
    public List<RangeAddress> Areas { get; }

    /// <summary>The rows printed at the top of each page, or null.</summary>
    public Band? TitleRows { get; }

    /// <summary>The columns printed at the left of each page, or null.</summary>
    public Band? TitleColumns { get; }

    /// <summary>The width of the row numbers and the height of the column letters, in sheet points; zero without headings.</summary>
    public double HeadingWidth { get; }

    public double HeadingHeight { get; }

    /// <summary>The scale the sheet prints at: 1 for 100 %.</summary>
    public double Scale { get; }

    public List<PagePlan> Pages { get; }

    /// <summary>The height the title rows take on a page whose rows start at <paramref name="first"/>, in sheet points.</summary>
    public double TitleRowsHeight(int first) => TitleRows is { } t && first > t.Last ? Grid.Rows(t.First, t.Last) : 0;

    /// <summary>The width the title columns take on a page whose columns start at <paramref name="first"/>, in sheet points.</summary>
    public double TitleColumnsWidth(int first) => TitleColumns is { } t && first > t.Last ? Grid.Columns(t.First, t.Last) : 0;

    /// <summary>
    /// What prints when there is no print area: the cells that hold a value or show a fill or a border, with the merged
    /// ranges and drawings, from their top-left to their bottom-right. A cell with only a font or an alignment prints nothing.
    /// </summary>
    private static List<RangeAddress> PrintRanges(Worksheet sheet, SheetGrid grid, bool ignorePrintAreas, List<(int Row, int First, int Last)> runningText)
    {
        if (!ignorePrintAreas && sheet.PageSetup.PrintArea is { Count: > 0 } areas)
        {
            return [.. areas.Select(a => Clip(a, sheet))];
        }

        RangeAddress? used = ShowingCells(sheet);
        foreach (RangeAddress merged in sheet.MergedCells)
        {
            used = Union(used, merged);
        }

        foreach (SheetDrawing drawing in sheet.Drawings)
        {
            used = Union(used, grid.Cells(drawing));
        }

        // Excel prints from A1, however far down and right the cells in use start.
        return used is { } range ? [new RangeAddress(0, 0, range.Last.Row, TextReach(sheet, grid, range, runningText))] : [];
    }

    /// <summary>
    /// The last column that prints: the cells in use may end before the text of a row's last cell, which runs on over the empty
    /// cells beside it, and Excel prints those columns too.
    /// </summary>
    private static int TextReach(Worksheet sheet, SheetGrid grid, RangeAddress used, List<(int Row, int First, int Last)> runningText)
    {
        // From the right, one column at a time: on most sheets the last column finds every row's last cell, and only the last
        // cells that hold text matter. Each row's entry is its last cell's column plus one, 0 when a number ends the row and there
        // is nothing to measure, and -1 until found.
        int reach = used.Last.Column;
        if (sheet.Store.OnlyNumbers(used.First.Row, used.Last.Row, reach))
        {
            return reach;
        }

        int rows = used.Last.Row - used.First.Row + 1;
        int[]? lastInRow = null;
        int missing = 0;
        int lastColumn = used.Last.Column;
        for (int i = 0; i < rows; i++)
        {
            CellKind kind = sheet.Store.Get(used.First.Row + i, lastColumn).Kind;
            if (kind is CellKind.Number or CellKind.Boolean)
            {
                continue;
            }

            lastInRow ??= new int[rows];
            lastInRow[i] = kind == CellKind.Empty ? -1 : lastColumn + 1;
            missing += kind == CellKind.Empty ? 1 : 0;
        }

        if (lastInRow is null)
        {
            return reach;
        }

        for (int column = lastColumn - 1; column >= used.First.Column && missing > 0; column--)
        {
            foreach ((int row, int found) in sheet.Store.Occupied(new RangeAddress(used.First.Row, column, used.Last.Row, column)))
            {
                int index = row - used.First.Row;
                if (lastInRow[index] == -1 && sheet.Store.Get(row, found).Kind != CellKind.Empty)
                {
                    lastInRow[index] = found + 1;
                    missing--;
                }
            }
        }

        double reachEdge = grid.Columns(0, reach);
        Workbook book = sheet.Workbook;
        StyleTable styles = book.Styles;
        for (int i = 0; i < lastInRow.Length; i++)
        {
            int row = used.First.Row + i, column = lastInRow[i] - 1;
            if (column < 0)
            {
                continue;
            }

            CellValue value = sheet.GetValue(row, column);
            if (value.Kind != CellValueKind.Text || value.Text.Length == 0 || grid.Column(column) <= 0 || grid.Row(row) <= 0)
            {
                continue;
            }

            CellStyle style = sheet.Store.Get(row, column).Style is var s && s < styles.Count ? styles[s] : CellStyle.Default;
            CellAlignment alignment = style.Alignment;
            double start = grid.Columns(0, column - 1);
            if (value.Text.Length * style.Font.Size <= grid.Column(column) || alignment.WrapText || alignment.ShrinkToFit || alignment.TextRotation != 0
                || alignment.Horizontal is not (CellHorizontalAlignment.General or CellHorizontalAlignment.Left or CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous)
                || alignment.Vertical is CellVerticalAlignment.Justify or CellVerticalAlignment.Distributed || InMerge(sheet, row, column))
            {
                continue;
            }

            // A character is at most its font's size wide, so text that fits by that isn't measured. A cell shows its first 1,024
            // characters.
            double width = TextWidth(grid, value.Text, style) + TextPadding;
            if (alignment.Horizontal is CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous)
            {
                width = (width + grid.Column(column)) / 2;
            }

            double edge = start + width;
            while (reachEdge < edge && reach < CellAddress.MaxColumns - 1)
            {
                reach++;
                reachEdge += grid.Column(reach);
            }

            int end = column;
            for (double x = start + grid.Column(column); x < edge && end < CellAddress.MaxColumns - 1; end++)
            {
                x += grid.Column(end + 1);
            }

            if (end > column)
            {
                runningText.Add((row, column, end));
            }
        }

        return reach;
    }

    private static bool InMerge(Worksheet sheet, int row, int column)
    {
        foreach (RangeAddress merged in sheet.MergedCells)
        {
            if (merged.Contains(new CellAddress(row, column)))
            {
                return true;
            }
        }

        return false;
    }

    private static RangeAddress? ShowingCells(Worksheet sheet)
    {
        if (sheet.Store.Bounds() is not { } bounds)
        {
            return null;
        }

        Dictionary<int, bool> showing = [];

        // Most sheets show something in their last row and last column, and then no other cell can change the answer.
        RangeAddress lastRow = new(bounds.Last.Row, bounds.First.Column, bounds.Last.Row, bounds.Last.Column);
        RangeAddress lastColumn = new(bounds.First.Row, bounds.Last.Column, bounds.Last.Row, bounds.Last.Column);
        if (AnyShows(lastRow) && AnyShows(lastColumn))
        {
            return bounds;
        }

        int top = int.MaxValue, left = int.MaxValue, bottom = -1, right = -1;
        foreach ((int row, int column) in sheet.Store.Occupied(bounds))
        {
            if (Shows(sheet, row, column, showing))
            {
                top = Math.Min(top, row);
                left = Math.Min(left, column);
                bottom = Math.Max(bottom, row);
                right = Math.Max(right, column);
            }
        }

        return bottom < 0 ? null : new RangeAddress(top, left, bottom, right);

        bool AnyShows(RangeAddress range)
        {
            foreach ((int row, int column) in sheet.Store.Occupied(range))
            {
                if (Shows(sheet, row, column, showing))
                {
                    return true;
                }
            }

            return false;
        }

    }

    /// <summary>Whether a cell puts something on paper: a value, a fill or a border. <paramref name="showing"/> holds the answer by style.</summary>
    private static bool Shows(Worksheet sheet, int row, int column, Dictionary<int, bool> showing)
    {
        ref readonly CellRecord record = ref sheet.Store.Get(row, column);
        if (record.Kind != CellKind.Empty)
        {
            return true;
        }

        if (!showing.TryGetValue(record.Style, out bool shows))
        {
            StyleTable styles = sheet.Workbook.Styles;
            CellStyle style = record.Style < styles.Count ? styles[record.Style] : CellStyle.Default;
            shows = style.Fill != CellFill.None || style.Borders != CellBorders.None;
            showing[record.Style] = shows;
        }

        return shows;
    }

    /// <summary>A whole-row or whole-column print area cut to the cells in use, as Excel prints it.</summary>
    private static RangeAddress Clip(RangeAddress area, Worksheet sheet)
    {
        if (!area.IsWholeRows && !area.IsWholeColumns)
        {
            return area;
        }

        RangeAddress used = sheet.Store.Bounds() ?? new RangeAddress(0, 0, 0, 0);
        int lastRow = area.IsWholeColumns ? Math.Max(area.First.Row, used.Last.Row) : area.Last.Row;
        int lastColumn = area.IsWholeRows ? Math.Max(area.First.Column, used.Last.Column) : area.Last.Column;
        return new RangeAddress(area.First.Row, area.First.Column, Math.Min(lastRow, area.Last.Row), Math.Min(lastColumn, area.Last.Column));
    }

    private static RangeAddress Union(RangeAddress? a, RangeAddress b) => a is not { } r ? b : new RangeAddress(
        Math.Min(r.First.Row, b.First.Row), Math.Min(r.First.Column, b.First.Column), Math.Max(r.Last.Row, b.Last.Row), Math.Max(r.Last.Column, b.Last.Column));

    /// <summary>The largest whole percent from 10 to 100 at which the ranges fit the pages asked for; 0 for a count means any.</summary>
    private int FitScale(int wide, int tall)
    {
        if (Fits(100))
        {
            return 100;
        }

        int low = 10, high = 99;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Fits(middle))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;

        bool Fits(int percent)
        {
            double scale = percent / 100.0;
            Grid.Scale = scale;
            foreach (RangeAddress area in Areas)
            {
                if ((wide > 0 && ColumnBands(area, scale, useBreaks: false, FitSlack).Count > wide) || (tall > 0 && RowBands(area, scale, useBreaks: false).Count > tall))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private List<PagePlan> Paginate(double scale, bool useBreaks)
    {
        List<PagePlan> pages = [];
        PageSetup setup = Sheet.PageSetup;
        bool overThenDown = setup.PageOrder == PrintPageOrder.OverThenDown;

        // Excel leaves out the empty pages at the end, while empty pages before the last with something on it print. A print
        // area, gridlines and headings print every page.
        bool keepAll = setup.PrintArea.Count > 0 || setup.PrintGridLines || setup.PrintHeadings;
        foreach (RangeAddress area in Areas)
        {
            List<Band> rows = RowBands(area, scale, useBreaks);
            List<Band> columns = ColumnBands(area, scale, useBreaks);
            Band RowOf(int page) => overThenDown ? rows[page / columns.Count] : rows[page % rows.Count];
            Band ColumnOf(int page) => overThenDown ? columns[page % columns.Count] : columns[page / rows.Count];

            int count = rows.Count * columns.Count;
            while (!keepAll && count > 1 && !HasContent(RowOf(count - 1), ColumnOf(count - 1)))
            {
                count--;
            }

            pages.EnsureCapacity(pages.Count + count);
            for (int page = 0; page < count; page++)
            {
                pages.Add(Plan(area, RowOf(page), ColumnOf(page)));
            }
        }

        return pages;
    }

    /// <summary>Whether a cell shows past a point in its own column, on a later page of a column wider than the page.</summary>
    private bool ReachesOffset(int row, int column, double offset)
    {
        CellValue value = Sheet.GetValue(row, column);
        StyleTable styles = Sheet.Workbook.Styles;
        CellStyle style = Sheet.Store.Get(row, column).Style is var s && s < styles.Count ? styles[s] : CellStyle.Default;
        if (value.Kind == CellValueKind.Empty || style.Alignment.WrapText || style.Alignment.Horizontal is CellHorizontalAlignment.Justify or CellHorizontalAlignment.Distributed)
        {
            return false;
        }

        if (value.Kind != CellValueKind.Text || style.Alignment.Horizontal is not (CellHorizontalAlignment.General or CellHorizontalAlignment.Left))
        {
            return true;
        }

        return TextWidth(Grid, value.Text, style) + TextPadding > offset;
    }

    /// <summary>The width of a cell's text on one line: a cell shows its first 1,024 characters.</summary>
    private static double TextWidth(SheetGrid grid, string value, CellStyle style)
    {
        string text = value[..Math.Min(value.Length, 1024)].Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        ParagraphStyle paragraph = new() { DefaultStyle = grid.Fonts.TextStyle(style.Font, default), Fallback = grid.Fonts.Fallback };
        return Paragraph.Create(text, paragraph).Layout(1e6f).LongestLine;
    }

    private bool HasContent(Band rows, Band columns)
    {
        RangeAddress page = new(rows.First, columns.First, rows.Last, columns.Last);
        foreach ((int row, int column) in Sheet.Store.Occupied(page))
        {
            if (Grid.Column(column) > 0 && Grid.Row(row) > 0 && Shows(Sheet, row, column, showing) && (columns.Offset == 0 || column != columns.First || ReachesOffset(row, column, columns.Offset)))
            {
                return true;
            }
        }

        foreach (RangeAddress merged in Sheet.MergedCells)
        {
            if (merged.Intersects(page) && (columns.Offset == 0 || merged.Last.Column > columns.First))
            {
                return true;
            }
        }

        drawingCells ??= [.. Sheet.Drawings.Select(Grid.Cells)];
        foreach (RangeAddress cells in drawingCells)
        {
            if (cells.Intersects(page))
            {
                return true;
            }
        }

        foreach ((int row, int first, int last) in runningText)
        {
            if (row >= rows.First && row <= rows.Last && first <= columns.Last && last >= columns.First)
            {
                return true;
            }
        }

        return false;
    }

    private PagePlan Plan(RangeAddress area, Band rows, Band columns) =>
        new(area, rows, columns, TitleRows is { } r && rows.First > r.Last, TitleColumns is { } c && columns.First > c.Last);

    private List<Band> RowBands(RangeAddress area, double scale, bool useBreaks) =>
        Bands(area.First.Row, area.Last.Row, ((PrintableHeight - PageBottomSlack) / scale) - HeadingHeight, Grid.Row, TitleRowsHeight,
            useBreaks ? Sheet.PageSetup.RowBreaks : null, every: Grid.EveryRow);

    private List<Band> ColumnBands(RangeAddress area, double scale, bool useBreaks, double slack = 0) =>
        Bands(area.First.Column, area.Last.Column, ((PrintableWidth - slack) / scale) - HeadingWidth, Grid.Column, TitleColumnsWidth,
            useBreaks ? Sheet.PageSetup.ColumnBreaks : null, splitWide: true);

    /// <summary>
    /// The runs of <paramref name="first"/> to <paramref name="last"/> that fit <paramref name="available"/> points each, less
    /// the titles a run starting there repeats; each run holds at least one row or column, and a manual break starts a new one.
    /// </summary>
    private static List<Band> Bands(int first, int last, double available, Func<int, double> size, Func<int, double> titles, PageBreakCollection? breaks, bool splitWide = false, double every = -1)
    {
        List<Band> bands = [];
        int start = first;
        double skip = 0;
        while (start <= last)
        {
            double room = available - titles(start);
            double used = 0;
            int i = start;
            while (i <= last)
            {
                if (i > start && breaks is not null && breaks.Contains(i))
                {
                    break;
                }

                double next = every >= 0 ? every : i == start ? size(i) - skip : size(i);
                if (used > 0 && used + next > room + Epsilon)
                {
                    break;
                }

                used += next;
                i++;
            }

            if (used == 0 && bands.Count > 0 && (breaks is null || !breaks.Contains(start)))
            {
                // Hidden rows or columns at the end belong to the page before.
                bands[^1] = bands[^1] with { Last = i - 1 };
            }
            else if (splitWide && i - 1 == start && used > room + Epsilon && room > 0)
            {
                // A column wider than the page fills it and goes on over: its last part shares a page with the next columns.
                bands.Add(new Band(start, start, new WidePart(skip, room)));
                skip += room;
                continue;
            }
            else
            {
                bands.Add(new Band(start, i - 1, skip > 0 ? new WidePart(skip, used) : null));
            }

            skip = 0;
            start = i;
        }

        return bands;
    }
}
