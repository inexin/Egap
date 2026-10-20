namespace Egap.Documents;

/// <summary>The kind of file <see cref="Document.Identify(Stream, string?)"/> finds, without loading it.</summary>
public enum DocumentFormatKind
{
    /// <summary>Not a file Egap reads: binary data no format here knows.</summary>
    Unknown,

    /// <summary>A PDF file.</summary>
    Pdf,

    /// <summary>A Word file: DOCX, DOTX, DOCM or DOTM, or an old binary Word file (which reading refuses with a clear error).</summary>
    Docx,

    /// <summary>An HTML or XHTML page; reading it needs the Egap.Documents.Html package.</summary>
    Html,

    /// <summary>A Markdown file; reading it needs the Egap.Documents.Markdown package.</summary>
    Markdown,

    /// <summary>A plain-text file.</summary>
    Text,

    /// <summary>A Docling JSON document.</summary>
    DoclingJson,
}
