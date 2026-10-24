using Egap.Documents.Pdf;

namespace Egap.Documents;

/// <summary>Loading and saving without naming the format, and telling formats apart.</summary>
public sealed partial class Document
{
    // Enough for a PDF header after leading bytes (ISO 32000-2 7.5.2 allows them in the first 1024) and a Docling schema name.
    private const int HeadLength = 4096;

    /// <summary>
    /// Reads a file in any format Egap reads, telling them apart by their bytes and, for text, by the file's extension (see
    /// <see cref="Identify(Stream, string?)"/>). DOCX, PDF, plain text and Docling JSON are built in; HTML, MHTML, EPUB and
    /// Markdown need their add-on package and its <c>Register()</c> call, such as <c>MarkdownFormat.Register()</c>.
    /// </summary>
    /// <exception cref="DocumentException">The file is not one Egap can read.</exception>
    /// <exception cref="NotSupportedException">The file's format needs a package that is not registered; the message names it.</exception>
    public static Document Load(string path)
    {
        return Load(path, out _);
    }

    /// <summary>Reads a file in any format Egap reads, reporting what could not be read as it was. See <see cref="Load(string)"/>.</summary>
    /// <exception cref="DocumentException">The file is not one Egap can read.</exception>
    /// <exception cref="NotSupportedException">The file's format needs a package that is not registered; the message names it.</exception>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes = File.ReadAllBytes(path);
        Document document = Load(bytes, Path.GetExtension(path), out warnings);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads a file in any format Egap reads from a stream, which is left open. See <see cref="Load(string)"/>.</summary>
    /// <exception cref="DocumentException">The stream is not a file Egap can read.</exception>
    /// <exception cref="NotSupportedException">The file's format needs a package that is not registered; the message names it.</exception>
    public static Document Load(Stream stream)
    {
        return Load(stream, out _);
    }

    /// <summary>
    /// Reads a file in any format Egap reads from a stream, which is left open, reporting what could not be read as it was.
    /// See <see cref="Load(string)"/>.
    /// </summary>
    /// <exception cref="DocumentException">The stream is not a file Egap can read.</exception>
    /// <exception cref="NotSupportedException">The file's format needs a package that is not registered; the message names it.</exception>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Load(copy.ToArray(), null, out warnings);
    }

    /// <summary>
    /// Saves the document in the format its file extension names: .docx, .dotx, .docm and .dotm as Word's, .pdf as PDF (as
    /// <see cref="PdfExport.SaveAsPdf(Document, string, PdfExportOptions?)"/> writes it), .txt as plain text, and, once their
    /// add-on is registered, .html and .htm as HTML, .mht and .mhtml as MHTML, .epub as EPUB and .md and .markdown as Markdown.
    /// </summary>
    /// <returns>What the format could not hold.</returns>
    /// <exception cref="ArgumentException">The extension names no format Egap.Documents writes.</exception>
    /// <exception cref="NotSupportedException">The format needs a package that is not registered; the message names it.</exception>
    public IReadOnlyList<ConversionWarning> Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        DocumentFormatKind kind = KindOfExtension(Path.GetExtension(path));
        if (kind == DocumentFormatKind.Unknown)
        {
            throw new ArgumentException($"No format writes the extension \"{Path.GetExtension(path)}\".", nameof(path));
        }

        return DocumentFormatRegistry.WriterFor(kind)(this, path);
    }

    /// <summary>
    /// Tells which format a file is in without loading it, from its first bytes and, for text, its extension: a ZIP package
    /// whose first entry is an EPUB <c>mimetype</c> is EPUB, another ZIP package or an old compound file is Word's, a MIME
    /// message of <c>multipart/related</c> parts is MHTML, <c>%PDF-</c> is PDF, a JSON object naming the <c>DoclingDocument</c> schema is
    /// Docling JSON, text that starts with a tag is HTML, and other text is Markdown, or plain text when the extension says
    /// so. It needs no add-on package, so a program can tell its user which package a file needs.
    /// </summary>
    /// <param name="stream">The file. A stream that can seek is put back where it was; one that cannot has its first bytes read.</param>
    /// <param name="fileName">The file's name or extension, or <see langword="null"/> to go by the bytes alone.</param>
    /// <returns>The format, or <see cref="DocumentFormatKind.Unknown"/> for binary data no format here knows.</returns>
    public static DocumentFormatKind Identify(Stream stream, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] head = new byte[HeadLength];
        long start = stream.CanSeek ? stream.Position : 0;
        int length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (stream.CanSeek)
        {
            stream.Position = start;
        }

        string? extension = fileName is null ? null : Path.GetExtension(fileName) is { Length: > 0 } e ? e : fileName;
        return Identify(head.AsSpan(0, length), extension);
    }

    /// <summary>Tells which format the file at <paramref name="path"/> is in without loading it. See <see cref="Identify(Stream, string?)"/>.</summary>
    /// <returns>The format, or <see cref="DocumentFormatKind.Unknown"/> for binary data no format here knows.</returns>
    public static DocumentFormatKind Identify(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Identify(stream, path);
    }

    private static Document Load(byte[] bytes, string? extension, out IReadOnlyList<ConversionWarning> warnings)
    {
        DocumentFormatKind kind = Identify(bytes.AsSpan(0, Math.Min(bytes.Length, HeadLength)), extension);
        if (kind == DocumentFormatKind.Unknown)
        {
            throw new DocumentException("The file is not one Egap can read.");
        }

        return DocumentFormatRegistry.ReaderFor(kind)(bytes, out warnings);
    }

    private static DocumentFormatKind Identify(ReadOnlySpan<byte> head, string? extension)
    {
        // An EPUB's first entry is its stored mimetype (EPUB 3.3 OCF 4.3): the name at offset 30, the type right after it.
        if (head is [0x50, 0x4B, 0x03, 0x04, ..] && head.Length >= 58 && head[30..38].SequenceEqual("mimetype"u8)
            && head[38..58].SequenceEqual("application/epub+zip"u8))
        {
            return DocumentFormatKind.Epub;
        }

        // A ZIP package, or an old compound file (whose error names it), is Word's.
        if (head is [0x50, 0x4B, 0x03, 0x04, ..] or [0xD0, 0xCF, 0x11, 0xE0, ..])
        {
            return DocumentFormatKind.Docx;
        }

        if (head[..Math.Min(head.Length, 1024)].IndexOf("%PDF-"u8) >= 0)
        {
            return DocumentFormatKind.Pdf;
        }

        DocumentFormatKind named = KindOfExtension(extension);
        if (named is DocumentFormatKind.Html or DocumentFormatKind.Markdown or DocumentFormatKind.Text or DocumentFormatKind.Mhtml)
        {
            return named;
        }

        if (IsMultipartRelated(head))
        {
            return DocumentFormatKind.Mhtml;
        }

        if (IsDoclingJson(head))
        {
            return DocumentFormatKind.DoclingJson;
        }

        if (StartsWithMarkup(head))
        {
            return DocumentFormatKind.Html;
        }

        // Binary data: a NUL byte, which UTF-8 text does not hold.
        return head.Contains((byte)0) ? DocumentFormatKind.Unknown : DocumentFormatKind.Markdown;
    }

    private static DocumentFormatKind KindOfExtension(string? extension) => extension?.ToUpperInvariant() switch
    {
        ".DOCX" or ".DOTX" or ".DOCM" or ".DOTM" => DocumentFormatKind.Docx,
        ".HTML" or ".HTM" or ".XHTML" => DocumentFormatKind.Html,
        ".MD" or ".MARKDOWN" or ".MDOWN" or ".MKD" => DocumentFormatKind.Markdown,
        ".TXT" or ".TEXT" => DocumentFormatKind.Text,
        ".PDF" => DocumentFormatKind.Pdf,
        ".JSON" => DocumentFormatKind.DoclingJson,
        ".EPUB" => DocumentFormatKind.Epub,
        ".MHT" or ".MHTML" => DocumentFormatKind.Mhtml,
        _ => DocumentFormatKind.Unknown,
    };

    /// <summary>Whether the text is a JSON object whose <c>schema_name</c> is Docling's, as Docling writes it.</summary>
    private static bool IsDoclingJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            bytes = bytes[3..];
        }

        if (bytes.TrimStart(" \t\r\n"u8) is not [(byte)'{', ..])
        {
            return false;
        }

        int name = bytes.IndexOf("\"schema_name\""u8);
        return name >= 0 && bytes[(name + "\"schema_name\"".Length)..].TrimStart(" \t\r\n:"u8).StartsWith("\"DoclingDocument\""u8);
    }

    /// <summary>
    /// Whether the bytes start with MIME headers (RFC 5322 header lines up to the first empty line) whose Content-Type is
    /// multipart/related, as Chromium's and Word's web archives do.
    /// </summary>
    private static bool IsMultipartRelated(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf("\r\n\r\n"u8) is >= 0 and var crlf ? crlf : bytes.IndexOf("\n\n"u8);
        ReadOnlySpan<byte> headers = end >= 0 ? bytes[..end] : bytes;
        if (headers.IsEmpty || !char.IsAsciiLetter((char)headers[0]))
        {
            return false;
        }

        // Every line is a header (a name and a colon) or a folded continuation.
        foreach (Range range in headers.Split((byte)'\n'))
        {
            ReadOnlySpan<byte> line = headers[range].TrimEnd((byte)'\r');
            if (line.IsEmpty || line[0] is (byte)' ' or (byte)'\t')
            {
                continue;
            }

            int colon = line.IndexOf((byte)':');
            if (colon <= 0 || line[..colon].ContainsAny(" \t"u8))
            {
                return false;
            }
        }

        int type = IndexOfIgnoreCase(headers, "content-type:"u8);
        return type >= 0 && IndexOfIgnoreCase(headers[type..], "multipart/related"u8) >= 0;
    }

    private static int IndexOfIgnoreCase(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> lower)
    {
        for (int i = 0; i + lower.Length <= bytes.Length; i++)
        {
            int j = 0;
            while (j < lower.Length && (bytes[i + j] | 0x20) == lower[j])
            {
                j++;
            }

            if (j == lower.Length)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether the text starts, past a byte order mark and white space, with a tag, a doctype or a comment.</summary>
    private static bool StartsWithMarkup(ReadOnlySpan<byte> bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            bytes = bytes[3..];
        }
        else if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
        {
            // UTF-16 text: only HTML is read in it.
            return true;
        }

        int i = 0;
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i + 1 < bytes.Length && bytes[i] == (byte)'<' && (bytes[i + 1] is (byte)'!' or (byte)'?' || char.IsAsciiLetter((char)bytes[i + 1]));
    }
}
