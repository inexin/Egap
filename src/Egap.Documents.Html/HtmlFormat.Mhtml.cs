using Egap.Documents.Html;

namespace Egap.Documents;

public static partial class HtmlFormat
{
    /// <summary>
    /// Reads an MHTML web archive (.mht, .mhtml), as Chromium, Edge and Word save pages: the page through the HTML reader,
    /// its pictures, style sheets and fonts from the archive's other parts. What the archive does not hold is fetched only
    /// through <see cref="HtmlLoadOptions.ResourceResolver"/>.
    /// </summary>
    /// <exception cref="DocumentException">The file is not a web archive, or holds no HTML page.</exception>
    public static Document LoadMhtml(string path, HtmlLoadOptions? options = null)
    {
        return LoadMhtml(path, out _, options);
    }

    /// <summary>Reads an MHTML web archive, reporting what could only be approximated or was left out.</summary>
    /// <exception cref="DocumentException">The file is not a web archive, or holds no HTML page.</exception>
    public static Document LoadMhtml(string path, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        Document document = LoadMhtml(File.ReadAllBytes(path), out warnings, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads an MHTML web archive from a stream.</summary>
    /// <exception cref="DocumentException">The stream is not a web archive, or holds no HTML page.</exception>
    public static Document LoadMhtml(Stream stream, HtmlLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return LoadMhtml(bytes.ToArray(), out _, options);
    }

    /// <summary>Reads the bytes of an MHTML web archive, reporting what could only be approximated or was left out.</summary>
    /// <exception cref="DocumentException">The bytes are not a web archive, or hold no HTML page.</exception>
    public static Document LoadMhtml(ReadOnlySpan<byte> mhtml, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null)
    {
        return MhtmlReader.Read(mhtml, options ?? new HtmlLoadOptions(), out warnings);
    }

    /// <summary>
    /// Saves the document as an MHTML web archive in the shape Chromium writes, which Chromium, Edge and Word open: the page
    /// and its pictures and embedded fonts in one file. Returns what HTML could not hold.
    /// </summary>
    public static IReadOnlyList<ConversionWarning> SaveMhtml(Document document, string path, HtmlSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.Create(path);
        return SaveMhtml(document, stream, options);
    }

    /// <summary>Writes the document as an MHTML web archive to a stream, returning what HTML could not hold.</summary>
    public static IReadOnlyList<ConversionWarning> SaveMhtml(Document document, Stream stream, HtmlSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(MhtmlWriter.Write(document, options ?? new HtmlSaveOptions(), out IReadOnlyList<ConversionWarning> warnings));
        return warnings;
    }

    extension(Document document)
    {
        /// <summary>
        /// The document as an MHTML web archive, reporting what HTML could not hold. Also callable as
        /// <c>HtmlFormat.ToMhtml(document, out warnings, options)</c>.
        /// </summary>
        public string ToMhtml(out IReadOnlyList<ConversionWarning> warnings, HtmlSaveOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(document);
            return System.Text.Encoding.ASCII.GetString(MhtmlWriter.Write(document, options ?? new HtmlSaveOptions(), out warnings));
        }

        /// <summary>The document as an MHTML web archive. Also callable as <c>HtmlFormat.ToMhtml(document, options)</c>.</summary>
        public string ToMhtml(HtmlSaveOptions? options = null) => document.ToMhtml(out _, options);

        /// <summary>Saves the document as an MHTML web archive, as <see cref="SaveMhtml(Document, string, HtmlSaveOptions?)"/> does.</summary>
        /// <returns>What HTML could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsMhtml(string path, HtmlSaveOptions? options = null) => SaveMhtml(document, path, options);

        /// <summary>Writes the document as an MHTML web archive to a stream, as <see cref="SaveMhtml(Document, Stream, HtmlSaveOptions?)"/> does.</summary>
        /// <returns>What HTML could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsMhtml(Stream stream, HtmlSaveOptions? options = null) => SaveMhtml(document, stream, options);
    }
}
