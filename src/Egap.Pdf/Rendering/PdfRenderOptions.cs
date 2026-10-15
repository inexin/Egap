using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf;

/// <summary>
/// How a page is rendered (Phase 4 design Q3 and Q4): the resolution or a size to fit, which box shows, extra rotation, a
/// region of the output for tiles, the intent, the background, annotations and anti-aliasing. The defaults draw the crop
/// box at 72 dpi (one pixel per point) on white with annotations, as a viewer shows the page.
/// </summary>
public sealed record PdfRenderOptions
{
    /// <summary>The default options: 72 dpi, the crop box, white, annotations drawn, everything anti-aliased.</summary>
    public static PdfRenderOptions Default { get; } = new();

    /// <summary>The resolution in pixels per inch, so 72 is one pixel per point; ignored when <see cref="FitWidth"/> or <see cref="FitHeight"/> is set.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a positive finite number.</exception>
    public double Dpi
    {
        get;
        init => field = value > 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The resolution must be a positive number.");
    } = 72;

    /// <summary>
    /// The width in pixels the page is scaled to fit, keeping its aspect; with <see cref="FitHeight"/> too, the page fits
    /// inside both. <see langword="null"/> to use <see cref="Dpi"/>.
    /// </summary>
    public int? FitWidth
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The width must be positive.");
    }

    /// <summary>The height in pixels the page is scaled to fit, keeping its aspect; <see langword="null"/> to use <see cref="Dpi"/>.</summary>
    public int? FitHeight
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The height must be positive.");
    }

    /// <summary>Which of the page's boxes is drawn; the crop box, what viewers show, by default.</summary>
    public PdfPageBox Box { get; init; } = PdfPageBox.CropBox;

    /// <summary>Degrees to turn the page clockwise on top of its own <see cref="PdfPage.Rotate"/>: a multiple of 90.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a multiple of 90.</exception>
    public int Rotation
    {
        get;
        init => field = value % 90 == 0 ? ((value % 360) + 360) % 360 : throw new ArgumentOutOfRangeException(nameof(value), value, "Pages turn by multiples of 90 degrees.");
    }

    /// <summary>
    /// Only this part of the output, in pixels of the whole render with (0, 0) at its top left, for rendering a big page as
    /// tiles; <see langword="null"/> for the whole page. The result is the region's size, rounded out to whole pixels.
    /// </summary>
    public RectF? Region { get; init; }

    /// <summary>Whether the page is drawn as shown on screen or as printed; decides which annotations and layers show.</summary>
    public PdfRenderIntent Intent { get; init; } = PdfRenderIntent.Display;

    /// <summary>The colour under the page; white by default, as viewers show it. Transparent leaves the page's own alpha.</summary>
    public RgbaF32 Background { get; init; } = new(1, 1, 1, 1);

    /// <summary>Which annotations are drawn from their appearances.</summary>
    public PdfAnnotationRendering Annotations { get; init; } = PdfAnnotationRendering.All;

    /// <summary>What is anti-aliased (design Q11); everything by default.</summary>
    public PdfAntialiasing Antialiasing { get; init; } = PdfAntialiasing.All;

    /// <summary>Options that fit the page inside <paramref name="width"/> by <paramref name="height"/> pixels, keeping its aspect, as for a thumbnail.</summary>
    public static PdfRenderOptions FitTo(int width, int height)
    {
        return new PdfRenderOptions { FitWidth = width, FitHeight = height };
    }
}

/// <summary>Which of a page's boxes (14.11.2) a render shows.</summary>
public enum PdfPageBox
{
    /// <summary>The crop box: what a viewer shows.</summary>
    CropBox,

    /// <summary>The media box: the whole medium.</summary>
    MediaBox,

    /// <summary>The bleed box.</summary>
    BleedBox,

    /// <summary>The trim box: the finished page.</summary>
    TrimBox,

    /// <summary>The art box.</summary>
    ArtBox,
}

/// <summary>What a render is for, which decides the annotations (by their flags) and layers (by their usage) that show.</summary>
public enum PdfRenderIntent
{
    /// <summary>On screen, as a viewer shows the page.</summary>
    Display,

    /// <summary>On paper, as the page prints.</summary>
    Print,
}

/// <summary>Which annotations a render draws (design Q12).</summary>
public enum PdfAnnotationRendering
{
    /// <summary>Every annotation that shows for the intent, form fields included.</summary>
    All,

    /// <summary>Annotations that show for the intent, but not form fields (widgets).</summary>
    NoForms,

    /// <summary>No annotations: the page's content only.</summary>
    None,
}

/// <summary>What a render anti-aliases (design Q11).</summary>
[Flags]
public enum PdfAntialiasing
{
    /// <summary>Nothing: hard edges everywhere.</summary>
    None = 0,

    /// <summary>The edges of filled and stroked paths and clips.</summary>
    Paths = 1,

    /// <summary>Glyphs.</summary>
    Text = 2,

    /// <summary>Images, smoothed when scaled up.</summary>
    Images = 4,

    /// <summary>Paths, text and images.</summary>
    All = Paths | Text | Images,
}
