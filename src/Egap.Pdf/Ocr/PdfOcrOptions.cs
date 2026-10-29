namespace Egap.Pdf.Ocr;

/// <summary>Which pages <see cref="PdfOcr.MakeSearchableAsync"/> sends to the engine.</summary>
public enum PdfOcrMode
{
    /// <summary>Pages that already have text, visible or an earlier invisible layer, are left alone and reported; the rest get OCR.</summary>
    Skip,

    /// <summary>
    /// An earlier invisible text layer is removed and the page recognized again; visible text is kept, and words the engine
    /// finds on it are not added twice. Pages with visible text only are left alone.
    /// </summary>
    Redo,

    /// <summary>Every page is rendered and recognized, keeping the text it has; words already on the page are not added twice.</summary>
    Force,
}

/// <summary>How <see cref="PdfOcr.MakeSearchableAsync"/> makes a PDF searchable.</summary>
public sealed record PdfOcrOptions
{
    /// <summary>The defaults: <see cref="PdfOcrMode.Skip"/> over every page, 300 dpi renders, all words kept, no PDF/A.</summary>
    public static PdfOcrOptions Default { get; } = new();

    /// <summary>Which pages get OCR. <see cref="PdfOcrMode.Skip"/> by default.</summary>
    public PdfOcrMode Mode { get; init; } = PdfOcrMode.Skip;

    /// <summary>The pages to look at, such as <c>PdfPageRange.Parse("1-3,7")</c>, or <see langword="null"/> (the default) for every page.</summary>
    public PdfPageRange? Pages { get; init; }

    /// <summary>The languages the engine reads, as it names them (such as "eng" and "nor"), most likely first; empty for its default.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>
    /// The resolution, in pixels per inch, a page is rendered at when it is not one scanned image the engine can read as it is.
    /// 300 by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 72 and 1200.</exception>
    public double Dpi
    {
        get;
        init => field = value is >= 72 and <= 1200 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Pages are rendered at 72 to 1200 dpi.");
    } = 300;

    /// <summary>The lowest confidence a word needs to be kept, from 0 (every word, the default) to 1. Words the engine gives no confidence for are kept.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 0 and 1.</exception>
    public double MinConfidence
    {
        get;
        init => field = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Confidence runs from 0 to 1.");
    }

    /// <summary>
    /// Whether the image sent to the engine is straightened first (Egami's skew detection), with the words mapped back onto
    /// the page as it is. The page itself is not changed. Off by default.
    /// </summary>
    public bool Deskew { get; init; }

    /// <summary>
    /// Whether a page the engine finds turned (<see cref="Egami.Ocr.OcrResult.Rotation"/> of 90, 180 or 270) gets its
    /// <c>/Rotate</c> changed so it shows upright. Off by default.
    /// </summary>
    public bool RotatePages { get; init; }

    /// <summary>
    /// How many pages the engine reads at once; 0 (the default) for the processor count. An engine that says it is not
    /// thread-safe always reads one page at a time.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int MaxDegreeOfParallelism
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The degree of parallelism cannot be negative.");
    }

    /// <summary>Told how many pages are done, after each page; null for none.</summary>
    public IProgress<int>? Progress { get; init; }

    /// <summary>
    /// Whether an engine error stops the run (the exception is thrown). By default the page is reported as failed and the
    /// other pages go on.
    /// </summary>
    public bool StopOnError { get; init; }

    /// <summary>
    /// The PDF/A level the document is converted to after the text is added (through <see cref="PdfAConverter"/>), or
    /// <see langword="null"/> (the default) to leave its conformance as it is.
    /// </summary>
    public PdfAConformance? PdfA { get; init; }

    /// <summary>
    /// Whether a signed document may be changed, which breaks its signatures. By default a signed document is refused before
    /// anything changes.
    /// </summary>
    public bool AllowInvalidatingSignatures { get; init; }

    /// <summary>The most pages one run looks at. 10,000 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxPages
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be at least 1.");
    } = 10_000;

    /// <summary>
    /// The most pixels an image sent to the engine has: a scanned image larger than this is rendered instead, and a render
    /// that would be larger is made at a lower resolution. 100 million by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public long MaxImagePixels
    {
        get;
        init => field = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be at least 1.");
    } = 100_000_000;
}
