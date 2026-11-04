using Egap.Documents.Odf;

namespace Egap.Documents;

/// <summary>Options for reading OpenDocument Text.</summary>
public sealed class OdtLoadOptions
{
    /// <summary>Limits that stop a hostile file from using too much memory or time.</summary>
    public DocumentLimits Limits { get; init; } = new();
}

/// <summary>
/// OpenDocument Text (ODF 1.2 to 1.4) read into the model: ODT documents, OTT templates and flat FODT files, with their
/// styles, lists, tables, frames, sections, page styles, headers and footers, notes, comments, fields, bookmarks, links and
/// tracked changes. Charts, formulas and other embedded objects are kept as islands drawn as their stored picture.
/// </summary>
public static class OdtFormat
{
    /// <summary>Reads an ODT, OTT or FODT file.</summary>
    /// <exception cref="DocumentException">The file is not OpenDocument Text, or is malformed or password-protected.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of the limits.</exception>
    public static Document Load(string path, OdtLoadOptions? options = null)
    {
        return Load(path, out _, options);
    }

    /// <summary>Reads an ODT, OTT or FODT file, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The file is not OpenDocument Text, or is malformed or password-protected.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of the limits.</exception>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings, OdtLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Document document = Read(File.ReadAllBytes(path), out warnings, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads ODT, OTT or FODT from a stream, which is left open.</summary>
    /// <exception cref="DocumentException">The stream is not OpenDocument Text, or is malformed or password-protected.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of the limits.</exception>
    public static Document Load(Stream stream, OdtLoadOptions? options = null)
    {
        return Load(stream, out _, options);
    }

    /// <summary>Reads ODT, OTT or FODT from a stream, which is left open, reporting what could not be read as it was.</summary>
    /// <exception cref="DocumentException">The stream is not OpenDocument Text, or is malformed or password-protected.</exception>
    /// <exception cref="DocumentLimitException">The file is past one of the limits.</exception>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings, OdtLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return Read(copy.ToArray(), out warnings, options);
    }

    internal static Document Read(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings, OdtLoadOptions? options = null)
    {
        return OdtReader.Read(bytes, options ?? new OdtLoadOptions(), out warnings);
    }
}
