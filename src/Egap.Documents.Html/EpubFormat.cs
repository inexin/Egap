using Egap.Documents.Html;

namespace Egap.Documents;

/// <summary>How an EPUB book is read.</summary>
public sealed class EpubLoadOptions
{
    /// <summary>
    /// Fetches what a chapter links to outside the book (remote images, style sheets and fonts EPUB 3 allows) by absolute URL,
    /// or returns null to leave it out. Egap fetches nothing by itself and never runs a book's scripts.
    /// </summary>
    public Func<Uri, byte[]?>? ResourceResolver { get; init; }

    /// <summary>The media the book's style sheets are applied for: "screen" (the default, as reading systems apply them) or "print".</summary>
    public string Media { get; init; } = "screen";

    /// <summary>Style sheets applied as the user's, after the user agent's and before the book's own.</summary>
    public IReadOnlyList<string> UserStyleSheets { get; init; } = [];

    /// <summary>
    /// The limits kept on untrusted books: entries, the largest entry, all entries together, the compression ratio, and
    /// how deep elements nest.
    /// </summary>
    public DocumentLimits Limits { get; init; } = new();
}

/// <summary>Where the EPUB writer starts a new chapter file.</summary>
[Flags]
public enum EpubSplit
{
    /// <summary>The whole document in one chapter file.</summary>
    None = 0,

    /// <summary>At each section break.</summary>
    SectionBreak = 1,

    /// <summary>At each paragraph of outline level 1 (Heading 1).</summary>
    HeadingLevel1 = 2,
}

/// <summary>How the model is written as an EPUB 3.3 book.</summary>
public sealed class EpubSaveOptions
{
    /// <summary>
    /// Where chapter files start; at section breaks and level 1 headings by default. A book read from EPUB keeps its own
    /// chapter files, whatever this says, as long as its sections are still the chapters it read.
    /// </summary>
    public EpubSplit SplitAt { get; init; } = EpubSplit.SectionBreak | EpubSplit.HeadingLevel1;

    /// <summary>How many outline levels the table of contents shows (1 to 9; 3 by default).</summary>
    public int NavigationDepth { get; init; } = 3;

    /// <summary>
    /// Whether the document's embedded fonts go into the book; off by default, as embedding is a licence question only the
    /// caller can answer. The fonts are subset to the characters the book uses and obfuscated as EPUB says.
    /// </summary>
    public bool EmbedFonts { get; init; }

    /// <summary>The cover picture: an image to put on a page of its own before the first chapter; none by default.</summary>
    public ImageData? CoverImage { get; init; }

    /// <summary>
    /// The book's unique identifier; by default the one it was read with, else a urn:uuid made from the title and author,
    /// so saving the same book twice gives the same identifier.
    /// </summary>
    public string? Identifier { get; init; }

    /// <summary>
    /// The accessibility summary written to the package (schema:accessibilitySummary, EPUB Accessibility 1.1); by default a
    /// sentence saying what the book has.
    /// </summary>
    public string? AccessibilitySummary { get; init; }

    /// <summary>When the book was last changed (dcterms:modified); the document's saved time, else now, by default.</summary>
    public DateTimeOffset? Modified { get; init; }
}

/// <summary>
/// EPUB books read into the model and written from it: EPUB 2.0.1 and 3.0 to 3.3 read as one document with a section per
/// chapter, the table of contents as outline levels and the metadata as document properties; the model written as an
/// EPUB 3.3 book with a navigation document, an EPUB 2 table of contents for older readers, and accessibility metadata.
/// Books under DRM are refused.
/// </summary>
// CA1708 misreads two extension blocks with different receivers as names differing only by case.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case", Justification = "C# 14 extension blocks")]
public static class EpubFormat
{
    /// <summary>Reads an EPUB file.</summary>
    /// <exception cref="DocumentException">The file is not an EPUB, or it is under DRM.</exception>
    /// <exception cref="DocumentLimitException">The book is past a limit of <see cref="EpubLoadOptions.Limits"/>.</exception>
    public static Document Load(string path, EpubLoadOptions? options = null)
    {
        return Load(path, out _, options);
    }

    /// <summary>Reads an EPUB file, reporting what could only be approximated or was left out.</summary>
    /// <exception cref="DocumentException">The file is not an EPUB, or it is under DRM.</exception>
    /// <exception cref="DocumentLimitException">The book is past a limit of <see cref="EpubLoadOptions.Limits"/>.</exception>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings, EpubLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        Document document = Load(stream, out warnings, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads an EPUB book from a stream, which is left open.</summary>
    /// <exception cref="DocumentException">The stream is not an EPUB, or it is under DRM.</exception>
    /// <exception cref="DocumentLimitException">The book is past a limit of <see cref="EpubLoadOptions.Limits"/>.</exception>
    public static Document Load(Stream stream, EpubLoadOptions? options = null)
    {
        return Load(stream, out _, options);
    }

    /// <summary>Reads an EPUB book from a stream, which is left open, reporting what could only be approximated or was left out.</summary>
    /// <exception cref="DocumentException">The stream is not an EPUB, or it is under DRM.</exception>
    /// <exception cref="DocumentLimitException">The book is past a limit of <see cref="EpubLoadOptions.Limits"/>.</exception>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings, EpubLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            copy.Position = 0;
            return EpubReader.Read(copy, options ?? new EpubLoadOptions(), out warnings);
        }

        return EpubReader.Read(stream, options ?? new EpubLoadOptions(), out warnings);
    }

    /// <summary>Saves the document as an EPUB 3.3 file, returning what EPUB could not hold.</summary>
    public static IReadOnlyList<ConversionWarning> Save(Document document, string path, EpubSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.Create(path);
        return Save(document, stream, options);
    }

    /// <summary>Writes the document as an EPUB 3.3 book to a stream, which is left open, returning what EPUB could not hold.</summary>
    public static IReadOnlyList<ConversionWarning> Save(Document document, Stream stream, EpubSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(stream);
        return EpubWriter.Write(document, stream, options ?? new EpubSaveOptions());
    }

    extension(Document document)
    {
        /// <summary>Reads an EPUB file, as <see cref="Load(string, EpubLoadOptions?)"/> does.</summary>
        public static Document LoadEpub(string path, EpubLoadOptions? options = null) => Load(path, options);

        /// <summary>
        /// Reads an EPUB file, reporting what could only be approximated or was left out, as
        /// <see cref="Load(string, out IReadOnlyList{ConversionWarning}, EpubLoadOptions?)"/> does.
        /// </summary>
        public static Document LoadEpub(string path, out IReadOnlyList<ConversionWarning> warnings, EpubLoadOptions? options = null) =>
            Load(path, out warnings, options);

        /// <summary>Reads an EPUB book from a stream, as <see cref="Load(Stream, EpubLoadOptions?)"/> does.</summary>
        public static Document LoadEpub(Stream stream, EpubLoadOptions? options = null) => Load(stream, options);

        /// <summary>The document as the bytes of an EPUB 3.3 book. Also callable as <c>EpubFormat.ToEpub(document, options)</c>.</summary>
        public byte[] ToEpub(EpubSaveOptions? options = null)
        {
            using MemoryStream stream = new();
            Save(document, stream, options);
            return stream.ToArray();
        }

        /// <summary>Saves the document as an EPUB file, as <see cref="Save(Document, string, EpubSaveOptions?)"/> does.</summary>
        /// <returns>What EPUB could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsEpub(string path, EpubSaveOptions? options = null) => Save(document, path, options);

        /// <summary>Writes the document as an EPUB book to a stream, as <see cref="Save(Document, Stream, EpubSaveOptions?)"/> does.</summary>
        /// <returns>What EPUB could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsEpub(Stream stream, EpubSaveOptions? options = null) => Save(document, stream, options);
    }
}
