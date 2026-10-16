namespace Egap.Pdf;

/// <summary>What <see cref="PdfDocument.Sanitize"/> removes (Phase 5 design Q10); every switch is on by default.</summary>
public sealed record PdfSanitizeOptions
{
    /// <summary>The default options: everything removed.</summary>
    public static PdfSanitizeOptions Default { get; } = new();

    /// <summary>The Info dictionary and every XMP metadata stream, the document's and each object's.</summary>
    public bool Metadata { get; init; } = true;

    /// <summary>JavaScript and every action but going to a place in the document: open actions, additional actions, links' and bookmarks' actions.</summary>
    public bool JavaScript { get; init; } = true;

    /// <summary>Embedded files: the attachments list, associated files and file attachment annotations.</summary>
    public bool Attachments { get; init; } = true;

    /// <summary>The content of layers that are hidden, and the layers themselves.</summary>
    public bool HiddenLayers { get; init; } = true;

    /// <summary>Comments: every markup annotation, from notes to highlights, stamps and drawings.</summary>
    public bool Comments { get; init; } = true;

    /// <summary>Form data: every field reset to its default value, and an XFA form's data.</summary>
    public bool FormData { get; init; } = true;

    /// <summary>Page thumbnails (<c>/Thumb</c>).</summary>
    public bool Thumbnails { get; init; } = true;

    /// <summary>Private application data (<c>/PieceInfo</c>) of the document, its pages and its forms, and the embedded search index.</summary>
    public bool PrivateData { get; init; } = true;
}

/// <summary>What sanitising removed, by kind (Phase 5 design Q10).</summary>
/// <param name="Metadata">Metadata dictionaries and streams removed.</param>
/// <param name="Actions">Actions and scripts removed.</param>
/// <param name="Attachments">Embedded files removed.</param>
/// <param name="Layers">Hidden layers removed with their content.</param>
/// <param name="Comments">Comment annotations removed.</param>
/// <param name="Fields">Form fields reset.</param>
/// <param name="Thumbnails">Thumbnails removed.</param>
/// <param name="PrivateData">Private application data and search indexes removed.</param>
public sealed record PdfSanitizeResult(int Metadata, int Actions, int Attachments, int Layers, int Comments, int Fields, int Thumbnails, int PrivateData);
