namespace Egap.Pdf;

/// <summary>
/// Converts documents to PDF/A and checks them against the rules Egap enforces (Phase 5 design Q12, Q13). Conversion
/// keeps the file's objects and fixes what it can, reporting every fix; if any rule cannot be met, nothing is changed. A
/// converted document saves by the part's rules from then on: written whole, without encryption, as the part's version.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("report.pdf");
/// PdfAReport report = PdfAConverter.Convert(pdf, PdfAConformance.PdfA2u);
/// if (report.Succeeded)
/// {
///     pdf.Save("archive.pdf");
/// }
/// else
/// {
///     Console.WriteLine(report);
/// }
/// </code>
/// </example>
public static class PdfAConverter
{
    /// <summary>
    /// Converts a document to the default conformance: PDF/A-4 for a PDF 2.0 document, PDF/A-2b for any other (design Q11).
    /// </summary>
    public static PdfAReport Convert(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Convert(document, DefaultFor(document));
    }

    /// <summary>
    /// Converts a document to <paramref name="conformance"/>: adds the output intent and identification, removes
    /// encryption, scripts and forbidden actions, draws missing appearances, embeds fonts, re-encodes LZW and more, each
    /// fix a report entry. When a rule cannot be met the document is left unchanged and the report says why.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="conformance"/> is <see cref="PdfAConformance.None"/>.</exception>
    public static PdfAReport Convert(PdfDocument document, PdfAConformance conformance, PdfAConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (conformance == PdfAConformance.None)
        {
            throw new ArgumentException("Choose a PDF/A conformance to convert to.", nameof(conformance));
        }

        options ??= PdfAConvertOptions.Default;
        List<PdfAIssue> found = PdfA.PdfARules.Run(document, conformance, options, fixing: false, allowLossy: true);
        if (found.Any(i => !i.Fixable))
        {
            return new PdfAReport(conformance, false, found);
        }

        List<PdfAIssue> fixes = PdfA.PdfARules.Run(document, conformance, options, fixing: true, allowLossy: true);
        document.WriterConformance = conformance;

        // Fixes can reveal what checking could not see (an appearance drawn with a font to embed): a second pass settles them.
        List<PdfAIssue> rest = PdfA.PdfARules.Run(document, conformance, options, fixing: true, allowLossy: true);
        List<PdfAIssue> all = [.. fixes, .. rest];
        return new PdfAReport(conformance, all.All(i => i.Fixed), all);
    }

    /// <summary>
    /// Checks a document against the rules of <paramref name="conformance"/> that Egap enforces, changing nothing; each
    /// issue says whether conversion can fix it (design Q13). veraPDF is the full judge.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="conformance"/> is <see cref="PdfAConformance.None"/>.</exception>
    public static PdfAReport Check(PdfDocument document, PdfAConformance conformance)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (conformance == PdfAConformance.None)
        {
            throw new ArgumentException("Choose a PDF/A conformance to check against.", nameof(conformance));
        }

        List<PdfAIssue> found = PdfA.PdfARules.Run(document, conformance, PdfAConvertOptions.Default, fixing: false, allowLossy: true);
        return new PdfAReport(conformance, found.Count == 0, found);
    }

    /// <summary>
    /// Checks a document against the PDF/UA-1 basics Egap enforces for documents it creates (ISO 14289-1, design Q14):
    /// identification, title and language, every piece of content tagged or an artifact, figures described, annotations
    /// tagged, fonts embedded. Changes nothing; veraPDF's PDF/UA profile is the full judge.
    /// </summary>
    public static PdfAReport CheckUniversalAccessibility(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<PdfAIssue> found = PdfA.PdfUARules.Run(document, fixing: false);
        return new PdfAReport(PdfAConformance.None, found.Count == 0, found) { Standard = "PDF/UA-1" };
    }

    /// <summary>The conformance <see cref="Convert(PdfDocument)"/> uses: PDF/A-4 for PDF 2.0, PDF/A-2b otherwise.</summary>
    public static PdfAConformance DefaultFor(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return string.CompareOrdinal(document.Version, "2.0") >= 0 ? PdfAConformance.PdfA4 : PdfAConformance.PdfA2b;
    }
}
