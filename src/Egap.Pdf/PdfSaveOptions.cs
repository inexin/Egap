namespace Egap.Pdf;

/// <summary>How a document is saved.</summary>
public enum PdfSaveMode
{
    /// <summary>
    /// The original bytes followed by the changed and new objects and a new cross-reference section (design hub Q4).
    /// </summary>
    Incremental,

    /// <summary>Every object reachable from the trailer written again, renumbered densely by default (design Q6).</summary>
    Rewrite,
}

/// <summary>Options for saving a document.</summary>
public sealed record PdfSaveOptions
{
    /// <summary>The default options.</summary>
    public static PdfSaveOptions Default { get; } = new();

    /// <summary>The save mode. The default is <see cref="PdfSaveMode.Incremental"/>.</summary>
    public PdfSaveMode Mode { get; init; }

    /// <summary>
    /// For a rewrite, whether objects keep their numbers and generations instead of being renumbered densely in the order
    /// they are reached from the trailer. Useful for comparing with the original while debugging. The default is
    /// <see langword="false"/>.
    /// </summary>
    public bool KeepObjectNumbers { get; init; }

    /// <summary>
    /// For a rewrite, whether objects that nothing reaches from the trailer are left out. The default is
    /// <see langword="true"/>.
    /// </summary>
    public bool RemoveUnused { get; init; } = true;

    /// <summary>
    /// Whether the file ID's second part is derived from the written content instead of being new for every save, so the
    /// same document always saves to the same bytes. The default is <see langword="false"/>.
    /// </summary>
    public bool Deterministic { get; init; }
}
