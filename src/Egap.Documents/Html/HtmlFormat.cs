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
        HtmlDocument dom = HtmlTreeBuilder.Parse(html);
        return HtmlReader.Read(dom, options ?? new HtmlLoadOptions(), out warnings);
    }

    /// <summary>Reads an HTML file; its encoding is sniffed unless the options name one.</summary>
    public static Document Load(string path, HtmlLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Load(File.ReadAllBytes(path), out _, options);
    }

    /// <summary>Reads HTML from a stream; its encoding is sniffed unless the options name one.</summary>
    public static Document Load(Stream stream, HtmlLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return Load(bytes.ToArray(), out _, options);
    }

    /// <summary>Reads HTML bytes, reporting what could only be approximated or was left out.</summary>
    public static Document Load(ReadOnlySpan<byte> html, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null)
    {
        options ??= new HtmlLoadOptions();
        HtmlDocument dom = HtmlTreeBuilder.Parse(html, out _, options.Encoding);
        return HtmlReader.Read(dom, options, out warnings);
    }
}
