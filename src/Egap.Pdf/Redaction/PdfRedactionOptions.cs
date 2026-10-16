namespace Egap.Pdf;

/// <summary>What redaction does to an image the area covers (Phase 5 design Q9).</summary>
public enum PdfRedactImages
{
    /// <summary>The covered pixels are set to the overlay colour (black without one) and the image is encoded again: the default.</summary>
    Pixels,

    /// <summary>The whole image goes when the area touches it.</summary>
    Remove,

    /// <summary>Images are left as they are.</summary>
    None,
}

/// <summary>What redaction does to vector line art the area covers (Phase 5 design Q9).</summary>
public enum PdfRedactPaths
{
    /// <summary>A path is removed when the area covers it entirely: the default.</summary>
    Covered,

    /// <summary>A path is removed when the area touches it at all.</summary>
    Touched,

    /// <summary>Paths are left as they are.</summary>
    None,
}

/// <summary>How redaction annotations are applied (Phase 5 design Q9).</summary>
public sealed record PdfRedactionOptions
{
    /// <summary>The default options: pixels redacted, covered paths removed.</summary>
    public static PdfRedactionOptions Default { get; } = new();

    /// <summary>What happens to covered images. The default is <see cref="PdfRedactImages.Pixels"/>.</summary>
    public PdfRedactImages Images { get; init; } = PdfRedactImages.Pixels;

    /// <summary>What happens to covered line art. The default is <see cref="PdfRedactPaths.Covered"/>.</summary>
    public PdfRedactPaths Paths { get; init; } = PdfRedactPaths.Covered;

    /// <summary>
    /// When set, each redacted page is also rendered at this resolution and its content replaced by that one image, which
    /// loses all its text: for the most cautious users (design Q9). Left out, pages keep their text and vectors.
    /// </summary>
    public double? RasterizeDpi { get; init; }
}

/// <summary>What applying redactions removed (Phase 5 design Q9).</summary>
/// <param name="Annotations">The redaction annotations applied and deleted.</param>
/// <param name="Glyphs">The glyphs removed from text.</param>
/// <param name="Paths">The paths removed.</param>
/// <param name="Images">The images changed or removed.</param>
/// <param name="Forms">The form XObjects redacted (copied first when other content used them too).</param>
/// <param name="RemovedAnnotations">The other annotations removed because an area covered them.</param>
public sealed record PdfRedactionResult(int Annotations, int Glyphs, int Paths, int Images, int Forms, int RemovedAnnotations);
