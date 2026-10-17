using System.Globalization;

using Egap.Pdf.Fonts;

namespace Egap.Pdf;

/// <summary>A sheet size in points, such as <see cref="A4"/>.</summary>
/// <param name="Width">The width in points.</param>
/// <param name="Height">The height in points.</param>
public readonly record struct PdfPageSize(double Width, double Height)
{
    /// <summary>ISO A3, 297 by 420 mm.</summary>
    public static PdfPageSize A3 { get; } = new(841.89, 1190.55);

    /// <summary>ISO A4, 210 by 297 mm.</summary>
    public static PdfPageSize A4 { get; } = new(595.28, 841.89);

    /// <summary>ISO A5, 148 by 210 mm.</summary>
    public static PdfPageSize A5 { get; } = new(419.53, 595.28);

    /// <summary>US Letter, 8.5 by 11 inches.</summary>
    public static PdfPageSize Letter { get; } = new(612, 792);

    /// <summary>US Legal, 8.5 by 14 inches.</summary>
    public static PdfPageSize Legal { get; } = new(612, 1008);

    /// <summary>US Tabloid, 11 by 17 inches.</summary>
    public static PdfPageSize Tabloid { get; } = new(792, 1224);

    /// <summary>The same size turned a quarter: width and height swapped.</summary>
    public PdfPageSize Turned => new(Height, Width);

    /// <summary>The size as a rectangle from the origin.</summary>
    public PdfRectangle ToRectangle() => new(0, 0, Width, Height);
}

/// <summary>The order pages fill the cells of an N-up sheet.</summary>
public enum NUpOrder
{
    /// <summary>Rows from the top, each from the left: as text reads in English.</summary>
    LeftToRightThenDown,

    /// <summary>Rows from the top, each from the right: as text reads in Arabic or Hebrew.</summary>
    RightToLeftThenDown,

    /// <summary>Columns from the left, each from the top.</summary>
    DownThenLeftToRight,

    /// <summary>Columns from the right, each from the top: as vertical Japanese reads.</summary>
    DownThenRightToLeft,
}

/// <summary>Which edge a booklet is bound on.</summary>
public enum BookletBinding
{
    /// <summary>The left edge, for left-to-right reading.</summary>
    Left,

    /// <summary>The right edge, for right-to-left reading.</summary>
    Right,
}

/// <summary>How <see cref="PdfImpose.NUp"/> lays pages out (Phase 7 design Q9).</summary>
public sealed record NUpOptions
{
    /// <summary>Gets the cells across. Defaults to 2.</summary>
    public int Columns { get; init; } = 2;

    /// <summary>Gets the cells down. Defaults to 2.</summary>
    public int Rows { get; init; } = 2;

    /// <summary>Gets the sheet size. Defaults to A4.</summary>
    public PdfPageSize Sheet { get; init; } = PdfPageSize.A4;

    /// <summary>Gets the order pages fill the cells.</summary>
    public NUpOrder Order { get; init; }

    /// <summary>Gets the space kept free at the sheet's edges, in points. Defaults to 18.</summary>
    public double Margin { get; init; } = 18;

    /// <summary>Gets the space between cells, in points. Defaults to 12.</summary>
    public double Gutter { get; init; } = 12;

    /// <summary>Gets whether each placed page gets a thin border. Defaults to false.</summary>
    public bool Border { get; init; }

    /// <summary>Gets whether the sheet is turned when the pages fit larger that way. Defaults to true.</summary>
    public bool AutoRotate { get; init; } = true;
}

/// <summary>How <see cref="PdfImpose.Grid"/> puts every page on one sheet.</summary>
public sealed record GridOptions
{
    /// <summary>Gets the cells across; 0 (the default) for as near a square as the page count allows.</summary>
    public int Columns { get; init; }

    /// <summary>Gets the space kept free at the sheet's edges, in points. Defaults to 18.</summary>
    public double Margin { get; init; } = 18;

    /// <summary>Gets the space between pages, in points. Defaults to 12.</summary>
    public double Gutter { get; init; } = 12;

    /// <summary>Gets whether each page gets a thin border. Defaults to false.</summary>
    public bool Border { get; init; }
}

/// <summary>How <see cref="PdfImpose.Booklet"/> lays a saddle-stitched booklet out.</summary>
public sealed record BookletOptions
{
    /// <summary>Gets the sheet size, used turned so two pages stand side by side. Defaults to A4.</summary>
    public PdfPageSize Sheet { get; init; } = PdfPageSize.A4;

    /// <summary>Gets the edge the booklet is bound on. Defaults to the left.</summary>
    public BookletBinding Binding { get; init; }

    /// <summary>
    /// Gets how many sheets are folded together into one signature; 0 (the default) folds them all into one. Each signature
    /// is padded to whole sheets.
    /// </summary>
    public int SheetsPerSignature { get; init; }

    /// <summary>Gets the space kept free at the sheet's outer edges, in points. Defaults to 18.</summary>
    public double Margin { get; init; } = 18;
}

/// <summary>How <see cref="PdfImpose.Poster"/> cuts pages into tiles.</summary>
public sealed record PosterOptions
{
    /// <summary>Gets the tile size each sheet holds. Defaults to A4.</summary>
    public PdfPageSize Tile { get; init; } = PdfPageSize.A4;

    /// <summary>Gets how many times the page is enlarged before it is cut. Defaults to 1.</summary>
    public double Scale { get; init; } = 1;

    /// <summary>Gets how far neighbouring tiles overlap, in points, for gluing. Defaults to 0.</summary>
    public double Overlap { get; init; }

    /// <summary>Gets whether cut marks show where each tile's own part ends. Defaults to false.</summary>
    public bool CutMarks { get; init; }

    /// <summary>Gets whether each tile is labelled with its page, row and column, such as <c>1 A2</c>. Defaults to false.</summary>
    public bool Labels { get; init; }
}

/// <summary>
/// Imposition (Phase 7 design 7.2, Q9): new documents whose sheets draw the source's pages as form XObjects, placed and
/// scaled. The source is never changed; it must stay open until the result is saved, which reads the pages' streams then.
/// Every layout keeps each page's content, resources and printable annotations, shared once per source.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument handout = PdfImpose.NUp(slides, new NUpOptions { Columns = 2, Rows = 3, Border = true });
/// using PdfDocument booklet = PdfImpose.Booklet(manual, new BookletOptions { Sheet = PdfPageSize.A4 });
/// handout.Save("handout.pdf");
/// </code>
/// </example>
public static class PdfImpose
{
    private const double BorderWidth = 0.5;

    /// <summary>Puts <see cref="NUpOptions.Columns"/> by <see cref="NUpOptions.Rows"/> pages on each sheet, each scaled to fit its cell.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count or size is not positive, or the margins leave no room.</exception>
    public static PdfDocument NUp(PdfDocument source, NUpOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new NUpOptions();
        Positive(options.Columns, nameof(options.Columns));
        Positive(options.Rows, nameof(options.Rows));
        PdfPageSize sheet = Checked(options.Sheet);
        PdfDocument target = PdfDocument.Create();
        Placer placer = new(source, target);
        if (options.AutoRotate && source.Pages.Count > 0)
        {
            (double w, double h) = placer.Size(0);
            double Fit(PdfPageSize s) => Math.Min(Cell(s.Width, options.Columns, options.Margin, options.Gutter) / w, Cell(s.Height, options.Rows, options.Margin, options.Gutter) / h);
            sheet = Fit(sheet.Turned) > Fit(sheet) * 1.0001 ? sheet.Turned : sheet;
        }

        double cellWidth = Cell(sheet.Width, options.Columns, options.Margin, options.Gutter);
        double cellHeight = Cell(sheet.Height, options.Rows, options.Margin, options.Gutter);
        int perSheet = options.Columns * options.Rows;
        for (int first = 0; first < source.Pages.Count; first += perSheet)
        {
            PdfPage page = target.Pages.AddBlank(sheet.ToRectangle());
            Sheet drawing = new(page);
            for (int k = 0; k < perSheet && first + k < source.Pages.Count; k++)
            {
                (int column, int row) = Slot(k, options.Columns, options.Rows, options.Order);
                double x = options.Margin + (column * (cellWidth + options.Gutter));
                double y = sheet.Height - options.Margin - cellHeight - (row * (cellHeight + options.Gutter));
                drawing.Fit(placer.Form(first + k), new PdfRectangle(x, y, x + cellWidth, y + cellHeight), options.Border);
            }

            drawing.Finish();
        }

        return target;
    }

    /// <summary>Puts every page on one sheet, in a grid of cells as large as the largest page, each page at its own size.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="GridOptions.Columns"/> is negative.</exception>
    public static PdfDocument Grid(PdfDocument source, GridOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new GridOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Columns, nameof(options));
        PdfDocument target = PdfDocument.Create();
        int count = source.Pages.Count;
        if (count == 0)
        {
            return target;
        }

        Placer placer = new(source, target);
        int columns = options.Columns > 0 ? options.Columns : (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (count + columns - 1) / columns;
        double cellWidth = 0, cellHeight = 0;
        for (int i = 0; i < count; i++)
        {
            (double w, double h) = placer.Size(i);
            cellWidth = Math.Max(cellWidth, w);
            cellHeight = Math.Max(cellHeight, h);
        }

        double width = (2 * options.Margin) + (columns * cellWidth) + ((columns - 1) * options.Gutter);
        double height = (2 * options.Margin) + (rows * cellHeight) + ((rows - 1) * options.Gutter);
        PdfPage page = target.Pages.AddBlank(new PdfRectangle(0, 0, width, height));
        Sheet drawing = new(page);
        for (int i = 0; i < count; i++)
        {
            int column = i % columns, row = i / columns;
            double x = options.Margin + (column * (cellWidth + options.Gutter));
            double y = height - options.Margin - cellHeight - (row * (cellHeight + options.Gutter));
            drawing.Fit(placer.Form(i), new PdfRectangle(x, y, x + cellWidth, y + cellHeight), options.Border);
        }

        drawing.Finish();
        return target;
    }

    /// <summary>
    /// Lays the pages out as a saddle-stitched booklet: padded with blank pages to a multiple of four, two pages on each side
    /// of each sheet, in the order that reads right once the sheets are printed on both sides, stacked, folded and stapled.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="BookletOptions.SheetsPerSignature"/> is negative.</exception>
    public static PdfDocument Booklet(PdfDocument source, BookletOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new BookletOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.SheetsPerSignature, nameof(options));
        PdfPageSize sheet = Checked(options.Sheet);
        sheet = sheet.Width < sheet.Height ? sheet.Turned : sheet;
        PdfDocument target = PdfDocument.Create();
        Placer placer = new(source, target);
        int count = source.Pages.Count;
        int pagesPerSignature = options.SheetsPerSignature > 0 ? 4 * options.SheetsPerSignature : RoundUp(count, 4);
        double half = sheet.Width / 2;
        for (int start = 0; start < count; start += pagesPerSignature)
        {
            int pages = Math.Min(pagesPerSignature, RoundUp(count - start, 4));
            foreach ((int left, int right) in BookletSides(pages, options.Binding))
            {
                PdfPage page = target.Pages.AddBlank(sheet.ToRectangle());
                Sheet drawing = new(page);
                foreach ((int index, double x0) in new[] { (left, 0.0), (right, half) })
                {
                    if (start + index < count)
                    {
                        // The outer margin only: pages meet at the fold.
                        PdfRectangle cell = x0 == 0
                            ? new PdfRectangle(options.Margin, options.Margin, half, sheet.Height - options.Margin)
                            : new PdfRectangle(half, options.Margin, sheet.Width - options.Margin, sheet.Height - options.Margin);
                        drawing.Fit(placer.Form(start + index), cell, border: false, align: x0 == 0 ? Align.Right : Align.Left);
                    }
                }

                drawing.Finish();
            }
        }

        return target;
    }

    /// <summary>
    /// The page indexes (0-based within a signature of <paramref name="pages"/>, a multiple of four) on each sheet side in
    /// printing order: front, then back, of each sheet from the outside in.
    /// </summary>
    internal static IEnumerable<(int Left, int Right)> BookletSides(int pages, BookletBinding binding)
    {
        for (int sheet = 0; sheet < pages / 4; sheet++)
        {
            (int, int) front = (pages - 1 - (2 * sheet), 2 * sheet);
            (int, int) back = ((2 * sheet) + 1, pages - 2 - (2 * sheet));
            yield return binding == BookletBinding.Left ? front : (front.Item2, front.Item1);
            yield return binding == BookletBinding.Left ? back : (back.Item2, back.Item1);
        }
    }

    /// <summary>
    /// Cuts each page, enlarged by <see cref="PosterOptions.Scale"/>, into tiles of <see cref="PosterOptions.Tile"/>, one sheet
    /// each, row by row from the top left; neighbouring tiles overlap by <see cref="PosterOptions.Overlap"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The scale is not positive, or the overlap leaves no part of a tile its own.</exception>
    public static PdfDocument Poster(PdfDocument source, PosterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new PosterOptions();
        PdfPageSize tile = Checked(options.Tile);
        if (!(options.Scale > 0 && double.IsFinite(options.Scale)))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Scale, "Scale must be above 0.");
        }

        if (!(options.Overlap >= 0) || options.Overlap >= Math.Min(tile.Width, tile.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Overlap, "Overlap must be 0 or more and smaller than the tile.");
        }

        PdfDocument target = PdfDocument.Create();
        Placer placer = new(source, target);
        double stepX = tile.Width - options.Overlap, stepY = tile.Height - options.Overlap;
        for (int i = 0; i < source.Pages.Count; i++)
        {
            (double w, double h) = placer.Size(i);
            double width = w * options.Scale, height = h * options.Scale;
            int columns = Math.Max(1, (int)Math.Ceiling((width - options.Overlap) / stepX - 1e-9));
            int rows = Math.Max(1, (int)Math.Ceiling((height - options.Overlap) / stepY - 1e-9));
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    PdfPage page = target.Pages.AddBlank(tile.ToRectangle());
                    Sheet drawing = new(page);

                    // The page's top left at the tile's top left, moved by the tiles before this one.
                    double x = -column * stepX, top = tile.Height + (row * stepY);
                    drawing.Place(placer.Form(i), x, top - height, options.Scale, clip: new PdfRectangle(Math.Max(0, x), Math.Max(0, top - height), Math.Min(tile.Width, x + width), Math.Min(tile.Height, top)));
                    if (options.CutMarks)
                    {
                        drawing.CutMarks(tile, options.Overlap, column < columns - 1, row < rows - 1);
                    }

                    if (options.Labels)
                    {
                        drawing.Label(string.Create(CultureInfo.InvariantCulture, $"{i + 1} {(char)('A' + (row % 26))}{column + 1}"), 6, 6);
                    }

                    drawing.Finish();
                }
            }
        }

        return target;
    }

    /// <summary>Scales each page to fit <paramref name="size"/>, centred, its proportions kept; a page already that size stays as it is.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The size is not positive.</exception>
    public static PdfDocument FitToSize(PdfDocument source, PdfPageSize size)
    {
        ArgumentNullException.ThrowIfNull(source);
        size = Checked(size);
        PdfDocument target = PdfDocument.Create();
        Placer placer = new(source, target);
        for (int i = 0; i < source.Pages.Count; i++)
        {
            PdfPage page = target.Pages.AddBlank(size.ToRectangle());
            Sheet drawing = new(page);
            drawing.Fit(placer.Form(i), size.ToRectangle(), border: false);
            drawing.Finish();
        }

        return target;
    }

    private static (int Column, int Row) Slot(int k, int columns, int rows, NUpOrder order) => order switch
    {
        NUpOrder.RightToLeftThenDown => (columns - 1 - (k % columns), k / columns),
        NUpOrder.DownThenLeftToRight => (k / rows, k % rows),
        NUpOrder.DownThenRightToLeft => (columns - 1 - (k / rows), k % rows),
        _ => (k % columns, k / columns),
    };

    private static double Cell(double length, int count, double margin, double gutter)
    {
        double cell = (length - (2 * margin) - ((count - 1) * gutter)) / count;
        return cell > 0 ? cell : throw new ArgumentOutOfRangeException(nameof(margin), margin, "The margins and gutters leave no room for the pages.");
    }

    private static PdfPageSize Checked(PdfPageSize size)
    {
        return size.Width > 0 && size.Height > 0 && double.IsFinite(size.Width) && double.IsFinite(size.Height)
            ? size
            : throw new ArgumentOutOfRangeException(nameof(size), size, "A sheet must have a positive width and height.");
    }

    private static void Positive(int value, string name)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be at least 1.");
        }
    }

    private static int RoundUp(int value, int multiple) => Math.Max(multiple, ((value + multiple - 1) / multiple) * multiple);

    private enum Align
    {
        Center,
        Left,
        Right,
    }

    /// <summary>The source's pages as forms in the target, each made the first time it is placed.</summary>
    private sealed class Placer(PdfDocument source, PdfDocument target)
    {
        private readonly Dictionary<int, PdfFormXObject> forms = [];

        public PdfFormXObject Form(int index)
        {
            if (!forms.TryGetValue(index, out PdfFormXObject? form))
            {
                forms[index] = form = source.Pages[index].ToFormXObject(target);
            }

            return form;
        }

        /// <summary>A page's shown size, without making its form.</summary>
        public (double Width, double Height) Size(int index)
        {
            PdfPage page = source.Pages[index];
            (_, double w, double h) = PdfPage.Displayed(page.CropBox.Normalize(), page.Rotate, page.UserUnit);
            return (w, h);
        }
    }

    /// <summary>One sheet's drawing, in PDF's own coordinates, written to the page when finished.</summary>
    private sealed class Sheet(PdfPage page)
    {
        private readonly PdfContentWriter content = new();
        private PdfName? font;

        public void Fit(PdfFormXObject form, PdfRectangle cell, bool border, Align align = Align.Center)
        {
            if (form.Width <= 0 || form.Height <= 0)
            {
                return;
            }

            double scale = Math.Min(cell.Width / form.Width, cell.Height / form.Height);
            double w = form.Width * scale, h = form.Height * scale;
            double x = align switch
            {
                Align.Left => cell.Left,
                Align.Right => cell.Right - w,
                _ => cell.Left + ((cell.Width - w) / 2),
            };
            double y = cell.Bottom + ((cell.Height - h) / 2);
            Place(form, x, y, scale, clip: null);
            if (border)
            {
                content.Save().LineWidth(BorderWidth).Rectangle(x, y, w, h).Stroke().Restore();
            }
        }

        public void Place(PdfFormXObject form, double x, double y, double scale, PdfRectangle? clip)
        {
            PdfName name = page.AddResource("XObject", form.Reference);
            content.Save();
            if (clip is PdfRectangle c)
            {
                content.Rectangle(c.Left, c.Bottom, c.Width, c.Height).Clip().EndPath();
            }

            content.Transform(scale, 0, 0, scale, x, y).Draw(name).Restore();
        }

        /// <summary>Short lines where the tile's own part ends, beside the overlap it shares with the next tile.</summary>
        public void CutMarks(PdfPageSize tile, double overlap, bool right, bool below)
        {
            const double Length = 12;
            content.Save().LineWidth(BorderWidth);
            if (right)
            {
                double x = tile.Width - overlap;
                content.MoveTo(x, 0).LineTo(x, Length).MoveTo(x, tile.Height - Length).LineTo(x, tile.Height);
            }

            if (below)
            {
                double y = overlap;
                content.MoveTo(0, y).LineTo(Length, y).MoveTo(tile.Width - Length, y).LineTo(tile.Width, y);
            }

            content.Stroke().Restore();
        }

        public void Label(string text, double x, double y)
        {
            font ??= page.AddResource("Font", page.Document.Objects.Add(new PdfDictionary
            {
                ["Type"] = PdfName.Get("Font"),
                ["Subtype"] = PdfName.Get("Type1"),
                ["BaseFont"] = PdfName.Get("Helvetica"),
                ["Encoding"] = PdfName.Get("WinAnsiEncoding"),
            }));
            content.Save().BeginText().Font(font, 8).MoveText(x, y).ShowText(WinAnsiEncoding.TryEncode(text) ?? []).EndText().Restore();
        }

        public void Finish()
        {
            page.AddContent(content.ToArray());
        }
    }
}
