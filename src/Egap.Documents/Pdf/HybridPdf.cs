using Egap.Documents.Docx;
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>When <see cref="PdfToDocument"/> uses a DOCX copy of the document that a hybrid PDF carries.</summary>
public enum EmbeddedSourceMode
{
    /// <summary>
    /// The default: the copy is used when it can be trusted (marked as the source, a DOCX, written by Egap and not followed by
    /// any later change to the PDF); otherwise the pages are analysed, with a warning saying why the copy was not used.
    /// </summary>
    Prefer,

    /// <summary>The pages are always analysed; a copy is left alone.</summary>
    Ignore,

    /// <summary>The copy must be used: a PDF without a trusted one throws.</summary>
    Require,
}

/// <summary>
/// Hybrid PDFs: a DOCX of the document attached as an associated file marked as its source (ISO 32000-2, 14.13), so the PDF
/// opens again as the editable document it was made from.
/// </summary>
internal static class HybridPdf
{
    public const string MimeType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>The key Egap puts in the file specification, so a copy someone else attached is not taken for Egap's.</summary>
    private const string Marker = "EgapSource";

    /// <summary>Attaches a DOCX of <paramref name="document"/> to <paramref name="pdf"/> as its source.</summary>
    public static void Embed(PdfDocument pdf, Document document, PdfAConformance conformance)
    {
        if (conformance is PdfAConformance.PdfA1b or PdfAConformance.PdfA2b or PdfAConformance.PdfA2u or PdfAConformance.PdfA2a)
        {
            throw new ArgumentException($"{conformance} does not allow embedded files other than PDFs; a hybrid PDF needs PDF/A-3, PDF/A-4f or no PDF/A.", nameof(conformance));
        }

        using MemoryStream docx = new();
        DocxFormat.Save(document, docx);
        DateTimeOffset modified = document.Properties.Saved ?? document.Properties.Created ?? DateTimeOffset.UtcNow;
        string name = FileName(document);
        PdfAttachment attachment = pdf.Attachments.Add(name, docx.ToArray(), PdfAttachmentRelationship.Source, MimeType, "The editable document this PDF was made from", modified);
        attachment.FileSpecification[Marker] = PdfName.Get("DOCX");
    }

    /// <summary>
    /// The document from <paramref name="pdf"/>'s embedded DOCX when <paramref name="mode"/> lets it be used and it can be
    /// trusted; null otherwise, with <paramref name="skipped"/> saying why a copy that was there was not used.
    /// </summary>
    public static Document? Open(PdfDocument pdf, EmbeddedSourceMode mode, out string? skipped)
    {
        skipped = null;
        if (mode == EmbeddedSourceMode.Ignore)
        {
            return null;
        }

        if (TryOpen(pdf, out string? why) is { } source)
        {
            return source;
        }

        if (mode == EmbeddedSourceMode.Require)
        {
            throw new DocumentException(why ?? "The PDF has no embedded source document.");
        }

        skipped = why;
        return null;
    }

    /// <summary>The embedded DOCX's document, or null with <paramref name="why"/> (null when there is no copy at all).</summary>
    private static Document? TryOpen(PdfDocument pdf, out string? why)
    {
        why = null;
        PdfAttachment? copy = pdf.Attachments.FirstOrDefault(a => a.FileSpecification.Get<PdfName>(Marker) is not null)
            ?? pdf.Attachments.FirstOrDefault(a => a.Relationship == "Source");
        if (copy is null)
        {
            return null;
        }

        if (copy.Relationship != "Source")
        {
            why = $"The attachment '{copy.Name}' is not marked as the PDF's source.";
        }
        else if (copy.MimeType != MimeType && !copy.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            why = $"The source '{copy.Name}' is not a DOCX file.";
        }
        else if (copy.FileSpecification.Get<PdfName>(Marker) is null)
        {
            why = $"The source '{copy.Name}' was not embedded by Egap, so it may not match the pages.";
        }
        else if (pdf.Revisions.Count > 1)
        {
            why = "The PDF was changed or signed after the source was embedded, so the source may not match the pages.";
        }
        else if (copy.GetBytes() is not { } bytes || copy.VerifyChecksum() == false)
        {
            why = $"The source '{copy.Name}' cannot be read.";
        }
        else
        {
            try
            {
                return DocxFormat.Load(new MemoryStream(bytes));
            }
            catch (Exception e) when (e is DocumentException or InvalidDataException or IOException)
            {
                why = $"The source '{copy.Name}' is not a readable DOCX file: {e.Message}";
            }
        }

        return null;
    }

    private static string FileName(Document document)
    {
        string? stem = document.FileName is { Length: > 0 } file ? Path.GetFileNameWithoutExtension(file) : document.Properties.Title;
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "document";
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(c, '_');
        }

        return stem.Trim() + ".docx";
    }
}
