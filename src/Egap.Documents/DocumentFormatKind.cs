namespace Egap.Documents;

/// <summary>The kind of file <see cref="Document.Identify(Stream, string?)"/> finds, without loading it.</summary>
public enum DocumentFormatKind
{
    /// <summary>Not a file Egap reads: binary data no format here knows.</summary>
    Unknown,

    /// <summary>A PDF file.</summary>
    Pdf,

    /// <summary>
    /// A Word file: DOCX, DOTX, DOCM or DOTM (a binary Word file is <see cref="Doc"/>), or another compound file Egap does
    /// not read, whose error names it. An encrypted Word file is <see cref="Encrypted"/>.
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
    /// An encrypted file: a password-protected Word document (an encrypted Office package or a password-protected binary
    /// Word file) in a compound file, or a password-protected OpenDocument text package. Loading it needs its password, in <see cref="DocumentLoadOptions.Password"/>,
    /// <see cref="DocxLoadOptions.Password"/> or <see cref="OdtLoadOptions.Password"/>.
    /// </summary>
    Encrypted,

    /// <summary>
    /// OpenDocument Text: an ODT document or OTT template (a ZIP package whose stored first entry is its <c>mimetype</c>), or a
    /// flat FODT file (one XML document). A password-protected one is <see cref="Encrypted"/>.
    /// </summary>
    Odt,

    /// <summary>
    /// An Excel workbook: XLSX, XLSM, XLTX or XLTM, known by a package part under <c>xl/</c> or by its extension. It is a
    /// spreadsheet, which Egap.Documents does not load as a document.
    /// </summary>
    Xlsx,

    /// <summary>
    /// A binary Word document or template (DOC or DOT): Word 97 to 2003, and Word 6 and Word 95, a compound file with a
    /// <c>WordDocument</c> stream. Egap reads it and does not write it. A password-protected one is <see cref="Encrypted"/>.
    /// </summary>
    Doc,
}
