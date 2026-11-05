namespace Egap.Spreadsheets.Printing;

/// <summary>
/// A sheet's row heights and column widths in points as it prints: column widths through the normal font's maximum digit
/// width at 96 dpi, row heights as set or the sheet's default, hidden rows and columns zero. Excel prints both a little
/// larger than they show: a column's pixels count in the printer's digit width, rounded to whole dots, and rows grow by
/// <see cref="RowScale"/>, as Excel's own PDFs measure. Each row and column prints a whole number of printer dots at the
/// print's <see cref="Scale"/>, so a scaled sheet fits as many rows as Excel fits.
/// </summary>
internal sealed class SheetGrid
{
    private readonly Worksheet sheet;
    private readonly int[] columns;
    private readonly int defaultRow;
    private int[]? specialRows;

    /// <summary>How much taller a row prints than its height, measured on Excel's PDF export at 600 dpi.</summary>
    public const double RowScale = 1.016;

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

        defaultRow = RowDots(sheet.DefaultRowHeight ?? 15);
    }

    /// <summary>The printed height of a row of the sheet's default height, in points at 100 %.</summary>
    public double DefaultRow => defaultRow * 72.0 / SheetFonts.PrinterDpi;

    /// <summary>The scale the sheet prints at, 1 for 100 %: rows and columns round to whole dots at it.</summary>
    public double Scale { get; set; } = 1;

    public SheetFonts Fonts { get; }

    public Worksheet Sheet => sheet;

    /// <summary>The width of column <paramref name="column"/> in points; zero when hidden.</summary>
    public double Column(int column) => Points(columns[column]);

    /// <summary>The height of row <paramref name="row"/> in points; zero when hidden.</summary>
    public double Row(int row)
    {
        if (!sheet.RowRecords.TryGetValue(row, out RowRecord? record))
        {
            return Points(defaultRow);
        }

        return record.Hidden ? 0 : Points(double.IsNaN(record.Height) ? defaultRow : RowDots(record.Height));
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
        specialRows ??= [.. sheet.RowRecords.Keys.Order()];
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

    /// <summary>A row of <paramref name="height"/> points in printer dots.</summary>
    private static int RowDots(double height) => (int)Math.Round(height * RowScale * SheetFonts.PrinterDpi / 72);

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
