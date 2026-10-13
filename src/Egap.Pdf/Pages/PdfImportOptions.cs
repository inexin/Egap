namespace Egap.Pdf;

/// <summary>Options for importing pages.</summary>
public sealed record PdfImportOptions
{
    /// <summary>The default options.</summary>
    public static PdfImportOptions Default { get; } = new();

    /// <summary>
    /// Whether an imported form field whose top-level name the document already has joins that field, so the two share one
    /// value, as copying a blank template several times wants (Phase 2 Q5). The default, false, renames the imported field
    /// with a suffix and a warning, so each keeps its own value, as merging filled forms wants. Fields that cannot share
    /// widgets, such as a field merged with its widget, are renamed either way.
    /// </summary>
    public bool JoinFields { get; init; }
}
