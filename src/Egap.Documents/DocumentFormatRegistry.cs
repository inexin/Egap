using System.Collections.Concurrent;
using System.Text;

using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents;

/// <summary>
/// The formats <see cref="Document.Load(string)"/> and <see cref="Document.Save(string)"/> reach: the core's own, and those an
/// add-on package registers once with its format class's <c>Register()</c>, such as <c>MarkdownFormat.Register()</c>. An
/// explicit call keeps the lookup free of reflection, so it works trimmed and ahead-of-time compiled.
/// </summary>
internal static class DocumentFormatRegistry
{
    /// <summary>Reads a whole file's bytes.</summary>
    internal delegate Document Reader(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings);

    /// <summary>Reads a file for conversion, taking its images and style sheets only from <paramref name="folder"/>.</summary>
    internal delegate Document FolderReader(string path, string folder);

    /// <summary>
    /// A format's readers and writer, any of which may be missing: <c>Read</c> and <c>Save</c> for whole files,
    /// <c>ReadInFolder</c> for how <see cref="Pdf.PdfConvert"/> reads a file whose links point at files beside it (HTML's and
    /// Markdown's images), and <c>ParseInFolder</c> for text whose links resolve in a folder, or nowhere when it is null (how
    /// <see cref="Pdf.PageBand"/> reads an HTML band).
    /// </summary>
    internal sealed record Format(
        DocumentFormatKind Kind,
        Reader? Read,
        Func<Document, string, IReadOnlyList<ConversionWarning>>? Save,
        FolderReader? ReadInFolder = null,
        Func<string, string?, Document>? ParseInFolder = null);

    private static readonly ConcurrentDictionary<DocumentFormatKind, Format> Formats = new()
    {
        [DocumentFormatKind.Docx] = new(DocumentFormatKind.Docx, ReadDocx, (document, path) => DocxFormat.Save(document, path)),
        [DocumentFormatKind.Text] = new(DocumentFormatKind.Text, ReadText, (document, path) => TextFormat.Save(document, path)),
        [DocumentFormatKind.Pdf] = new(DocumentFormatKind.Pdf, ReadPdf, SavePdf),

        // Docling JSON holds page layouts, which a document is read from; a document is not written back as one.
        [DocumentFormatKind.DoclingJson] = new(DocumentFormatKind.DoclingJson, ReadDocling, null),
    };

    /// <summary>Adds or replaces a format; registering the same format again changes nothing.</summary>
    public static void Register(Format format)
    {
        Formats[format.Kind] = format;
    }

    /// <summary>Whether a reader or writer for <paramref name="kind"/> is registered.</summary>
    public static bool IsRegistered(DocumentFormatKind kind) => Formats.ContainsKey(kind);

    /// <summary>The reader for <paramref name="kind"/>.</summary>
    /// <exception cref="NotSupportedException">No reader is registered; the message names the package and the call that adds one.</exception>
    public static Reader ReaderFor(DocumentFormatKind kind)
    {
        return Formats.TryGetValue(kind, out Format? format) && format.Read is { } read
            ? read
            : throw new NotSupportedException($"Reading {Name(kind)} {Missing(kind)}");
    }

    /// <summary>The reader for files of <paramref name="kind"/> that link to files beside them, or null when it has none.</summary>
    /// <exception cref="NotSupportedException">The format is not registered; the message names the package and the call that adds it.</exception>
    public static FolderReader? FolderReaderFor(DocumentFormatKind kind)
    {
        return Formats.TryGetValue(kind, out Format? format)
            ? format.ReadInFolder
            : throw new NotSupportedException($"Reading {Name(kind)} {Missing(kind)}");
    }

    /// <summary>The reader for text of <paramref name="kind"/> whose links resolve in a folder.</summary>
    /// <exception cref="NotSupportedException">The format is not registered; the message names the package and the call that adds it.</exception>
    public static Func<string, string?, Document> TextReaderFor(DocumentFormatKind kind)
    {
        return Formats.TryGetValue(kind, out Format? format) && format.ParseInFolder is { } parse
            ? parse
            : throw new NotSupportedException($"Reading {Name(kind)} {Missing(kind)}");
    }

    /// <summary>The writer for <paramref name="kind"/>.</summary>
    /// <exception cref="NotSupportedException">No writer is registered; the message names the package and the call that adds one.</exception>
    public static Func<Document, string, IReadOnlyList<ConversionWarning>> WriterFor(DocumentFormatKind kind)
    {
        return Formats.TryGetValue(kind, out Format? format) && format.Save is { } save
            ? save
            : throw new NotSupportedException($"Writing {Name(kind)} {Missing(kind)}");
    }

    private static Document ReadDocx(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings)
    {
        using MemoryStream stream = new(bytes, writable: false);
        return DocxFormat.Load(stream, out warnings);
    }

    private static Document ReadText(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings)
    {
        warnings = [];
        using MemoryStream stream = new(bytes, writable: false);
        return TextFormat.Load(stream);
    }

    private static Document ReadPdf(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings)
    {
        warnings = [];
        using PdfDocument pdf = PdfDocument.Open(bytes);
        return PdfToDocument.Convert(pdf);
    }

    private static IReadOnlyList<ConversionWarning> SavePdf(Document document, string path)
    {
        document.SaveAsPdf(path);
        return [];
    }

    private static Document ReadDocling(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings)
    {
        warnings = [];
        return DoclingJson.ToDocument(Encoding.UTF8.GetString(bytes.AsSpan(bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0)));
    }

    private static string Name(DocumentFormatKind kind) => kind switch
    {
        DocumentFormatKind.Pdf => "PDF",
        DocumentFormatKind.Docx => "DOCX",
        DocumentFormatKind.Html => "HTML",
        DocumentFormatKind.Markdown => "Markdown",
        DocumentFormatKind.Text => "plain text",
        DocumentFormatKind.DoclingJson => "Docling JSON",
        _ => "this file",
    };

    private static string Missing(DocumentFormatKind kind) => kind switch
    {
        DocumentFormatKind.Html => "needs the Egap.Documents.Html package: add it and call HtmlFormat.Register() once at startup.",
        DocumentFormatKind.Markdown => "needs the Egap.Documents.Markdown package: add it and call MarkdownFormat.Register() once at startup.",
        DocumentFormatKind.DoclingJson => "is not supported: Docling JSON holds page layouts, which DoclingJson.Write writes from PdfLayoutAnalyzer's.",
        _ => "is not supported.",
    };
}
