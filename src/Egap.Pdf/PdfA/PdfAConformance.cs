namespace Egap.Pdf;

/// <summary>A PDF/A part and conformance level a file claims (ISO 19005; Phase 5 design Q11).</summary>
public enum PdfAConformance
{
    /// <summary>No PDF/A claim.</summary>
    None,

    /// <summary>PDF/A-1b (ISO 19005-1, PDF 1.4): reliable appearance; no transparency, object streams or cross-reference streams.</summary>
    PdfA1b,

    /// <summary>PDF/A-2b (ISO 19005-2, PDF 1.7): reliable appearance. The default for new files.</summary>
    PdfA2b,

    /// <summary>PDF/A-2u: PDF/A-2b with Unicode for all text.</summary>
    PdfA2u,

    /// <summary>PDF/A-3b (ISO 19005-3): PDF/A-2b that may carry attachments of any kind, each with its relationship.</summary>
    PdfA3b,

    /// <summary>PDF/A-3u: PDF/A-3b with Unicode for all text.</summary>
    PdfA3u,

    /// <summary>PDF/A-4 (ISO 19005-4, PDF 2.0). The default for PDF 2.0 files.</summary>
    PdfA4,

    /// <summary>PDF/A-4f: PDF/A-4 that may carry attachments of any kind.</summary>
    PdfA4f,
}

/// <summary>Options for <see cref="PdfDocument.Create(PdfCreateOptions)"/>.</summary>
public sealed record PdfCreateOptions
{
    /// <summary>The default options: a plain PDF 1.7 document.</summary>
    public static PdfCreateOptions Default { get; } = new();

    /// <summary>
    /// The PDF/A conformance the new document claims (Phase 5 design Q11). With one, the document gets the part's PDF version,
    /// an sRGB output intent and the XMP identification, and each save writes it by the part's rules: no encryption, a file
    /// identifier, no LZW, every font embedded (the standard 14 as metric-compatible substitutes), annotations printed, and for
    /// PDF/A-1 no object or cross-reference streams and no transparency. A rule a save cannot meet throws a
    /// <see cref="PdfException"/> naming it. The default is <see cref="PdfAConformance.None"/>.
    /// </summary>
    public PdfAConformance Conformance { get; init; }
}
