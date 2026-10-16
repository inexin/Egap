namespace Egap.Pdf;

/// <summary>One PDF/A rule a document broke (Phase 5 design Q12, Q13), and whether conversion can put it right or did.</summary>
/// <param name="Clause">The clause of the PDF/A part, such as <c>6.2.11.4</c> in ISO 19005-2.</param>
/// <param name="Description">What was wrong and, when fixed, what was done.</param>
/// <param name="Page">The page number (from 1) when the problem is on one page.</param>
/// <param name="Fixable">Whether <see cref="PdfAConverter.Convert(PdfDocument, PdfAConformance, PdfAConvertOptions?)"/> can put it right.</param>
/// <param name="Fixed">Whether it was put right.</param>
public sealed record PdfAIssue(string Clause, string Description, int? Page, bool Fixable, bool Fixed);

/// <summary>
/// What a PDF/A conversion did or a check found (Phase 5 design Q12, Q13). A conversion that could not meet every rule
/// changed nothing; its report lists every issue, the ones it would have fixed with <see cref="PdfAIssue.Fixable"/> set.
/// </summary>
public sealed class PdfAReport
{
    internal PdfAReport(PdfAConformance conformance, bool succeeded, IReadOnlyList<PdfAIssue> issues)
    {
        Conformance = conformance;
        Succeeded = succeeded;
        Issues = issues;
    }

    /// <summary>The standard's name for the report's heading when it is not a PDF/A part.</summary>
    internal string? Standard { get; init; }

    /// <summary>The conformance converted to or checked against; <see cref="PdfAConformance.None"/> for a PDF/UA check.</summary>
    public PdfAConformance Conformance { get; }

    /// <summary>
    /// For a conversion, whether every rule Egap enforces is now met and the document saves as <see cref="Conformance"/>;
    /// for a check, whether no rule was broken. Egap checks the rules it enforces; veraPDF is the full judge (design Q13).
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>Every broken rule found, fixed or not.</summary>
    public IReadOnlyList<PdfAIssue> Issues { get; }

    /// <summary>The issues conversion put right.</summary>
    public IEnumerable<PdfAIssue> Fixes => Issues.Where(i => i.Fixed);

    /// <summary>The issues still standing: for a failed conversion, the ones it cannot fix are why nothing changed.</summary>
    public IEnumerable<PdfAIssue> Unmet => Issues.Where(i => !i.Fixed);

    /// <summary>A line per issue.</summary>
    public override string ToString()
    {
        string head = $"{Standard ?? PdfA.PdfAWriter.Name(Conformance)}: {(Succeeded ? "met" : "not met")}";
        return Issues.Count == 0 ? head : head + Environment.NewLine + string.Join(Environment.NewLine, Issues.Select(i =>
            $"  {(i.Fixed ? "fixed" : i.Fixable ? "fixable" : "unmet")} {i.Clause}{(i.Page is int p ? $" page {p}" : "")}: {i.Description}"));
    }
}

/// <summary>Options for <see cref="PdfAConverter.Convert(PdfDocument, PdfAConformance, PdfAConvertOptions?)"/>.</summary>
public sealed record PdfAConvertOptions
{
    /// <summary>The default options: system fonts used for fonts that are not embedded, no substitution.</summary>
    public static PdfAConvertOptions Default { get; } = new();

    /// <summary>
    /// Whether a font that is not embedded may be embedded from the same font installed on this machine (found by its
    /// PostScript or family and style name). The default is true.
    /// </summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// Whether a font that is not embedded, and not found, may be replaced by a bundled substitute (Liberation, Carlito or
    /// Caladea, by the font's name and flags) with the PDF's widths kept, as Ghostscript does. Text keeps its place but
    /// changes how it looks, so it is off by default and each substitution is reported (design Q12).
    /// </summary>
    public bool AllowFontSubstitution { get; init; }

    /// <summary>
    /// The ICC profile of the output intent added to a file that has none, such as a CMYK printing profile for a file that
    /// paints in DeviceCMYK. Left out, the intent is sRGB, which suits RGB and gray content only (design Q12).
    /// </summary>
    public ReadOnlyMemory<byte>? OutputIntentProfile { get; init; }

    /// <summary>The output condition identifier the added intent gives its profile, such as <c>FOGRA39</c>; "Custom" when left out.</summary>
    public string? OutputCondition { get; init; }
}
