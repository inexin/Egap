namespace Egap.Pdf;

/// <summary>Which coordinates a <see cref="PdfCanvas"/> draws in (Phase 3 design Q13).</summary>
public enum PdfCanvasCoordinates
{
    /// <summary>
    /// As the page is shown: points, y down, from the top left of the crop box, with the page's <c>/Rotate</c> applied, so
    /// the same drawing code gives the same picture on a raster and on any page. The canvas is the displayed page's size.
    /// </summary>
    Displayed,

    /// <summary>PDF's own default user space: points, y up, unrotated, with the origin where the page puts it.</summary>
    Pdf,
}

/// <summary>How a <see cref="PdfCanvas"/> draws onto a page.</summary>
public sealed record PdfCanvasOptions
{
    /// <summary>The default options: displayed coordinates, drawing over the page's content, fallbacks at 300 dpi.</summary>
    public static PdfCanvasOptions Default { get; } = new();

    /// <summary>Whether the drawing goes over or under the page's existing content. Defaults to <see cref="PdfCanvasPlacement.Over"/>.</summary>
    public PdfCanvasPlacement Placement { get; init; } = PdfCanvasPlacement.Over;

    /// <summary>Which coordinates the canvas draws in. Defaults to <see cref="PdfCanvasCoordinates.Displayed"/>.</summary>
    public PdfCanvasCoordinates Coordinates { get; init; } = PdfCanvasCoordinates.Displayed;

    /// <summary>
    /// The resolution, in dots per inch, at which what PDF cannot express (a blur, noise, a Porter-Duff blend) is drawn into
    /// an image instead (Phase 3 design Q14). Defaults to 300.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not from 1 to 2,400.</exception>
    public float FallbackDpi
    {
        get;
        init => field = value is >= 1 and <= 2400 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The fallback resolution must be from 1 to 2,400 dpi.");
    } = 300;

    /// <summary>
    /// Whether anything PDF cannot express throws <see cref="NotSupportedException"/> instead of being drawn as an image with a
    /// warning. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Strict { get; init; }
}
