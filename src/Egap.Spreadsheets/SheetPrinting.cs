using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Pdf;
using Egap.Spreadsheets.Printing;

namespace Egap.Spreadsheets;

/// <summary>How sheets lay out on pages to print, export to PDF or render as images.</summary>
public sealed record SheetPrintOptions
{
    /// <summary>The defaults: the print areas as set, the system's fonts and the standard substitutes.</summary>
    public static SheetPrintOptions Default { get; } = new();

    /// <summary>
    /// Where fonts come from and what stands in for the ones missing, as for documents; null for the system's fonts with the
    /// standard substitutes (Carlito for Calibri, Liberation Sans for Arial and so on).
    /// </summary>
    public FontSettings? Fonts { get; init; }

    /// <summary>Whether to print each sheet's used cells even where it has a print area, as Excel's Ignore print areas does.</summary>
    public bool IgnorePrintAreas { get; init; }

    /// <summary>The date and time headers and footers show for <c>&amp;D</c> and <c>&amp;T</c>; null for now.</summary>
    public DateTime? PrintTime { get; init; }

    /// <summary>
    /// The workbook's file path, for <c>&amp;F</c> (its name) and <c>&amp;Z</c> (its folder) in headers and footers; null
    /// leaves them empty, or uses the path a save to PDF writes beside it.
    /// </summary>
    public string? FilePath { get; init; }
}

/// <summary>How sheets export to PDF.</summary>
public sealed record SheetPdfOptions
{
    /// <summary>The defaults: an untagged PDF.</summary>
    public static SheetPdfOptions Default { get; } = new();

    /// <summary>How the sheets lay out on pages; null for <see cref="SheetPrintOptions.Default"/>.</summary>
    public SheetPrintOptions? Print { get; init; }

    /// <summary>
    /// Whether to write a tagged PDF: each print range a table of rows and cells, pictures as figures with their alternative
    /// text, and gridlines, fills, borders, headings, headers and footers marked as artifacts.
    /// </summary>
    public bool Tagged { get; init; }

    /// <summary>The PDF/A part and level to conform to, or none. The a levels are tagged.</summary>
    public PdfAConformance Conformance { get; init; }

    /// <summary>Whether to conform to PDF/UA-1, which is tagged and needs a language and a title.</summary>
    public bool UniversalAccessibility { get; init; }

    /// <summary>The document's language, such as <c>en-US</c>; null for the workbook culture's.</summary>
    public string? Language { get; init; }

    /// <summary>The document's title; null for the file name, or the first sheet's name.</summary>
    public string? Title { get; init; }

    /// <summary>Whether the PDF is tagged: asked for, or needed by PDF/UA or an a level of PDF/A.</summary>
    public bool IsTagged => Tagged || UniversalAccessibility || Conformance is PdfAConformance.PdfA2a or PdfAConformance.PdfA3a;
}

/// <summary>
/// One or more sheets laid out on pages as Excel prints them (Build Plan task 17): the print area, the scale or fit to pages,
/// the page breaks, the repeated titles and the page order give the pages; each draws on any Egami canvas.
/// </summary>
public sealed class SheetPrintLayout
{
    internal SheetPrintLayout(IReadOnlyList<SheetPage> pages, SheetFonts fonts)
    {
        Pages = pages;
        Fonts = fonts;
    }

    /// <summary>The pages, in print order.</summary>
    public IReadOnlyList<SheetPage> Pages { get; }

    /// <summary>The fonts used in place of the ones the cells ask for, each once.</summary>
    public IReadOnlyList<FontSubstitution> FontSubstitutions => Fonts.Substitutions;

    internal SheetFonts Fonts { get; }

    internal static SheetPrintLayout Create(Workbook book, IEnumerable<Worksheet> sheets, SheetPrintOptions? options, string? filePath = null)
    {
        options ??= SheetPrintOptions.Default;
        SheetFonts fonts = new(book, options.Fonts);
        SheetColors colors = SheetColors.For(book);
        PrintContext context = new(book, fonts, colors, options.PrintTime ?? DateTime.Now, options.FilePath ?? filePath);
        List<SheetPage> pages = [];
        foreach (Worksheet sheet in sheets)
        {
            SheetPaginator paginator = new(sheet, new SheetGrid(sheet, fonts), options.IgnorePrintAreas);
            int first = sheet.PageSetup.FirstPageNumber ?? 1;
            for (int i = 0; i < paginator.Pages.Count; i++)
            {
                pages.Add(new SheetPage(context, paginator, paginator.Pages[i], i, first + i));
            }
        }

        return new SheetPrintLayout(pages, fonts);
    }
}

/// <summary>A printed page of a sheet: its size, the cells on it, and drawing it.</summary>
public sealed class SheetPage
{
    internal SheetPage(PrintContext context, SheetPaginator paginator, PagePlan plan, int index, int number)
    {
        Context = context;
        Paginator = paginator;
        Plan = plan;
        Index = index;
        Number = number;
    }

    /// <summary>The sheet the page prints.</summary>
    public Worksheet Worksheet => Paginator.Sheet;

    /// <summary>The page's place among the sheet's pages, from 0.</summary>
    public int Index { get; }

    /// <summary>The page number headers and footers show: the sheet's first page number plus <see cref="Index"/>.</summary>
    public int Number { get; }

    /// <summary>The number of pages the sheet prints.</summary>
    public int PageCount => Paginator.Pages.Count;

    /// <summary>The page's width in points, as the paper and orientation give it.</summary>
    public double Width => Paginator.PageWidth;

    /// <summary>The page's height in points.</summary>
    public double Height => Paginator.PageHeight;

    /// <summary>The scale the cells print at: 1 for 100 %.</summary>
    public double Scale => Paginator.Scale;

    /// <summary>The cells the page prints, without the repeated titles.</summary>
    public RangeAddress Cells => new(Plan.Rows.First, Plan.Columns.First, Plan.Rows.Last, Plan.Columns.Last);

    internal PrintContext Context { get; }

    internal SheetPaginator Paginator { get; }

    internal PagePlan Plan { get; }

    /// <summary>
    /// Draws the page onto <paramref name="canvas"/> with its top-left corner at the canvas's origin, one canvas unit to the
    /// point: any Egami canvas, such as a raster, an SVG or a PDF page.
    /// </summary>
    public void Draw(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        new SheetPageRenderer(this, canvas, null).Draw();
    }

    /// <summary>The page drawn on white at <paramref name="dpi"/> dots per inch.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dpi"/> is not positive.</exception>
    public Raster<Rgba8> Render(double dpi = 96)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        double scale = dpi / 72;
        Raster<Rgba8> raster = Raster.Create(Math.Max(1, (int)Math.Ceiling(Width * scale)), Math.Max(1, (int)Math.Ceiling(Height * scale)), new Rgba8(255, 255, 255, 255));
        using RasterCanvas<Rgba8> canvas = new(raster);
        canvas.Scale((float)scale, (float)scale);
        Draw(canvas);
        return raster;
    }
}
