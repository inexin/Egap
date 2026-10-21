using Egap.Documents.Layout;
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>What a PDF's outline (its bookmarks pane) is built from.</summary>
public enum OutlineFrom
{
    /// <summary>No outline.</summary>
    None,

    /// <summary>Paragraphs with an outline level (the heading styles), nested by level.</summary>
    Headings,

    /// <summary>The document's bookmarks, in document order, leaving out hidden ones (names starting with an underscore).</summary>
    Bookmarks,
}

/// <summary>How a flow document is saved as PDF (Phase 6 design Q2, Build Plan task 22).</summary>
public sealed record PdfExportOptions
{
    /// <summary>The options with every default.</summary>
    public static PdfExportOptions Default { get; } = new();

    /// <summary>How the document is laid out (fonts); the defaults when <see langword="null"/>.</summary>
    public LayoutOptions? Layout { get; init; }

    /// <summary>
    /// Whether the PDF is tagged: a structure tree from the model (headings, paragraphs, lists, tables, figures with their
    /// alternative text, notes, links), with headers, footers and borders as artifacts. Always on for PDF/A level a and for
    /// <see cref="UniversalAccessibility"/>.
    /// </summary>
    public bool Tagged { get; init; }

    /// <summary>What the outline is built from; headings by default.</summary>
    public OutlineFrom Outline { get; init; } = OutlineFrom.Headings;

    /// <summary>Whether hyperlinks (and HYPERLINK fields) become link annotations; on by default.</summary>
    public bool Links { get; init; } = true;

    /// <summary>Whether bookmarks become named destinations, so links from outside can open the PDF at them; on by default.</summary>
    public bool NamedDestinations { get; init; } = true;

    /// <summary>
    /// Whether form controls become PDF form fields at their laid-out place: HTML inputs, check boxes, radio buttons,
    /// selects and text areas, and DOCX plain text, date, drop-down list, combo box and check box content controls. Each
    /// field is named by the control's tag (an HTML name) and starts with its value; the control's own text is taken out of
    /// the page. Off by default.
    /// </summary>
    public bool FormFields { get; init; }

    /// <summary>The PDF/A part and level the file is written to, with Phase 5's writer rules; none by default.</summary>
    public PdfAConformance Conformance { get; init; }

    /// <summary>Whether the file claims PDF/UA-1 (and is tagged); off by default.</summary>
    public bool UniversalAccessibility { get; init; }

    /// <summary>The document's language (a BCP 47 tag such as "en-US"); the default text language of its styles when <see langword="null"/>.</summary>
    public string? Language { get; init; }

    /// <summary>How the PDF is saved by <see cref="PdfExport.SaveAsPdf(Egap.Documents.Document, string, PdfExportOptions?)"/>.</summary>
    public PdfSaveOptions? Save { get; init; }

    /// <summary>Whether this export tags the PDF.</summary>
    internal bool IsTagged => Tagged || UniversalAccessibility || Conformance is PdfAConformance.PdfA2a or PdfAConformance.PdfA3a;
}
