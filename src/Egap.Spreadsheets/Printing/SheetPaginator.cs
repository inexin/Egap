using Egami.Fonts.Layout;
using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Printing;

/// <summary>A run of rows or columns that prints on one page, first to last.</summary>
internal readonly record struct Band(int First, int Last);

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

    /// <summary>The room Excel leaves between a cell's edge and its text, in points.</summary>
    private const double TextPadding = 1.5;

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
        Areas = PrintRanges(sheet, grid, ignorePrintAreas);
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
    private static List<RangeAddress> PrintRanges(Worksheet sheet, SheetGrid grid, bool ignorePrintAreas)
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
        return used is { } range ? [new RangeAddress(0, 0, range.Last.Row, TextReach(sheet, grid, range))] : [];
    }

    /// <summary>
    /// The last column that prints: the cells in use may end before the text of a row's last cell, which runs on over the empty
    /// cells beside it, and Excel prints those columns too.
    /// </summary>
    private static int TextReach(Worksheet sheet, SheetGrid grid, RangeAddress used)
    {
        if (sheet.Store.Bounds() is not { } bounds)
        {
            return used.Last.Column;
        }

        int[] lastInRow = new int[bounds.Last.Row - bounds.First.Row + 1];
        Array.Fill(lastInRow, -1);
        foreach ((int row, int column) in sheet.Store.Occupied(bounds))
        {
            int index = row - bounds.First.Row;
            if (column > lastInRow[index] && sheet.Store.Get(row, column).Kind != CellKind.Empty)
            {
                lastInRow[index] = column;
            }
        }

        // A character is at least a fifth of its font's size wide, so most text is seen not to reach without measuring it.
        const double NarrowestCharacter = 0.2;
        int reach = used.Last.Column;
        double reachEdge = grid.Columns(0, reach);
        Workbook book = sheet.Workbook;
        StyleTable styles = book.Styles;
        for (int i = 0; i < lastInRow.Length; i++)
        {
            int row = bounds.First.Row + i, column = lastInRow[i];
            if (column < 0)
            {
                continue;
            }

            CellValue value = sheet.GetValue(row, column);
            if (value.Kind != CellValueKind.Text || value.Text.Length == 0)
            {
                continue;
            }

            CellStyle style = sheet.Store.Get(row, column).Style is var s && s < styles.Count ? styles[s] : CellStyle.Default;
            CellAlignment alignment = style.Alignment;
            double start = grid.Columns(0, column - 1);
            double room = reachEdge - start;
            if (value.Text.Length * style.Font.Size * NarrowestCharacter <= room || alignment.WrapText || alignment.ShrinkToFit || alignment.TextRotation != 0
                || alignment.Horizontal is not (CellHorizontalAlignment.General or CellHorizontalAlignment.Left or CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous)
                || alignment.Vertical is CellVerticalAlignment.Justify or CellVerticalAlignment.Distributed || InMerge(sheet, row, column))
            {
                continue;
            }

            // A cell shows its first 1,024 characters.
            string text = value.Text[..Math.Min(value.Text.Length, 1024)].Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
            ParagraphStyle paragraph = new() { DefaultStyle = grid.Fonts.TextStyle(style.Font, default), Fallback = grid.Fonts.Fallback };
            double width = Paragraph.Create(text, paragraph).Layout(1e6f).LongestLine + (2 * TextPadding);
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

        StyleTable styles = sheet.Workbook.Styles;
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
            if (Shows(row, column))
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
                if (Shows(row, column))
                {
                    return true;
                }
            }

            return false;
        }

        bool Shows(int row, int column)
        {
            ref readonly CellRecord record = ref sheet.Store.Get(row, column);
            if (record.Kind != CellKind.Empty)
            {
                return true;
            }

            if (!showing.TryGetValue(record.Style, out bool shows))
            {
                CellStyle style = record.Style < styles.Count ? styles[record.Style] : CellStyle.Default;
                shows = style.Fill != CellFill.None || style.Borders != CellBorders.None;
                showing[record.Style] = shows;
            }

            return shows;
        }
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
                if ((wide > 0 && ColumnBands(area, scale, useBreaks: false).Count > wide) || (tall > 0 && RowBands(area, scale, useBreaks: false).Count > tall))
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
        bool overThenDown = Sheet.PageSetup.PageOrder == PrintPageOrder.OverThenDown;
        foreach (RangeAddress area in Areas)
        {
            List<Band> rows = RowBands(area, scale, useBreaks);
            List<Band> columns = ColumnBands(area, scale, useBreaks);
            if (overThenDown)
            {
                foreach (Band row in rows)
                {
                    foreach (Band column in columns)
                    {
                        pages.Add(Plan(area, row, column));
                    }
                }
            }
            else
            {
                foreach (Band column in columns)
                {
                    foreach (Band row in rows)
                    {
                        pages.Add(Plan(area, row, column));
                    }
                }
            }
        }

        return pages;
    }

    private PagePlan Plan(RangeAddress area, Band rows, Band columns) =>
        new(area, rows, columns, TitleRows is { } r && rows.First > r.Last, TitleColumns is { } c && columns.First > c.Last);

    private List<Band> RowBands(RangeAddress area, double scale, bool useBreaks) =>
        Bands(area.First.Row, area.Last.Row, ((PrintableHeight - PageBottomSlack) / scale) - HeadingHeight, Grid.Row, TitleRowsHeight,
            useBreaks ? Sheet.PageSetup.RowBreaks : null);

    private List<Band> ColumnBands(RangeAddress area, double scale, bool useBreaks) =>
        Bands(area.First.Column, area.Last.Column, (PrintableWidth / scale) - HeadingWidth, Grid.Column, TitleColumnsWidth,
            useBreaks ? Sheet.PageSetup.ColumnBreaks : null);

    /// <summary>
    /// The runs of <paramref name="first"/> to <paramref name="last"/> that fit <paramref name="available"/> points each, less
    /// the titles a run starting there repeats; each run holds at least one row or column, and a manual break starts a new one.
    /// </summary>
    private static List<Band> Bands(int first, int last, double available, Func<int, double> size, Func<int, double> titles, PageBreakCollection? breaks)
    {
        List<Band> bands = [];
        int start = first;
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

                double next = size(i);
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
            else
            {
                bands.Add(new Band(start, i - 1));
            }

            start = i;
        }

        return bands;
    }
}
