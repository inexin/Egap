using Egami.Drawing;

namespace Egap.Pdf.Layout;

/// <summary>
/// What layout analysis found on one page (design Q10): text blocks with their roles, tables and figures, each with its
/// bounds and place in the page's reading order. Bounds are in points on the displayed page: y down from the top left of the
/// crop box, the page's rotation applied, as <see cref="PdfCanvas"/> draws by default.
/// </summary>
public sealed class PdfPageLayout
{
    internal PdfPageLayout(int pageIndex, float width, float height, IReadOnlyList<LayoutElement> elements, bool fromTags, IReadOnlyList<PdfWarning> warnings)
    {
        PageIndex = pageIndex;
        Width = width;
        Height = height;
        Elements = elements;
        Blocks = [.. elements.OfType<LayoutBlock>()];
        Tables = [.. elements.OfType<DetectedTable>()];
        Figures = [.. elements.OfType<LayoutFigure>()];
        FromTags = fromTags;
        Warnings = warnings;
    }

    /// <summary>The page's index in its document, from 0.</summary>
    public int PageIndex { get; }

    /// <summary>The displayed page's width in points.</summary>
    public float Width { get; }

    /// <summary>The displayed page's height in points.</summary>
    public float Height { get; }

    /// <summary>Every block, table and figure in reading order: page headers first, then the body, then footnotes, footers and page numbers.</summary>
    public IReadOnlyList<LayoutElement> Elements { get; }

    /// <summary>The text blocks in reading order.</summary>
    public IReadOnlyList<LayoutBlock> Blocks { get; }

    /// <summary>The tables in reading order.</summary>
    public IReadOnlyList<DetectedTable> Tables { get; }

    /// <summary>The figures in reading order.</summary>
    public IReadOnlyList<LayoutFigure> Figures { get; }

    /// <summary>Whether the page's structure tree gave its roles and order (design Q2) rather than its geometry.</summary>
    public bool FromTags { get; }

    /// <summary>What went wrong reading the page, if anything (unmapped glyphs, broken content).</summary>
    public IReadOnlyList<PdfWarning> Warnings { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Page {PageIndex + 1}: {Blocks.Count} blocks, {Tables.Count} tables, {Figures.Count} figures";
    }
}

/// <summary>A block, table or figure on a page: where it is, where it comes in reading order and how sure the analysis is.</summary>
public abstract class LayoutElement
{
    private protected LayoutElement(RectF bounds, int order, double confidence)
    {
        Bounds = bounds;
        Order = order;
        Confidence = confidence;
    }

    /// <summary>Where it is on the displayed page, in points.</summary>
    public RectF Bounds { get; }

    /// <summary>Its place in the page's reading order, from 0.</summary>
    public int Order { get; }

    /// <summary>How sure the analysis is of what it found, from 0 to 1; tags and a region detector's sure answers give 1.</summary>
    public double Confidence { get; }
}

/// <summary>A text block: a paragraph, heading, list item, caption, note, header or footer, with its lines.</summary>
public sealed class LayoutBlock : LayoutElement
{
    internal LayoutBlock(RectF bounds, int order, double confidence, LayoutRole role, string text, int level, string? listMarker, bool continues,
        IReadOnlyList<LayoutLine> lines)
        : base(bounds, order, confidence)
    {
        Role = role;
        Text = text;
        Level = level;
        ListMarker = listMarker;
        ContinuesPrevious = continues;
        Lines = lines;
    }

    /// <summary>What the block is.</summary>
    public LayoutRole Role { get; }

    /// <summary>The block's text: its lines joined, words broken at a line end with a hyphen joined again.</summary>
    public string Text { get; }

    /// <summary>A heading's level (1 for the top) or a list item's nesting (1 for the outermost list); 0 otherwise.</summary>
    public int Level { get; }

    /// <summary>A list item's marker as written (a bullet, "1.", "a)"), or a footnote's marker; null otherwise.</summary>
    public string? ListMarker { get; }

    /// <summary>Whether the block carries on the paragraph before it in reading order, across a column or page break.</summary>
    public bool ContinuesPrevious { get; }

    /// <summary>The block's lines, top to bottom.</summary>
    public IReadOnlyList<LayoutLine> Lines { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"{Role} {Order}: {Text}";
    }
}

/// <summary>A line of text with its words.</summary>
public sealed class LayoutLine
{
    internal LayoutLine(string text, RectF bounds, float baseline, IReadOnlyList<LayoutWord> words)
    {
        Text = text;
        Bounds = bounds;
        Baseline = baseline;
        Words = words;
    }

    /// <summary>The line's text: its words joined by single spaces.</summary>
    public string Text { get; }

    /// <summary>Where the line is on the displayed page, in points.</summary>
    public RectF Bounds { get; }

    /// <summary>The y of the baseline most of the line's glyphs sit on.</summary>
    public float Baseline { get; }

    /// <summary>The words, in reading order.</summary>
    public IReadOnlyList<LayoutWord> Words { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Text;
    }
}

/// <summary>A word with the font it is mostly set in.</summary>
public sealed class LayoutWord
{
    internal LayoutWord(string text, RectF bounds, string fontFamily, float fontSize, bool bold, bool italic, PdfColor color)
    {
        Text = text;
        Bounds = bounds;
        FontFamily = fontFamily;
        FontSize = fontSize;
        IsBold = bold;
        IsItalic = italic;
        Color = color;
    }

    /// <summary>The word's text.</summary>
    public string Text { get; }

    /// <summary>Where the word is on the displayed page, in points.</summary>
    public RectF Bounds { get; }

    /// <summary>The font's family, its subset prefix and style suffix removed ("Arial" for "ABCDEF+Arial-BoldMT").</summary>
    public string FontFamily { get; }

    /// <summary>The size in points.</summary>
    public float FontSize { get; }

    /// <summary>Whether the word is bold: its font says so, or it is filled and stroked as synthetic bold is.</summary>
    public bool IsBold { get; }

    /// <summary>Whether the word is italic or oblique.</summary>
    public bool IsItalic { get; }

    /// <summary>The fill colour as the page sets it.</summary>
    public PdfColor Color { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Text;
    }
}

/// <summary>A table: its cells on a grid of <see cref="RowCount"/> by <see cref="ColumnCount"/>.</summary>
public sealed class DetectedTable : LayoutElement
{
    internal DetectedTable(RectF bounds, int order, double confidence, int rows, int columns, bool ruled, bool continues, bool repeatsHeader, IReadOnlyList<TableCell> cells)
        : base(bounds, order, confidence)
    {
        RowCount = rows;
        ColumnCount = columns;
        IsRuled = ruled;
        ContinuesPrevious = continues;
        RepeatsHeader = repeatsHeader;
        Cells = cells;
    }

    /// <summary>The number of rows.</summary>
    public int RowCount { get; }

    /// <summary>The number of columns.</summary>
    public int ColumnCount { get; }

    /// <summary>Whether ruling lines draw the grid (rather than text alignment or tags).</summary>
    public bool IsRuled { get; }

    /// <summary>Whether the table carries on a table from the page before.</summary>
    public bool ContinuesPrevious { get; }

    /// <summary>Whether, carrying on, its first row repeats the earlier part's header.</summary>
    public bool RepeatsHeader { get; }

    /// <summary>The cells, row by row, each at its top-left grid position; a spanned position has no cell of its own.</summary>
    public IReadOnlyList<TableCell> Cells { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Table {RowCount} x {ColumnCount}{(IsRuled ? ", ruled" : "")}";
    }
}

/// <summary>A table cell.</summary>
public sealed class TableCell
{
    internal TableCell(int row, int column, int rowSpan, int columnSpan, bool header, string text, RectF bounds, IReadOnlyList<LayoutLine> lines)
    {
        Row = row;
        Column = column;
        RowSpan = rowSpan;
        ColumnSpan = columnSpan;
        IsHeader = header;
        Text = text;
        Bounds = bounds;
        Lines = lines;
    }

    /// <summary>The cell's row, from 0.</summary>
    public int Row { get; }

    /// <summary>The cell's column, from 0.</summary>
    public int Column { get; }

    /// <summary>How many rows the cell spans.</summary>
    public int RowSpan { get; }

    /// <summary>How many columns the cell spans.</summary>
    public int ColumnSpan { get; }

    /// <summary>Whether the cell is a header cell.</summary>
    public bool IsHeader { get; }

    /// <summary>The cell's text: its lines joined.</summary>
    public string Text { get; }

    /// <summary>The cell's area on the displayed page, in points.</summary>
    public RectF Bounds { get; }

    /// <summary>The cell's lines, top to bottom.</summary>
    public IReadOnlyList<LayoutLine> Lines { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return Text;
    }
}

/// <summary>A figure: images and drawings with any text inside them.</summary>
public sealed class LayoutFigure : LayoutElement
{
    private readonly PdfPage? page;

    internal LayoutFigure(RectF bounds, int order, double confidence, PdfPage? page, IReadOnlyList<LayoutImage> images, IReadOnlyList<LayoutLine> labels,
        string? altText, bool decorative, bool inline, bool wrapped)
        : base(bounds, order, confidence)
    {
        this.page = page;
        Images = images;
        Labels = labels;
        AltText = altText;
        IsDecorative = decorative;
        IsInline = inline;
        IsWrapped = wrapped;
    }

    /// <summary>The images in the figure, with their original bytes kept.</summary>
    public IReadOnlyList<LayoutImage> Images { get; }

    /// <summary>For a figure read from JSON, the image the JSON carried as a data URI, with its size in pixels.</summary>
    internal (Egap.Documents.ImageData Image, int Width, int Height)? Embedded { get; init; }

    /// <summary>The text inside or just beside the figure: axis labels, callouts, a legend.</summary>
    public IReadOnlyList<LayoutLine> Labels { get; }

    /// <summary>The figure's alternate text from the structure tree, or null.</summary>
    public string? AltText { get; }

    /// <summary>Whether the figure is marked as an artifact: decoration that needs no alternate text.</summary>
    public bool IsDecorative { get; }

    /// <summary>Whether the figure sits in a line of text, as an icon or a symbol does.</summary>
    public bool IsInline { get; }

    /// <summary>Whether text runs beside the figure, so it floats with text wrapped around it.</summary>
    public bool IsWrapped { get; }

    /// <summary>
    /// The figure's region of the page as an Egami Picture, drawn by Egap's renderer: drawings stay vectors, images stay
    /// images. A writer makes a PNG or an SVG of it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The figure was read from JSON (<see cref="DoclingJson.Read(string)"/>) and has no page to draw.</exception>
    public Picture ToPicture()
    {
        if (page is null)
        {
            throw new InvalidOperationException("This figure was read from JSON and has no PDF page to draw.");
        }

        return page.ToPicture(new PdfRenderOptions { Region = Bounds, Annotations = PdfAnnotationRendering.None });
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        return $"Figure {Order}: {Images.Count} images{(AltText is null ? "" : ", " + AltText)}";
    }

    /// <summary>
    /// The figure as an image file and its size in pixels: a lone upright image's stored JPEG as it is, or the page drawn over
    /// the figure at <paramref name="dpi"/> as a PNG. Null for a figure read from JSON, which has no page.
    /// </summary>
    internal (Egap.Documents.ImageData Image, int Width, int Height)? Image(double dpi)
    {
        if (Embedded is not null)
        {
            return Embedded;
        }

        if (page is null || Bounds.Width < 1 || Bounds.Height < 1 || !float.IsFinite(Bounds.Width) || !float.IsFinite(Bounds.Height))
        {
            return null;
        }

        if (Images is [LayoutImage { Image: PdfImage stored } only] && Labels.Count == 0 && only.Bounds.Width >= Bounds.Width - 1 && only.Bounds.Height >= Bounds.Height - 1
            && only.Corners is [var a, var b, _, var d] && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.X - d.X) < 0.5 && a.X < b.X && a.Y < d.Y
            && ModelBuilder.Stored(stored) is Egap.Documents.ImageData kept)
        {
            return (kept, stored.Width, stored.Height);
        }

        double scale = dpi / 72;
        using Egami.Raster<Egami.Pixels.Rgba8> raster = page.Render(new PdfRenderOptions
        {
            Dpi = dpi,
            Region = new RectF((float)(Bounds.X * scale), (float)(Bounds.Y * scale), (float)(Bounds.Width * scale), (float)(Bounds.Height * scale)),
            Annotations = PdfAnnotationRendering.None,
        });
        return (ModelBuilder.Png(raster), raster.Width, raster.Height);
    }
}

/// <summary>An image where the page draws it.</summary>
public sealed class LayoutImage
{
    internal LayoutImage(PdfImage? image, RectF bounds, IReadOnlyList<System.Numerics.Vector2> corners)
    {
        Image = image;
        Bounds = bounds;
        Corners = corners;
    }

    /// <summary>The image as stored: its original bytes, filters, colour space and soft mask. Null when it cannot be read.</summary>
    public PdfImage? Image { get; }

    /// <summary>The box around the drawn image on the displayed page.</summary>
    public RectF Bounds { get; }

    /// <summary>Where the image's corners land (top left, top right, bottom right, bottom left of the image as stored), for a turned or slanted image.</summary>
    public IReadOnlyList<System.Numerics.Vector2> Corners { get; }
}
