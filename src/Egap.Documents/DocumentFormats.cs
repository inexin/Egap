using Egap.Documents.Pdf;

namespace Egap.Documents;

/// <summary>Loading and saving without naming the format, and telling formats apart.</summary>
public sealed partial class Document
{
    // Enough for a PDF header after leading bytes (ISO 32000-2 7.5.2 allows them in the first 1024) and a Docling schema name.
    private const int HeadLength = 4096;

    /// <summary>
    /// Reads a file in any format Egap reads, telling them apart by their bytes and, for text, by the file's extension (see
    /// <see cref="Identify(Stream, string?)"/>). DOCX, RTF, PDF, plain text and Docling JSON are built in; HTML, MHTML, EPUB and
    /// Markdown need their add-on package and its <c>Register()</c> call, such as <c>MarkdownFormat.Register()</c>. An
    /// encrypted Word file needs its password: see <see cref="Load(string, DocumentLoadOptions?)"/>.
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
        return Load(path, out warnings, null);
    }

    /// <summary>
    /// Reads a file in any format Egap reads, with a password for an encrypted file and limits for untrusted ones. See
    /// <see cref="Load(string)"/>.
    /// </summary>
    /// <exception cref="DocumentException">The file is not one Egap can read.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and no password, or no right one, was given.</exception>
    /// <exception cref="NotSupportedException">The file's format needs a package that is not registered; the message names it.</exception>
    public static Document Load(string path, DocumentLoadOptions? options)
    {
        return Load(path, out _, options);
    }

    /// <summary>
    /// Reads a file in any format Egap reads, with a password for an encrypted file and limits for untrusted ones, reporting
    /// what could not be read as it was. See <see cref="Load(string)"/>.
    /// </summary>
    /// <exception cref="DocumentException">The file is not one Egap can read.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and no password, or no right one, was given.</exception>
    /// <exception cref="NotSupportedException">The file's format needs a package that is not registered; the message names it.</exception>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings, DocumentLoadOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes = File.ReadAllBytes(path);
        Document document = Load(bytes, Path.GetExtension(path), out warnings, options);
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
        return Load(stream, out warnings, null);
    }

    /// <summary>
    /// Reads a file in any format Egap reads from a stream, which is left open, with a password for an encrypted file and
    /// limits for untrusted ones. See <see cref="Load(string)"/>.
    /// </summary>
    /// <exception cref="DocumentException">The stream is not a file Egap can read.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and no password, or no right one, was given.</exception>
    /// <exception cref="NotSupportedException">The file's format needs a package that is not registered; the message names it.</exception>
    public static Document Load(Stream stream, DocumentLoadOptions? options)
    {
        return Load(stream, out _, options);
    }

    /// <summary>
    /// Reads a file in any format Egap reads from a stream, which is left open, with a password for an encrypted file and
    /// limits for untrusted ones, reporting what could not be read as it was. See <see cref="Load(string)"/>.
    /// </summary>
    /// <exception cref="DocumentException">The stream is not a file Egap can read.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and no password, or no right one, was given.</exception>
    /// <exception cref="NotSupportedException">The file's format needs a package that is not registered; the message names it.</exception>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings, DocumentLoadOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Load(copy.ToArray(), null, out warnings, options);
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
    /// whose first entry is an EPUB <c>mimetype</c> is EPUB, one with a part under <c>xl/</c> or named .xlsx, .xlsm, .xltx or
    /// .xltm is an Excel workbook, another ZIP package or an old compound file is Word's (and a
    /// compound file that holds an encrypted package is <see cref="DocumentFormatKind.Encrypted"/>), an OpenDocument text
    /// package is ODT (and <see cref="DocumentFormatKind.Encrypted"/> when its manifest says it is encrypted), a MIME
    /// message of <c>multipart/related</c> parts is MHTML, <c>%PDF-</c> is PDF, <c>{\rtf</c> is RTF, a JSON object naming the <c>DoclingDocument</c> schema is
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
        DocumentFormatKind kind = Identify(head.AsSpan(0, length), extension);

        // A compound file is encrypted when its directory names the encrypted package; a stream that cannot seek is judged
        // by the directory sectors among its first bytes.
        if (kind == DocumentFormatKind.Docx && Office.CompoundFile.IsCompoundFile(head.AsSpan(0, length))
            && Office.OfficeEncryption.IsEncryptedPackage(stream.CanSeek ? stream : new MemoryStream(head, 0, length, writable: false)))
        {
            return DocumentFormatKind.Encrypted;
        }

        // An encrypted OpenDocument package says so in its manifest, which a stream that cannot seek may hold too late to read.
        if (kind == DocumentFormatKind.Odt && stream.CanSeek && head is [0x50, 0x4B, 0x03, 0x04, ..] && Odf.OdfPackage.IsEncryptedZip(stream))
        {
            stream.Position = start;
            return DocumentFormatKind.Encrypted;
        }

        if (stream.CanSeek)
        {
            stream.Position = start;
        }

        return kind;
    }

    /// <summary>Tells which format the file at <paramref name="path"/> is in without loading it. See <see cref="Identify(Stream, string?)"/>.</summary>
    /// <returns>The format, or <see cref="DocumentFormatKind.Unknown"/> for binary data no format here knows.</returns>
    public static DocumentFormatKind Identify(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Identify(stream, path);
    }

    private static Document Load(byte[] bytes, string? extension, out IReadOnlyList<ConversionWarning> warnings, DocumentLoadOptions? options)
    {
        DocumentFormatKind kind = Identify(bytes.AsSpan(0, Math.Min(bytes.Length, HeadLength)), extension);
        if (kind == DocumentFormatKind.Unknown)
        {
            throw new DocumentException("The file is not one Egap can read.");
        }

        // DOCX, encrypted packages included, and RTF take the options' password and limits.
        if (kind == DocumentFormatKind.Docx && options is not null)
        {
            using MemoryStream stream = new(bytes, writable: false);
            return DocxFormat.Load(stream, out warnings, new DocxLoadOptions
            {
                Limits = options.Limits,
                Password = options.Password,
                PasswordCallback = options.PasswordCallback,
            });
        }

        if (kind == DocumentFormatKind.Rtf && options is not null)
        {
            return RtfFormat.Read(bytes, out warnings, new RtfLoadOptions { Limits = options.Limits });
        }

        if (kind == DocumentFormatKind.Odt && options is not null)
        {
            return OdtFormat.Read(bytes, out warnings, new OdtLoadOptions
            {
                Limits = options.Limits,
                Password = options.Password,
                PasswordCallback = options.PasswordCallback,
            });
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

        // An OpenDocument package's first entry is its stored mimetype too (ODF 1.3 Part 2, 3.3); a ZIP package named .odt,
        // .ott or .fodt is taken as one when a writer put the mimetype elsewhere.
        if (Odf.OdfNames.IsText(Odf.OdfPackage.MediaTypeOfZip(head))
            || (head is [0x50, 0x4B, 0x03, 0x04, ..] && KindOfExtension(extension) == DocumentFormatKind.Odt))
        {
            return DocumentFormatKind.Odt;
        }

        // A ZIP package with a part under xl/, or one named as a workbook, is Excel's.
        if (head is [0x50, 0x4B, 0x03, 0x04, ..] && (HasWorkbookPart(head) || KindOfExtension(extension) == DocumentFormatKind.Xlsx))
        {
            return DocumentFormatKind.Xlsx;
        }

        // Another ZIP package, or an old compound file (whose error names it), is Word's.
        if (head is [0x50, 0x4B, 0x03, 0x04, ..] or [0xD0, 0xCF, 0x11, 0xE0, ..])
        {
            return DocumentFormatKind.Docx;
        }

        if (head[..Math.Min(head.Length, 1024)].IndexOf("%PDF-"u8) >= 0)
        {
            return DocumentFormatKind.Pdf;
        }

        if (IsRtf(head))
        {
            return DocumentFormatKind.Rtf;
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

        if (Odf.OdfPackage.IsFlatText(head))
        {
            return DocumentFormatKind.Odt;
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
        ".RTF" => DocumentFormatKind.Rtf,
        ".ODT" or ".OTT" or ".FODT" => DocumentFormatKind.Odt,
        ".XLSX" or ".XLSM" or ".XLTX" or ".XLTM" => DocumentFormatKind.Xlsx,
        _ => DocumentFormatKind.Unknown,
    };

    /// <summary>
    /// Whether a ZIP file's local headers among its first bytes name a part under <c>xl/</c>, as every SpreadsheetML package
    /// has (ECMA-376 Part 1, 12.3). The walk stops at an entry whose size its header does not give.
    /// </summary>
    private static bool HasWorkbookPart(ReadOnlySpan<byte> head)
    {
        int at = 0;
        while (at + 30 <= head.Length && head[at..] is [0x50, 0x4B, 0x03, 0x04, ..])
        {
            int flags = head[at + 6] | (head[at + 7] << 8);
            long size = (uint)(head[at + 18] | (head[at + 19] << 8) | (head[at + 20] << 16) | (head[at + 21] << 24));
            int nameLength = head[at + 26] | (head[at + 27] << 8);
            int extra = head[at + 28] | (head[at + 29] << 8);
            if (at + 30 + nameLength > head.Length)
            {
                return false;
            }

            if (head.Slice(at + 30, nameLength).StartsWith("xl/"u8))
            {
                return true;
            }

            if ((flags & 8) != 0)
            {
                return false;
            }

            long next = at + 30L + nameLength + extra + size;
            if (next >= head.Length)
            {
                return false;
            }

            at = (int)next;
        }

        return false;
    }

    /// <summary>Whether the bytes start with <c>{\rtf</c>, after a byte order mark or white space as Word allows.</summary>
    private static bool IsRtf(ReadOnlySpan<byte> bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            bytes = bytes[3..];
        }

        return bytes.TrimStart(" \t\r\n"u8).StartsWith("{\\rtf"u8);
    }

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
