namespace Egap.Pdf;

/// <summary>
/// What <see cref="PdfDocument.Optimize"/> does (Phase 7 design Q2 to Q7). A preset fills in the switches; each can be changed
/// with <c>with</c>. <see cref="Lossless"/>, the default, never changes a pixel: it merges duplicates, subsets fonts, removes
/// what nobody sees, codes bilevel images as CCITT G4, optimizes JPEG Huffman tables and writes object streams.
/// <see cref="Print"/>, <see cref="Ebook"/> and <see cref="Screen"/> also downsample images above 1.5 times their resolution
/// and write photos as JPEG at their quality.
/// </summary>
/// <example>
/// <code>
/// PdfOptimizeReport report = pdf.Optimize(PdfOptimizeOptions.Ebook with { JpegQuality = 70 });
/// pdf.Save("small.pdf");
/// </code>
/// </example>
public sealed record PdfOptimizeOptions
{
    /// <summary>Nothing lossy: the default.</summary>
    public static PdfOptimizeOptions Lossless { get; } = new();

    /// <summary>For print: images to 300 dpi, photos as JPEG at quality 85.</summary>
    public static PdfOptimizeOptions Print { get; } = new() { LossyImages = true, ImageResolution = 300, JpegQuality = 85 };

    /// <summary>For reading on screens and tablets: images to 150 dpi, photos as JPEG at quality 75.</summary>
    public static PdfOptimizeOptions Ebook { get; } = new() { LossyImages = true, ImageResolution = 150, JpegQuality = 75 };

    /// <summary>
    /// The smallest: images to 96 dpi, photos as JPEG at quality 60. (96 rather than 72 dpi, at which the text of scanned
    /// pages becomes hard to read.)
    /// </summary>
    public static PdfOptimizeOptions Screen { get; } = new() { LossyImages = true, ImageResolution = 96, JpegQuality = 60 };

    /// <summary>
    /// Gets whether images may lose quality: downsampled, and photos written as JPEG. False keeps every pixel. Defaults to
    /// false.
    /// </summary>
    public bool LossyImages { get; init; }

    /// <summary>
    /// Gets the resolution, in samples per inch at an image's largest placement, that images are downsampled to when lossy;
    /// null keeps their size. Bilevel images are never downsampled.
    /// </summary>
    public double? ImageResolution { get; init; }

    /// <summary>Gets how many times <see cref="ImageResolution"/> an image must exceed before it is downsampled. Defaults to 1.5.</summary>
    public double DownsampleThreshold { get; init; } = 1.5;

    /// <summary>
    /// Gets the JPEG quality, 1 to 100, photos are written at. An existing JPEG is re-encoded only when it is downsampled or
    /// estimated at least 10 points above this. Defaults to 75.
    /// </summary>
    public int JpegQuality { get; init; } = 75;

    /// <summary>
    /// Gets whether images are re-encoded at all: G4 for bilevel images, JPEG Huffman optimisation, and the lossy changes
    /// <see cref="LossyImages"/> allows. Defaults to true.
    /// </summary>
    public bool OptimizeImages { get; init; } = true;

    /// <summary>Gets whether streams that hold the same data are written once. Defaults to true.</summary>
    public bool MergeDuplicates { get; init; } = true;

    /// <summary>
    /// Gets whether fully embedded TrueType and CFF fonts are cut to the glyphs used. Fonts a form may type with are kept
    /// whole. Defaults to true.
    /// </summary>
    public bool SubsetFonts { get; init; } = true;

    /// <summary>Gets whether page thumbnails (<c>/Thumb</c>) are removed. Defaults to true.</summary>
    public bool RemoveThumbnails { get; init; } = true;

    /// <summary>Gets whether private application data (<c>/PieceInfo</c>) is removed. Defaults to true.</summary>
    public bool RemovePrivateData { get; init; } = true;

    /// <summary>Gets whether content streams that draw nothing are removed. Defaults to true.</summary>
    public bool RemoveEmptyContent { get; init; } = true;

    /// <summary>
    /// Gets whether a signed document is optimized, which breaks its signatures: optimizing ends in a full rewrite. False
    /// (the default) leaves a signed document as it is and says so in the report.
    /// </summary>
    public bool AllowInvalidatingSignatures { get; init; }

    /// <summary>Throws when a value is outside what it can mean.</summary>
    internal void Check(string name)
    {
        if (JpegQuality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(name, JpegQuality, "JpegQuality must be 1 to 100.");
        }

        if (ImageResolution is double dpi && !(dpi > 0 && double.IsFinite(dpi)))
        {
            throw new ArgumentOutOfRangeException(name, dpi, "ImageResolution must be above 0.");
        }

        if (!(DownsampleThreshold >= 1 && double.IsFinite(DownsampleThreshold)))
        {
            throw new ArgumentOutOfRangeException(name, DownsampleThreshold, "DownsampleThreshold must be 1 or more.");
        }
    }
}

/// <summary>What <see cref="PdfDocument.Optimize"/> did (Phase 7 design 7.1): bytes before and after, by pass, and every image changed.</summary>
public sealed class PdfOptimizeReport
{
    internal PdfOptimizeReport(long bytesBefore, long bytesAfter, IReadOnlyList<PdfOptimizedImage> images, PdfOptimizeSavings savings, IReadOnlyList<string> skipped)
    {
        BytesBefore = bytesBefore;
        BytesAfter = bytesAfter;
        Images = images;
        Savings = savings;
        Skipped = skipped;
    }

    /// <summary>The size of the file as it was opened; 0 for a document made in memory.</summary>
    public long BytesBefore { get; }

    /// <summary>The size the optimized document saves at, measured by writing it.</summary>
    public long BytesAfter { get; }

    /// <summary>Every image changed, in the order of the pages that first draw them.</summary>
    public IReadOnlyList<PdfOptimizedImage> Images { get; }

    /// <summary>What each pass saved.</summary>
    public PdfOptimizeSavings Savings { get; }

    /// <summary>The passes that were skipped, each with why, such as a signed document or a PDF/A rule.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <inheritdoc/>
    public override string ToString()
    {
        return FormattableString.Invariant($"{BytesBefore} -> {BytesAfter} bytes; {Images.Count} images, {Savings.FontsSubset} fonts subset, {Savings.StreamsMerged} streams merged");
    }
}

/// <summary>One image the optimizer changed.</summary>
/// <param name="Page">The first page that draws it, 1-based.</param>
/// <param name="OldWidth">The width in samples before.</param>
/// <param name="OldHeight">The height in samples before.</param>
/// <param name="NewWidth">The width in samples after.</param>
/// <param name="NewHeight">The height in samples after.</param>
/// <param name="OldBytes">The stored bytes of the image and its soft mask before.</param>
/// <param name="NewBytes">The stored bytes after.</param>
/// <param name="OldResolution">The effective resolution before, in samples per inch; null when its placement is not known, and always under lossless options, which do not look at placements.</param>
/// <param name="NewResolution">The effective resolution after.</param>
/// <param name="Encoding">How it is stored now: <c>JPEG</c>, <c>Flate</c>, <c>CCITT G4</c> or <c>JPEG (Huffman optimized)</c>.</param>
public sealed record PdfOptimizedImage(int Page, int OldWidth, int OldHeight, int NewWidth, int NewHeight, long OldBytes, long NewBytes,
    double? OldResolution, double? NewResolution, string Encoding);

/// <summary>What each optimizer pass saved, in stored bytes before the file is written.</summary>
/// <param name="ImageBytes">Bytes the image pass saved.</param>
/// <param name="StreamsMerged">Streams that duplicated another and are written once.</param>
/// <param name="MergedBytes">Bytes the duplicates held.</param>
/// <param name="FontsSubset">Font programs cut to the glyphs used.</param>
/// <param name="FontBytes">Bytes the cut programs no longer hold.</param>
/// <param name="ThumbnailsRemoved">Page thumbnails removed.</param>
/// <param name="PrivateDataRemoved">Private application data dictionaries removed.</param>
/// <param name="EmptyContentRemoved">Empty content streams removed.</param>
public sealed record PdfOptimizeSavings(long ImageBytes, int StreamsMerged, long MergedBytes, int FontsSubset, long FontBytes,
    int ThumbnailsRemoved, int PrivateDataRemoved, int EmptyContentRemoved);
