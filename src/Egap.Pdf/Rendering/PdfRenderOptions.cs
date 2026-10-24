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

    /// <summary>
    /// Whether fonts that are not embedded may be matched to the machine's own fonts by name (design Q5). The standard 14
    /// and their aliases always use the fonts bundled with Egap; turn this off for output that is the same on every machine.
    /// </summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// Whether a document's output intent decides what its device colours look like (design Q7): a PDF/X file's CMYK through
    /// its press profile. On by default; off draws device colours as they are, CMYK through <see cref="DefaultCmykProfile"/>.
    /// </summary>
    public bool UseOutputIntents { get; init; } = true;

    /// <summary>
    /// The press profile DeviceCMYK colours are drawn through when the document has no CMYK output intent (or
    /// <see cref="UseOutputIntents"/> is off), such as U.S. Web Coated (SWOP) or GRACoL; <see langword="null"/> (the default)
    /// for Coated FOGRA39 (<see cref="Egami.Colors.ColorProfile.Fogra39"/>). The profile must be a CMYK output profile.
    /// </summary>
    /// <exception cref="ArgumentException">The profile is not a four-channel CMYK profile.</exception>
    public Egami.Colors.ColorProfile? DefaultCmykProfile
    {
        get;
        init => field = value is null or { Model: Egami.Colors.ColorModel.Cmyk, ChannelCount: 4 } ? value : throw new ArgumentException("The default CMYK profile must be a CMYK profile.", nameof(value));
    }

    /// <summary>
    /// How many bytes of decoded images a document keeps for its renders (design Q14), the least recently drawn dropped
    /// first; 256 MB by default, 0 to keep none.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public long ImageCacheLimit
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The image cache limit cannot be negative.");
    } = 256L << 20;

    /// <summary>
    /// Layers shown or hidden whatever the document's configuration says (design Q13), applied last; <see langword="null"/>
    /// to keep the configuration's choice for every layer.
    /// </summary>
    public IReadOnlyDictionary<PdfLayer, bool>? Layers { get; init; }

    /// <summary>
    /// The optional content configuration to start from, by its <c>/Name</c> in <c>/OCProperties /Configs</c>; <see langword="null"/>
    /// (the default) for the document's default configuration.
    /// </summary>
    public string? LayerConfiguration { get; init; }

    /// <summary>
    /// Whether the first problem in the page throws a <see cref="PdfException"/> (design Q15). By default a render is
    /// lenient: a bad operator is skipped, a missing resource or broken image draws nothing, a broken font is replaced, and
    /// each is reported to <see cref="OnWarning"/>.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>Called with each problem a lenient render worked around, after the page is drawn; null to ignore them.</summary>
    public Action<PdfWarning>? OnWarning { get; init; }

    /// <summary>
    /// The most pixels <see cref="PdfPage.Render(PdfRenderOptions?, CancellationToken)"/> makes (design Q15): a larger output throws
    /// <see cref="PdfLimitException"/> before anything is allocated. 256 megapixels by default; render big pages as tiles
    /// through <see cref="Region"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxOutputPixels
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be positive.");
    } = 256_000_000;

    /// <summary>
    /// The most content operators one render walks, forms, patterns and glyph procedures included; past it the rest of the
    /// page is left out with a warning (a <see cref="PdfLimitException"/> in strict mode). 10 million by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxOperations
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be positive.");
    } = 10_000_000;

    /// <summary>The most pixels an image may have to be decoded and drawn; larger ones are skipped with a warning. 256 megapixels by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public long MaxImagePixels
    {
        get;
        init => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be positive.");
    } = 256_000_000;

    /// <summary>What is anti-aliased (design Q11); everything by default.</summary>
    public PdfAntialiasing Antialiasing { get; init; } = PdfAntialiasing.All;

    /// <summary>
    /// Whether the page is shown as it prints (Phase 8 design Q11): rendered as ink plates, so overprint and spot colours act
    /// as on press, and proofed back to sRGB through the press profile (the CMYK output intent, else FOGRA39), as a soft
    /// proof such as Acrobat's Output Preview. The background is the paper; the result is opaque. Off by default, when
    /// overprint is ignored as on screen.
    /// </summary>
    public bool SimulateOverprint { get; init; }

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
