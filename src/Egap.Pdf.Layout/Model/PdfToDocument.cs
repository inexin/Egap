using Egap.Documents;

namespace Egap.Pdf.Layout;

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

    /// <summary>
    /// Whether the TrueType and OpenType fonts the PDF embeds are copied into the document (design Q6). False by default:
    /// most are subsets, holding only the glyphs the PDF uses, so text typed later may not show in them.
    /// </summary>
    public bool EmbedFonts { get; init; }
}
