namespace Egap.Pdf.Ocr;

/// <summary>What happened to one page in <see cref="PdfOcr.MakeSearchableAsync"/>.</summary>
public enum PdfOcrPageStatus
{
    /// <summary>The engine read the page and its words were added.</summary>
    Recognized,

    /// <summary>The page was left alone, as the mode says; <see cref="PdfOcrPageReport.Message"/> says why.</summary>
    Skipped,

    /// <summary>The engine failed on the page; <see cref="PdfOcrPageReport.Message"/> holds its error. The page is unchanged.</summary>
    Failed,
}

/// <summary>Where the image the engine read came from.</summary>
public enum PdfOcrImageSource
{
    /// <summary>No image: the page was skipped.</summary>
    None,

    /// <summary>The page's own scanned image, its pixels as stored.</summary>
    PageImage,

    /// <summary>A render of the page.</summary>
    Rendered,
}

/// <summary>One page's part of a <see cref="PdfOcrReport"/>.</summary>
/// <param name="PageIndex">The page, from 0.</param>
/// <param name="Status">What happened to it.</param>
public sealed record PdfOcrPageReport(int PageIndex, PdfOcrPageStatus Status)
{
    /// <summary>Gets how many words were added.</summary>
    public int Words { get; init; }

    /// <summary>Gets the mean confidence of the words added, from 0 to 1, or <see langword="null"/> when the engine gives none.</summary>
    public double? MeanConfidence { get; init; }

    /// <summary>Gets how long the engine took on the page.</summary>
    public TimeSpan EngineTime { get; init; }

    /// <summary>Gets where the image the engine read came from.</summary>
    public PdfOcrImageSource Source { get; init; }

    /// <summary>Gets the resolution of that image, in pixels per inch, or 0 when the page was skipped.</summary>
    public double Dpi { get; init; }

    /// <summary>Gets the clockwise turn the engine found would put the page upright: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }

    /// <summary>Gets how many glyphs of an earlier invisible text layer were removed (<see cref="PdfOcrMode.Redo"/>).</summary>
    public int RemovedGlyphs { get; init; }

    /// <summary>Gets why the page was skipped, the engine's error, or <see langword="null"/>.</summary>
    public string? Message { get; init; }
}

/// <summary>What <see cref="PdfOcr.MakeSearchableAsync"/> did, page by page.</summary>
/// <param name="Pages">One report per page looked at, in page order.</param>
public sealed record PdfOcrReport(IReadOnlyList<PdfOcrPageReport> Pages)
{
    /// <summary>Gets how many words were added over all pages.</summary>
    public int Words => Pages.Sum(page => page.Words);

    /// <summary>Gets how long the engine took over all pages, added up.</summary>
    public TimeSpan EngineTime => TimeSpan.FromTicks(Pages.Sum(page => page.EngineTime.Ticks));

    /// <summary>Gets the PDF/A conversion's report when <see cref="PdfOcrOptions.PdfA"/> asked for one, else <see langword="null"/>.</summary>
    public PdfAReport? PdfA { get; init; }
}
