using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Pdf.Conversion;

/// <summary>
/// Flow documents saved as PDF (Phase 6 design Q2, Build Plan task 22): each laid-out page drawn on a
/// <see cref="PdfCanvas"/>, with link annotations for hyperlinks, an outline, named destinations for bookmarks, the
/// document's properties as metadata, and on request tags and PDF/A.
/// </summary>
public static class PdfExport
{
    /// <summary>Saves the document as a PDF file.</summary>
    /// <example>
    /// <code>
    /// Document word = DocxFormat.Load("report.docx");
    /// word.SaveAsPdf("report.pdf", new PdfExportOptions { Tagged = true });
    /// </code>
    /// </example>
    public static void SaveAsPdf(this Document document, string path, PdfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using PdfDocument pdf = document.ToPdf(options);
        pdf.Save(path, options?.Save);
    }

    /// <summary>Writes the document as PDF to a stream.</summary>
    public static void SaveAsPdf(this Document document, Stream output, PdfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        using PdfDocument pdf = document.ToPdf(options);
        pdf.Save(output, options?.Save);
    }

    /// <summary>Lays the document out and makes a new PDF of it, open for more changes before it is saved.</summary>
    public static PdfDocument ToPdf(this Document document, PdfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Layout(options?.Layout).ToPdf(options);
    }

    /// <summary>Makes a new PDF of a layout already made, open for more changes before it is saved.</summary>
    public static PdfDocument ToPdf(this DocumentLayout layout, PdfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new PdfExporter(layout, options ?? PdfExportOptions.Default).Export();
    }
}
