
using Egap.Pdf;

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
public static class PdfToDocument
{
    /// <summary>Converts every page of <paramref name="document"/> into a new <see cref="Document"/>.</summary>
    /// <param name="document">The PDF.</param>
    /// <param name="options">How to analyse and build; <see cref="PdfToDocumentOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Stops between pages and sections.</param>
    public static Document Convert(PdfDocument document, PdfToDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= PdfToDocumentOptions.Default;
        (List<PageDraft> drafts, List<Roles.NoteLink> notes) = PdfLayoutAnalyzer.Drafts(document, Enumerable.Range(0, document.Pages.Count), options.Layout, cancellationToken);
        return ModelBuilder.Build(document, drafts, notes, options, cancellationToken);
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
}
