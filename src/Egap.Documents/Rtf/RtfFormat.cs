using System.Text;

using Egap.Documents.Rtf;

namespace Egap.Documents;

/// <summary>How <see cref="RtfFormat"/> reads a file.</summary>
public sealed class RtfLoadOptions
{
    /// <summary>
    /// The limits on the file: <see cref="DocumentLimits.MaxDepth"/> caps how deep groups nest, and
    /// <see cref="DocumentLimits.MaxPartSize"/> caps each picture and each block of <c>\bin</c> data.
    /// </summary>
    public DocumentLimits Limits { get; init; } = new();
}

/// <summary>How <see cref="RtfFormat"/> writes a file.</summary>
public sealed class RtfSaveOptions
{
    /// <summary>
    /// Whether groups the reader kept as it read them (Word's theme, data store and latent styles, say) are written back
    /// where they sat; true by default. Turn it off for a smaller file that only says what the model holds.
    /// </summary>
    public bool KeepUnknownGroups { get; init; } = true;
}

/// <summary>
/// Rich Text Format, as Word 2007 and later, WordPad, LibreOffice, TextEdit and Outlook write it (RTF 1.9.1): text and its
/// formatting, fonts, colours, styles, lists, tables, sections, headers and footers, notes, fields, bookmarks, comments,
/// tracked changes and pictures read into the model. Text boxes become shapes, math is kept as an island, and other
/// drawings and embedded objects come in as the picture the file saved for them, with a warning. Groups the reader does
/// not know are kept as extension data.
/// </summary>
public static class RtfFormat
{
    /// <summary>Reads the RTF file at <paramref name="path"/>.</summary>
    /// <exception cref="DocumentException">The file is not RTF.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="RtfLoadOptions.Limits"/>.</exception>
    public static Document Load(string path, RtfLoadOptions? options = null)
    {
        return Load(path, out _, options);
    }

    /// <summary>Reads the RTF file at <paramref name="path"/>, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The file is not RTF.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="RtfLoadOptions.Limits"/>.</exception>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings, RtfLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Document document = Read(File.ReadAllBytes(path), out warnings, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads RTF from <paramref name="stream"/>, which is left open.</summary>
    /// <exception cref="DocumentException">The stream is not RTF.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="RtfLoadOptions.Limits"/>.</exception>
    public static Document Load(Stream stream, RtfLoadOptions? options = null)
    {
        return Load(stream, out _, options);
    }

    /// <summary>Reads RTF from <paramref name="stream"/>, which is left open, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The stream is not RTF.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of <see cref="RtfLoadOptions.Limits"/>.</exception>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings, RtfLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Read(copy.ToArray(), out warnings, options);
    }

    /// <summary>Reads RTF text, such as a string from the clipboard or a database field.</summary>
    /// <exception cref="DocumentException">The text is not RTF.</exception>
    /// <exception cref="DocumentLimitException">The text is past one of <see cref="RtfLoadOptions.Limits"/>.</exception>
    public static Document Parse(string rtf, RtfLoadOptions? options = null)
    {
        return Parse(rtf, out _, options);
    }

    /// <summary>Reads RTF text, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The text is not RTF.</exception>
    /// <exception cref="DocumentLimitException">The text is past one of <see cref="RtfLoadOptions.Limits"/>.</exception>
    public static Document Parse(string rtf, out IReadOnlyList<ConversionWarning> warnings, RtfLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rtf);

        // RTF is 7-bit text, so each character stands for a byte; one past U+00FF can only be text typed into the string, and
        // goes in as a \uN escape.
        return Read(ToBytes(rtf), out warnings, options);
    }

    /// <summary>
    /// Writes <paramref name="document"/> as RTF to <paramref name="path"/>. Text outside ASCII is written as Unicode escapes,
    /// pictures as PNG, JPEG, EMF or WMF (other images are converted to PNG), and what the reader kept is written back.
    /// </summary>
    /// <returns>What RTF could not hold, such as margin boxes, content controls and SVG pictures.</returns>
    public static IReadOnlyList<ConversionWarning> Save(Document document, string path, RtfSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.Create(path);
        return Save(document, stream, options);
    }

    /// <summary>Writes <paramref name="document"/> as RTF to <paramref name="stream"/>, which is left open.</summary>
    /// <returns>What RTF could not hold, such as margin boxes, content controls and SVG pictures.</returns>
    public static IReadOnlyList<ConversionWarning> Save(Document document, Stream stream, RtfSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        return RtfWriter.Write(document, stream, options ?? new RtfSaveOptions());
    }

    /// <summary>Reads RTF from bytes, for <see cref="Document.Load(string)"/>.</summary>
    internal static Document Read(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings, RtfLoadOptions? options = null)
    {
        return RtfReader.Read(bytes, options ?? new RtfLoadOptions(), out warnings);
    }

    private static byte[] ToBytes(string text)
    {
        if (!text.AsSpan().ContainsAnyExceptInRange('\0', 'ÿ'))
        {
            return Encoding.Latin1.GetBytes(text);
        }

        // An empty group after the escape ends its fallback, whatever \uc says.
        StringBuilder escaped = new(text.Length + 16);
        foreach (char c in text)
        {
            if (c > 'ÿ')
            {
                escaped.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{(short)c}{{}}");
            }
            else
            {
                escaped.Append(c);
            }
        }

        return Encoding.Latin1.GetBytes(escaped.ToString());
    }
}
