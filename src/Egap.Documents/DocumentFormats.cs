namespace Egap.Documents;

/// <summary>Loading and saving without naming the format (API review point 2, as Aspose.Words and GemBox.Document do).</summary>
public sealed partial class Document
{
    /// <summary>
    /// Reads a DOCX, DOTX, DOCM, HTML or Markdown file, telling them apart by their bytes (a ZIP package is Word's) and,
    /// for text, by the file's extension, else by whether it starts with markup.
    /// </summary>
    /// <exception cref="DocumentException">The file is not one Egap can read.</exception>
    public static Document Load(string path)
    {
        return Load(path, out _);
    }

    /// <summary>Reads a DOCX, DOTX, DOCM, HTML or Markdown file, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The file is not one Egap can read.</exception>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] bytes = File.ReadAllBytes(path);
        Document document = Load(bytes, Path.GetExtension(path), out warnings);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads a DOCX, DOTX, DOCM, HTML or Markdown file from a stream, which is left open.</summary>
    /// <exception cref="DocumentException">The stream is not a file Egap can read.</exception>
    public static Document Load(Stream stream)
    {
        return Load(stream, out _);
    }

    /// <summary>Reads a DOCX, DOTX, DOCM, HTML or Markdown file from a stream, which is left open, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The stream is not a file Egap can read.</exception>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Load(copy.ToArray(), null, out warnings);
    }

    /// <summary>
    /// Saves the document in the format its file extension names: .docx, .dotx, .docm and .dotm as Word's, .html and .htm
    /// as HTML, .md and .markdown as Markdown. PDF is <c>SaveAsPdf</c> in Egap.Pdf.Layout.
    /// </summary>
    /// <returns>What the format could not hold.</returns>
    /// <exception cref="ArgumentException">The extension names no format Egap.Documents writes.</exception>
    public IReadOnlyList<ConversionWarning> Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".DOCX" or ".DOTX" or ".DOCM" or ".DOTM" => DocxFormat.Save(this, path),
            ".HTML" or ".HTM" or ".XHTML" => HtmlFormat.Save(this, path),
            ".MD" or ".MARKDOWN" or ".MDOWN" or ".MKD" => MarkdownFormat.Save(this, path),
            ".PDF" => throw new ArgumentException("PDF is written by SaveAsPdf in Egap.Pdf.Layout.", nameof(path)),
            string other => throw new ArgumentException($"No format writes the extension \"{other}\".", nameof(path)),
        };
    }

    private static Document Load(byte[] bytes, string? extension, out IReadOnlyList<ConversionWarning> warnings)
    {
        // A ZIP package, or an old compound file (whose error names it), is Word's.
        if (bytes is [0x50, 0x4B, 0x03, 0x04, ..] or [0xD0, 0xCF, 0x11, 0xE0, ..])
        {
            using MemoryStream stream = new(bytes, writable: false);
            return DocxFormat.Load(stream, out warnings);
        }

        bool html = extension?.ToUpperInvariant() switch
        {
            ".HTML" or ".HTM" or ".XHTML" => true,
            ".MD" or ".MARKDOWN" or ".MDOWN" or ".MKD" or ".TXT" => false,
            _ => StartsWithMarkup(bytes),
        };
        if (html)
        {
            return HtmlFormat.Load(bytes, out warnings);
        }

        using MemoryStream text = new(bytes, writable: false);
        return MarkdownFormat.Load(text, out warnings);
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
