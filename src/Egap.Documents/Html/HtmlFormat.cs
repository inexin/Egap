using Egap.Documents.Html;

namespace Egap.Documents;

/// <summary>How HTML is read (Phase 6 design Q14 to Q16).</summary>
public sealed class HtmlLoadOptions
{
    /// <summary>The address relative URLs (images, style sheets) are resolved against; none by default.</summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Fetches what the page links to (images, style sheets) by absolute URL, or returns null to leave it out. The reader
    /// fetches nothing by itself and never runs scripts (design Q16); data: URLs need no resolver.
    /// </summary>
    public Func<Uri, byte[]?>? ResourceResolver { get; init; }

    /// <summary>The encoding of HTML given as bytes when it does not say; sniffed by the HTML rules when null.</summary>
    public string? Encoding { get; init; }

    /// <summary>The media the style sheets are applied for: "print" (the default) or "screen".</summary>
    public string Media { get; init; } = "print";

    /// <summary>Style sheets applied as the user's, after the user agent's and before the page's own.</summary>
    public IReadOnlyList<string> UserStyleSheets { get; init; } = [];

    /// <summary>
    /// The limits kept on untrusted pages: the largest page (<see cref="DocumentLimits.MaxPartSize"/>) and how deep elements
    /// are read as structure (<see cref="DocumentLimits.MaxDepth"/>; deeper content is kept as plain text).
    /// </summary>
    public DocumentLimits Limits { get; init; } = new();
}

/// <summary>How the model is written as HTML (Phase 6 design Q16).</summary>
public sealed class HtmlSaveOptions
{
    /// <summary>
    /// Stores a picture and returns the URL the page uses for it (a file beside the page, say); null leaves it to
    /// <see cref="EmbedImages"/>.
    /// </summary>
    public Func<Picture, string?>? ImageWriter { get; init; }

    /// <summary>Whether pictures no writer stored are embedded as data URLs; on by default.</summary>
    public bool EmbedImages { get; init; } = true;

    /// <summary>Whether the page size, margins and simple headers and footers are written as @page rules; on by default.</summary>
    public bool WritePageSetup { get; init; } = true;
}

/// <summary>
/// HTML read into the model (Phase 6 Build Plan tasks 24 and 25): parsed as browsers parse it, styled through Egami.Css's
/// cascade with HTML's default style sheet, and the computed styles mapped onto paragraphs, runs, lists and tables. Flexbox
/// and grid are read as stacked blocks, with a warning (design Q15).
/// </summary>
public static class HtmlFormat
{
    /// <summary>Reads HTML from a string.</summary>
    public static Document Parse(string html, HtmlLoadOptions? options = null)
    {
        return Parse(html, out _, options);
    }

    /// <summary>Reads HTML from a string, reporting what could only be approximated or was left out.</summary>
    public static Document Parse(string html, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new HtmlLoadOptions();
        if ((long)html.Length * 2 > options.Limits.MaxPartSize)
        {
            throw new DocumentLimitException($"The page is larger than the limit of {options.Limits.MaxPartSize} bytes.");
        }

        HtmlDocument dom = HtmlTreeBuilder.Parse(html);
        return HtmlReader.Read(dom, options, out warnings);
    }

    /// <summary>Reads an HTML file; its encoding is sniffed unless the options name one.</summary>
    public static Document Load(string path, HtmlLoadOptions? options = null)
    {
        return Load(path, out _, options);
    }

    /// <summary>Reads an HTML file, reporting what could only be approximated or was left out.</summary>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        Document document = Load(File.ReadAllBytes(path), out warnings, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads HTML from a stream; its encoding is sniffed unless the options name one.</summary>
    public static Document Load(Stream stream, HtmlLoadOptions? options = null)
    {
        return Load(stream, out _, options);
    }

    /// <summary>Reads HTML from a stream, reporting what could only be approximated or was left out.</summary>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return Load(bytes.ToArray(), out warnings, options);
    }

    /// <summary>Writes the document as an HTML5 page with one style sheet, reporting what HTML could not hold.</summary>
    public static string ToHtml(Document document, out IReadOnlyList<ConversionWarning> warnings, HtmlSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return HtmlWriter.Write(document, options ?? new HtmlSaveOptions(), out warnings);
    }

    /// <summary>Writes the document as an HTML5 page.</summary>
    public static string ToHtml(Document document, HtmlSaveOptions? options = null)
    {
        return ToHtml(document, out _, options);
    }

    /// <summary>Saves the document as an HTML file (UTF-8), returning what HTML could not hold.</summary>
    public static IReadOnlyList<ConversionWarning> Save(Document document, string path, HtmlSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, ToHtml(document, out IReadOnlyList<ConversionWarning> warnings, options), new System.Text.UTF8Encoding(false));
        return warnings;
    }

    /// <summary>Writes the document as HTML (UTF-8) to a stream, returning what HTML could not hold.</summary>
    public static IReadOnlyList<ConversionWarning> Save(Document document, Stream stream, HtmlSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = new System.Text.UTF8Encoding(false).GetBytes(ToHtml(document, out IReadOnlyList<ConversionWarning> warnings, options));
        stream.Write(bytes);
        return warnings;
    }

    /// <summary>Reads HTML bytes, reporting what could only be approximated or was left out.</summary>
    public static Document Load(ReadOnlySpan<byte> html, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null)
    {
        options ??= new HtmlLoadOptions();
        if (html.Length > options.Limits.MaxPartSize)
        {
            throw new DocumentLimitException($"The page is larger than the limit of {options.Limits.MaxPartSize} bytes.");
        }

        HtmlDocument dom = HtmlTreeBuilder.Parse(html, out _, options.Encoding);
        return HtmlReader.Read(dom, options, out warnings);
    }
}
