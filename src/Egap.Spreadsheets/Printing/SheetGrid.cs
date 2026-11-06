namespace Egap.Spreadsheets.Printing;

/// <summary>
/// A sheet's row heights and column widths in points as it prints: column widths through the normal font's maximum digit
/// width at 96 dpi, row heights as set or the sheet's default, hidden rows and columns zero. Excel prints neither at the
/// size it shows: a column's pixels count in the printer's digit width, rounded to whole dots, and a row's in the normal
/// font's printed row height over its row height on screen (<see cref="PrintedRows"/>), as Excel's own PDFs measure. Each row and column prints a whole number of printer dots at the
/// print's <see cref="Scale"/>, so a scaled sheet fits as many rows as Excel fits.
/// </summary>
internal sealed class SheetGrid
{
    private readonly Worksheet sheet;
    private readonly int[] columns;
    private readonly int defaultRow;
    private readonly Dictionary<int, int>? fittedRows;
    private int[]? specialRows;
    private readonly double rowDotsPerPixel;

    public SheetGrid(Worksheet sheet, SheetFonts fonts)
    {
        this.sheet = sheet;
        Fonts = fonts;
        int defaultColumn = Printed(fonts.DefaultColumnPixels(sheet), fonts);
        columns = new int[CellAddress.MaxColumns];
        Array.Fill(columns, defaultColumn);
        foreach (ColumnSpan span in sheet.ColumnSpans.Spans)
        {
            int width = span.Hidden ? 0 : double.IsNaN(span.Width) ? defaultColumn : Printed(fonts.ColumnPixels(span.Width), fonts);
            for (int c = span.First; c <= span.Last; c++)
            {
                columns[c] = width;
            }
        }

        if (sheet.View.ShowFormulas)
        {
            for (int c = 0; c < columns.Length; c++)
            {
                columns[c] *= 2;
            }
        }

        PrintedRow? row = PrintedRows.Find(fonts.NormalFont.Name, fonts.NormalFont.Size);
        rowDotsPerPixel = row is { } known ? (double)known.Dots / known.Pixels : PrintedRows.UsualDotsPerPixel;
        defaultRow = row is { } normal && (sheet.DefaultRowHeight is null || sheet.DefaultRowFollowsFont)
            ? normal.Dots
            : RowDots(sheet.DefaultRowHeight ?? 15);
        if (row is { } own && StyledColumnsRow(sheet) is { } pixels && pixels < own.Pixels)
        {
            defaultRow = (int)((pixels * rowDotsPerPixel) + 0.2);
        }

        fittedRows = row is { } usual ? FittedRows(sheet, usual.Pixels) : null;
    }

    /// <summary>
    /// The rows with no height in the file that hold a larger font than the normal one, in dots: Excel fits such a row as it
    /// opens the file. Null when no style has such a font, as the file's own rows mostly say their height.
    /// </summary>
    private Dictionary<int, int>? FittedRows(Worksheet sheet, int usual)
    {
        var styles = sheet.Workbook.Styles;
        int[] pixels = new int[styles.Count];
        bool any = false;
        for (int i = 0; i < pixels.Length; i++)
        {
            CellFont font = styles[i].Font;
            pixels[i] = PrintedRows.Find(font.Name, font.Size) is { } row ? row.Pixels : 0;
            any |= pixels[i] > usual;
        }

        if (!any || sheet.Store.Bounds() is not { } bounds)
        {
            return null;
        }

        Dictionary<int, int> rows = [];
        foreach ((int row, int column) in sheet.Store.Occupied(bounds))
        {
            int style = sheet.Store.Get(row, column).Style;
            if (style < pixels.Length && pixels[style] > usual
                && (!sheet.RowRecords.TryGetValue(row, out RowRecord? record) || (double.IsNaN(record.Height) && double.IsNaN(record.FittedHeight))))
            {
                int dots = Math.Max(defaultRow, (int)((pixels[style] * rowDotsPerPixel) + 0.2));
                rows[row] = rows.TryGetValue(row, out int known) ? Math.Max(known, dots) : dots;
            }
        }

        return rows;
    }

    /// <summary>
    /// The row in pixels that the columns' font calls for when every column of the sheet has one, or null: Excel fits an empty
    /// row to the font of its cells, so a smaller font on all the columns makes for lower rows than the normal font's.
    /// </summary>
    private static int? StyledColumnsRow(Worksheet sheet)
    {
        var styles = sheet.Workbook.Styles;
        int next = 0, tallest = 0;
        foreach (ColumnSpan span in sheet.ColumnSpans.Spans)
        {
            if (span.First != next || span.Style < 0 || span.Style >= styles.Count)
            {
                return null;
            }

            CellFont font = styles[span.Style].Font;
            if (PrintedRows.Find(font.Name, font.Size) is not { } row)
            {
                return null;
            }

            tallest = Math.Max(tallest, row.Pixels);
            next = span.Last + 1;
        }

        return next == CellAddress.MaxColumns ? tallest : null;
    }

    /// <summary>The printed height of a row of the sheet's default height, in points at 100 %.</summary>
    public double DefaultRow => defaultRow * 72.0 / SheetFonts.PrinterDpi;

    /// <summary>The scale the sheet prints at, 1 for 100 %: rows and columns round to whole dots at it.</summary>
    public double Scale { get; set; } = 1;

    public SheetFonts Fonts { get; }

    public Worksheet Sheet => sheet;

    /// <summary>The width of column <paramref name="column"/> in points; zero when hidden.</summary>
    public double Column(int column) => Points(columns[column]);

    /// <summary>The height of every row in points when no row has its own, as on most sheets made by a program; otherwise -1.</summary>
    public double EveryRow => fittedRows is null && sheet.RowRecords.Count == 0 ? Points(defaultRow) : -1;

    /// <summary>The height of row <paramref name="row"/> in points; zero when hidden.</summary>
    public double Row(int row)
    {
        if (fittedRows is not null && fittedRows.TryGetValue(row, out int fitted) && !(sheet.RowRecords.TryGetValue(row, out RowRecord? own) && own.Hidden))
        {
            return Points(fitted);
        }

        if (!sheet.RowRecords.TryGetValue(row, out RowRecord? record))
        {
            return Points(defaultRow);
        }

        if (record.Hidden)
        {
            return 0;
        }

        double height = double.IsNaN(record.Height) ? record.FittedHeight : record.Height;
        return Points(double.IsNaN(height) ? defaultRow : RowDots(height));
    }

    /// <summary>The width of columns <paramref name="first"/> to <paramref name="last"/> together; negative when last comes first.</summary>
    public double Columns(int first, int last)
    {
        if (last == first - 1)
        {
            return 0;
        }

        if (last < first)
        {
            return -Columns(last + 1, first - 1);
        }

        double total = 0;
        for (int c = Math.Max(0, first); c <= Math.Min(last, CellAddress.MaxColumns - 1); c++)
        {
            total += Points(columns[c]);
        }

        return total;
    }

    /// <summary>The height of rows <paramref name="first"/> to <paramref name="last"/> together; negative when last comes first.</summary>
    public double Rows(int first, int last)
    {
        if (last == first - 1)
        {
            return 0;
        }

        if (last < first)
        {
            return -Rows(last + 1, first - 1);
        }

        first = Math.Max(0, first);
        last = Math.Min(last, CellAddress.MaxRows - 1);
        if (last - first < 4096)
        {
            double sum = 0;
            for (int r = first; r <= last; r++)
            {
                sum += Row(r);
            }

            return sum;
        }

        // A long run: the default height times the rows, corrected for the rows that have their own.
        specialRows ??= [.. sheet.RowRecords.Keys.Union(fittedRows?.Keys ?? Enumerable.Empty<int>()).Order()];
        double row = Points(defaultRow);
        double total = (last - first + 1) * row;
        int start = Array.BinarySearch(specialRows, first);
        for (int i = start < 0 ? ~start : start; i < specialRows.Length && specialRows[i] <= last; i++)
        {
            total += Row(specialRows[i]) - row;
        }

        return total;
    }

    /// <summary>The place of a drawing anchor in points from the sheet's top-left corner.</summary>
    public (double X, double Y) Position(DrawingPosition position) =>
        (Columns(0, position.Cell.Column - 1) + Emu(position.OffsetX), Rows(0, position.Cell.Row - 1) + Emu(position.OffsetY));

    /// <summary>A drawing's rectangle in points from the sheet's top-left corner.</summary>
    public (double X, double Y, double Width, double Height) Bounds(SheetDrawing drawing)
    {
        switch (drawing.AnchorKind)
        {
            case DrawingAnchorKind.TwoCell:
                (double x1, double y1) = Position(drawing.From);
                (double x2, double y2) = Position(drawing.To);
                return (x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
            case DrawingAnchorKind.OneCell:
                (double x, double y) = Position(drawing.From);
                return (x, y, Emu(drawing.Width), Emu(drawing.Height));
            default:
                return (Emu(drawing.X), Emu(drawing.Y), Emu(drawing.Width), Emu(drawing.Height));
        }
    }

    /// <summary>The cells a drawing covers.</summary>
    public RangeAddress Cells(SheetDrawing drawing)
    {
        if (drawing.AnchorKind == DrawingAnchorKind.TwoCell)
        {
            return new RangeAddress(drawing.From.Cell, new CellAddress(Math.Max(drawing.From.Cell.Row, drawing.To.Cell.Row), Math.Max(drawing.From.Cell.Column, drawing.To.Cell.Column)));
        }

        (double x, double y, double width, double height) = Bounds(drawing);
        return new RangeAddress(RowAt(y), ColumnAt(x), RowAt(y + height), ColumnAt(x + width));
    }

    /// <summary>A column of <paramref name="pixels"/> screen pixels in printer dots: pixels count in the printer's digit width.</summary>
    private static int Printed(int pixels, SheetFonts fonts) =>
        (int)Math.Round((double)pixels * fonts.PrinterDigitWidth / fonts.MaxDigitWidth);

    /// <summary>A row of <paramref name="height"/> points in printer dots; Excel drops the fraction, all but the last fifth.</summary>
    private int RowDots(double height) => (int)((height / SheetFonts.PointsPerPixel * rowDotsPerPixel) + 0.2);

    /// <summary>Printer dots in sheet points at <see cref="Scale"/>: whole dots on paper, divided back by the scale.</summary>
    private double Points(int dots) => dots == 0 ? 0 : Math.Round(dots * Scale) * 72 / SheetFonts.PrinterDpi / Scale;

    /// <summary>EMUs in points: 12,700 to the point.</summary>
    public static double Emu(long emu) => emu / 12700.0;

    private int ColumnAt(double x)
    {
        int c = 0;
        while (c < CellAddress.MaxColumns - 1 && x > Column(c))
        {
            x -= Column(c);
            c++;
        }

        return c;
    }

    private int RowAt(double y)
    {
        int r = 0;
        while (r < CellAddress.MaxRows - 1 && y > Row(r))
        {
            y -= Row(r);
            r++;
        }

        return r;
    }
}
