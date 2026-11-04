namespace Egap.Documents;

/// <summary>The kind of file <see cref="Document.Identify(Stream, string?)"/> finds, without loading it.</summary>
public enum DocumentFormatKind
{
    /// <summary>Not a file Egap reads: binary data no format here knows.</summary>
    Unknown,

    /// <summary>A PDF file.</summary>
    Pdf,

    /// <summary>
    /// A Word file: DOCX, DOTX, DOCM or DOTM, or an old binary Word file (which reading refuses with a clear error). An
    /// encrypted Word file is <see cref="Encrypted"/>.
    /// </summary>
    Docx,

    /// <summary>An HTML or XHTML page; reading it needs the Egap.Documents.Html package.</summary>
    Html,

    /// <summary>A Markdown file; reading it needs the Egap.Documents.Markdown package.</summary>
    Markdown,

    /// <summary>A plain-text file.</summary>
    Text,

    /// <summary>A Docling JSON document.</summary>
    DoclingJson,

    /// <summary>An EPUB book (EPUB 2 or 3); reading and writing it needs the Egap.Documents.Html package.</summary>
    Epub,

    /// <summary>An MHTML web archive (.mht, .mhtml); reading and writing it needs the Egap.Documents.Html package.</summary>
    Mhtml,

    /// <summary>An RTF file (Rich Text Format, as Word and WordPad write it).</summary>
    Rtf,

    /// <summary>
    /// An encrypted Office file: a password-protected Word document (or another Office package) in a compound file. Loading
    /// it needs its password, in <see cref="DocumentLoadOptions.Password"/> or <see cref="DocxLoadOptions.Password"/>.
    /// </summary>
    Encrypted,

    /// <summary>
    /// OpenDocument Text: an ODT document or OTT template (a ZIP package whose stored first entry is its <c>mimetype</c>), or a
    /// flat FODT file (one XML document).
    /// </summary>
    Odt,

    /// <summary>
    /// An Excel workbook: XLSX, XLSM, XLTX or XLTM, known by a package part under <c>xl/</c> or by its extension. It is a
    /// spreadsheet, which Egap.Documents does not load as a document.
    /// </summary>
    Xlsx,
}
