namespace Egap.Pdf;

/// <summary>
/// Checks documents against PDF/X and converts them to it (Phase 8 design Q9, Q10). PDF/X-4 and X-6 are checked and
/// converted; X-1a and X-3 too when the file has no transparency, which conversion does not flatten; X-4p, X-5g, X-5pg and
/// X-6p are checked only; X-5n and X-6n are checked, with an n-colorant output intent. Conversion fixes in place what it
/// can (output intent, trim and bleed boxes, identification and metadata, trapping key, fonts, colours through
/// <see cref="PdfColorConverter"/>, transfer functions, annotations in the print area, actions) and reports every fix; when
/// a rule cannot be met nothing is changed. Issues are <see cref="PdfAIssue"/>s, as for PDF/A. No open validator judges
/// PDF/X: Acrobat Preflight or PackzView is the full judge.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("brochure.pdf");
/// PdfAReport report = PdfXConverter.Convert(pdf, PdfXConformance.PdfX4, new PdfXConvertOptions { OutputCondition = PdfOutputCondition.Fogra39, Bleed = 3 });
/// if (report.Succeeded)
/// {
///     pdf.Save("print.pdf");
/// }
/// </code>
/// </example>
public static class PdfXConverter
{
    /// <summary>Checks a document against <paramref name="conformance"/>'s rules, changing nothing; each issue says whether conversion can fix it.</summary>
    /// <exception cref="ArgumentException"><paramref name="conformance"/> is <see cref="PdfXConformance.None"/>.</exception>
    public static PdfAReport Check(PdfDocument document, PdfXConformance conformance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Require(conformance);
        List<PdfAIssue> found = Print.PdfXRules.Run(document, conformance, PdfXConvertOptions.Default, fixing: false, cancellationToken);
        return Report(conformance, found.Count == 0, found);
    }

    /// <summary>
    /// Converts a document to <paramref name="conformance"/> in place; the report lists every fix. When a rule cannot be met,
    /// or the version is one Egap only checks, the document is left unchanged and the report says why.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="conformance"/> is <see cref="PdfXConformance.None"/>.</exception>
    public static PdfAReport Convert(PdfDocument document, PdfXConformance conformance, PdfXConvertOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Require(conformance);
        options ??= PdfXConvertOptions.Default;
        document.WrittenResources.Finish();
        List<PdfAIssue> found = Print.PdfXRules.Run(document, conformance, options, fixing: false, cancellationToken);
        if (conformance is PdfXConformance.PdfX4p or PdfXConformance.PdfX5g or PdfXConformance.PdfX5pg or PdfXConformance.PdfX6p)
        {
            found.Add(new PdfAIssue("Conversion", $"{Print.PdfXRules.VersionName(conformance)} is checked only; convert to PDF/X-4 or X-6 instead", null, false, false));
        }

        if (found.Any(i => !i.Fixable))
        {
            return Report(conformance, false, found);
        }

        List<PdfAIssue> fixes = Print.PdfXRules.Run(document, conformance, options, fixing: true, cancellationToken);
        document.WriterPdfX = conformance;
        document.ResetRenderCache();

        // Fixes can reveal what checking could not see (a converted colour space, a box set from another): a second pass settles them.
        List<PdfAIssue> rest = Print.PdfXRules.Run(document, conformance, options, fixing: true, cancellationToken);
        List<PdfAIssue> all = [.. fixes, .. rest];
        return Report(conformance, all.All(i => i.Fixed), all);
    }

    private static PdfAReport Report(PdfXConformance conformance, bool succeeded, IReadOnlyList<PdfAIssue> issues)
    {
        return new PdfAReport(PdfAConformance.None, succeeded, issues) { Standard = $"{Print.PdfXRules.VersionName(conformance)} ({Print.PdfXRules.Standard(conformance)})" };
    }

    private static void Require(PdfXConformance conformance)
    {
        if (conformance == PdfXConformance.None)
        {
            throw new ArgumentException("Choose a PDF/X version.", nameof(conformance));
        }
    }
}
