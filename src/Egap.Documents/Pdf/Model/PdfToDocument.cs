
using Egami.Ocr;

using Egap.Pdf;
using Egap.Pdf.Ocr;

namespace Egap.Documents.Pdf;

/// <summary>
/// PDF to the flow document model (design Q6 to Q8, Q10): layout analysis, then the pages joined into sections with styles,
/// lists, tables, pictures, headers and footers and footnotes, ready to save as Word, HTML or Markdown through Egap.Documents.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("report.pdf");
/// Document doc = PdfToDocument.Convert(pdf);
/// DocxFormat.Save(doc, "report.docx");
/// </code>
/// </example>
public static partial class PdfToDocument
{
    /// <summary>Converts every page of <paramref name="document"/> into a new <see cref="Document"/>.</summary>
    /// <param name="document">The PDF.</param>
    /// <param name="options">How to analyse and build; <see cref="PdfToDocumentOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Stops between pages and sections.</param>
    public static Document Convert(PdfDocument document, PdfToDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= PdfToDocumentOptions.Default;
        if (EmbeddedCopy(document, options) is { } source)
        {
            return source;
        }

        (List<PageDraft> drafts, List<Roles.NoteLink> notes) = PdfLayoutAnalyzer.Drafts(document, Enumerable.Range(0, document.Pages.Count), options.Layout, cancellationToken);
        return ModelBuilder.Build(document, drafts, notes, options, cancellationToken);
    }

    /// <summary>
    /// Converts every page of <paramref name="document"/> into a new <see cref="Document"/>, as <see cref="Convert"/> does, and
    /// runs scanned pages (no text, one image over most of the page) through <see cref="PdfToDocumentOptions.OcrEngine"/> when
    /// one is set, so their text becomes paragraphs instead of a picture. <paramref name="document"/> is not changed.
    /// </summary>
    /// <param name="document">The PDF.</param>
    /// <param name="options">How to analyse and build, and the OCR engine; <see cref="PdfToDocumentOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the OCR and stops between pages and sections.</param>
    /// <returns>The document.</returns>
    public static async Task<Document> ConvertAsync(PdfDocument document, PdfToDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= PdfToDocumentOptions.Default;
        if (EmbeddedCopy(document, options) is { } source)
        {
            return source;
        }

        IEnumerable<int> all = Enumerable.Range(0, document.Pages.Count);
        (List<PageDraft> drafts, List<Roles.NoteLink> notes) = PdfLayoutAnalyzer.Drafts(document, all, options.Layout, cancellationToken);
        List<int> scans = [.. drafts.Where(d => ModelBuilder.Scan(d) is not null).Select(d => d.Content.Index)];
        if (options.OcrEngine is not IOcrEngine engine || scans.Count == 0)
        {
            return ModelBuilder.Build(document, drafts, notes, options, cancellationToken);
        }

        // The text layer goes into a copy, which layout analysis then reads as any page with text.
        using MemoryStream bytes = new();
        document.Save(bytes, new PdfSaveOptions { AllowInvalidatingSignatures = true });
        using PdfDocument copy = PdfDocument.Open(bytes.ToArray(), document.Options);
        PdfOcrOptions ocr = options.Ocr with { Mode = PdfOcrMode.Skip, Pages = scans, PdfA = null, AllowInvalidatingSignatures = true };
        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(copy, engine, ocr, cancellationToken).ConfigureAwait(false);
        HashSet<int> read = [.. report.Pages.Where(p => p.Words > 0).Select(p => p.PageIndex)];
        foreach (PdfOcrPageReport failed in report.Pages.Where(p => p.Status == PdfOcrPageStatus.Failed))
        {
            options.OnWarning?.Invoke(new PdfToDocumentWarning(PdfToDocumentWarningKind.OcrFailed, failed.PageIndex,
                $"OCR failed on page {failed.PageIndex + 1}: {failed.Message}"));
        }

        (drafts, notes) = PdfLayoutAnalyzer.Drafts(copy, all, options.Layout, cancellationToken);
        foreach (PageDraft draft in drafts.Where(d => read.Contains(d.Content.Index)))
        {
            // The scan itself is the page the text came from, not a figure on it.
            double area = draft.Content.Width * draft.Content.Height;
            draft.Figures.RemoveAll(f => f.Box.Intersect(draft.Content.Bounds).Area >= area * 0.6);
        }

        Document result = ModelBuilder.Build(copy, drafts, notes, options, cancellationToken);
        return result;
    }

    /// <summary>A hybrid PDF's own document when <see cref="PdfToDocumentOptions.EmbeddedSource"/> lets it be used; null to analyse the pages.</summary>
    private static Document? EmbeddedCopy(PdfDocument document, PdfToDocumentOptions options)
    {
        Document? source = HybridPdf.Open(document, options.EmbeddedSource, out string? skipped);
        if (skipped is not null)
        {
            options.OnWarning?.Invoke(new PdfToDocumentWarning(PdfToDocumentWarningKind.EmbeddedSourceSkipped, 0, skipped));
        }

        return source;
    }
}

/// <summary>How <see cref="PdfToDocument"/> analyses a PDF and builds the document.</summary>
public sealed record PdfToDocumentOptions
{
    /// <summary>The defaults: Egap's layout analysis with its defaults, figures at 200 dpi, no fonts copied.</summary>
    public static PdfToDocumentOptions Default { get; } = new();

    /// <summary>How the pages are analysed: tags, tables, replaceable stages and thresholds.</summary>
    public PdfLayoutOptions Layout { get; init; } = PdfLayoutOptions.Default;

    /// <summary>The resolution, in pixels per inch, at which figures drawn from the page (rather than kept as stored images) become pictures. 200 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 10 and 1200.</exception>
    public double FigureResolution
    {
        get;
        init => field = value is >= 10 and <= 1200 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Figures are drawn at 10 to 1200 dpi.");
    } = 200;

    /// <summary>How pages become the document: reflowing text by default, or every line placed where it was (design Q8).</summary>
    public PdfToDocumentMode Mode { get; init; } = PdfToDocumentMode.Flow;

    /// <summary>
    /// In <see cref="PdfToDocumentMode.Flow"/>, blocks the analysis is less sure of than this (0 to 1) are kept where they
    /// were as positioned text frames rather than reflowed. 0.5 by default; 0 reflows everything the analysis found.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 0 and 1.</exception>
    public double ConfidenceThreshold
    {
        get;
        init => field = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Confidence runs from 0 to 1.");
    } = 0.5;

    /// <summary>Called for what the conversion could not carry over as text, such as a scanned page kept as a picture; null to ignore.</summary>
    public Action<PdfToDocumentWarning>? OnWarning { get; init; }

    /// <summary>
    /// Whether the TrueType and OpenType fonts the PDF embeds are copied into the document (design Q6). False by default:
    /// most are subsets, holding only the glyphs the PDF uses, so text typed later may not show in them.
    /// </summary>
    public bool EmbedFonts { get; init; }

    /// <summary>
    /// The OCR engine <see cref="PdfToDocument.ConvertAsync"/> and <c>Document.LoadPdfAsync</c> run scanned pages through, so
    /// their text becomes paragraphs; null (the default) keeps a scanned page as a picture with a warning. The sync
    /// <see cref="PdfToDocument.Convert"/> never runs it.
    /// </summary>
    public IOcrEngine? OcrEngine { get; init; }

    /// <summary>
    /// How scanned pages are read by <see cref="OcrEngine"/>: languages, resolution, confidence, parallel pages. Its mode,
    /// pages and PDF/A level are set by the conversion.
    /// </summary>
    public PdfOcrOptions Ocr { get; init; } = PdfOcrOptions.Default;

    /// <summary>
    /// Whether a hybrid PDF's embedded DOCX (see <see cref="PdfExportOptions.EmbedSource"/>) is read instead of its pages:
    /// <see cref="Documents.Pdf.EmbeddedSource.Prefer"/> by default.
    /// </summary>
    public EmbeddedSource EmbeddedSource { get; init; }
}

/// <summary>How <see cref="PdfToDocument"/> turns pages into a document (design Q8).</summary>
public enum PdfToDocumentMode
{
    /// <summary>
    /// Reflowing paragraphs in sections, for editing. What cannot flow (text over a figure, a block the analysis is unsure
    /// of, a page of scattered labels) is kept where it was in a positioned text frame, so no text is lost.
    /// </summary>
    Flow = 0,

    /// <summary>Every line in a positioned text frame where it was on its page, as Acrobat's "Retain Page Layout": it looks like the PDF but edits poorly.</summary>
    Exact = 1,
}

/// <summary>What <see cref="PdfToDocument"/> could not carry over as it was, on which page.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="PageIndex">The page, from 0.</param>
/// <param name="Message">A sentence for a person.</param>
public sealed record PdfToDocumentWarning(PdfToDocumentWarningKind Kind, int PageIndex, string Message);

/// <summary>The kinds of <see cref="PdfToDocumentWarning"/>.</summary>
public enum PdfToDocumentWarningKind
{
    /// <summary>A page with no text but a picture of a page (a scan): it is kept as a picture, its text not recognised.</summary>
    ScannedPage = 0,

    /// <summary>The OCR engine failed on a scanned page, which is kept as a picture.</summary>
    OcrFailed = 1,

    /// <summary>A hybrid PDF's embedded source was not used, because it could not be trusted to match the pages; the pages were analysed.</summary>
    EmbeddedSourceSkipped = 2,
}
