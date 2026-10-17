using System.Text;

using Egap.Documents.Markdown;

namespace Egap.Documents;

/// <summary>The Markdown extensions the reader understands beyond CommonMark (Phase 6 design Q17).</summary>
[Flags]
public enum MarkdownExtensions
{
    /// <summary>CommonMark only.</summary>
    None = 0,

    /// <summary>GitHub's pipe tables.</summary>
    Tables = 1,

    /// <summary>GitHub's task list items, "- [ ]" and "- [x]".</summary>
    TaskLists = 2,

    /// <summary>GitHub's strikethrough, ~text~ and ~~text~~.</summary>
    Strikethrough = 4,

    /// <summary>GitHub's extended autolinks: www., http://, https:// and e-mail addresses without angle brackets.</summary>
    Autolinks = 8,

    /// <summary>GitHub's footnotes, [^label] with [^label]: definitions.</summary>
    Footnotes = 16,

    /// <summary>YAML front matter between --- lines at the very start, kept and mapped to document properties.</summary>
    FrontMatter = 32,

    /// <summary>Everything GitHub Flavored Markdown adds, with front matter.</summary>
    GitHub = Tables | TaskLists | Strikethrough | Autolinks | Footnotes | FrontMatter,
}

/// <summary>How <see cref="MarkdownFormat"/> reads Markdown.</summary>
public sealed class MarkdownLoadOptions
{
    /// <summary>The extensions to read; GitHub's by default.</summary>
    public MarkdownExtensions Extensions { get; init; } = MarkdownExtensions.GitHub;

    /// <summary>
    /// Gives the bytes of an image the Markdown names by URL, or null to leave it unfetched (the default: Egap opens nothing
    /// by itself, Phase 6 design Q16). An unfetched image becomes a picture that links to its URL.
    /// </summary>
    public Func<string, ImageData?>? ImageResolver { get; init; }
}

/// <summary>What the Markdown writer does with pictures that have no address of their own.</summary>
public enum MarkdownImageMode
{
    /// <summary>Each image goes to <see cref="MarkdownSaveOptions.ImageResolver"/>, or, saving to a file, to a folder beside it.</summary>
    Files,

    /// <summary>Each image is written in the Markdown as a data: URI (which GitHub and many viewers do not show).</summary>
    DataUris,

    /// <summary>An HTML comment marks where each image was, for text-only output.</summary>
    Placeholders,

    /// <summary>Images are left out, with a warning.</summary>
    Omit,
}

/// <summary>An image the Markdown writer hands to <see cref="MarkdownSaveOptions.ImageResolver"/>.</summary>
/// <param name="Bytes">The image in a browser-ready format: JPEG, PNG, GIF, WebP or SVG.</param>
/// <param name="ContentType">Its media type.</param>
/// <param name="SuggestedName">A file name in reading order, such as image-001.png.</param>
/// <param name="Picture">The picture it came from.</param>
public sealed record MarkdownImage(ReadOnlyMemory<byte> Bytes, string ContentType, string SuggestedName, Picture Picture);

/// <summary>How <see cref="MarkdownFormat"/> writes Markdown.</summary>
public sealed class MarkdownSaveOptions
{
    /// <summary>
    /// Stores a picture that has no address of its own (an image embedded in a DOCX, say) and returns the address to write
    /// for it, or null to leave it out with a warning.
    /// </summary>
    public Func<Picture, string?>? ImageWriter { get; init; }

    /// <summary>Writes pictures with no address and no <see cref="ImageWriter"/> as data: URIs; the same as <see cref="Images"/> set to <see cref="MarkdownImageMode.DataUris"/>.</summary>
    public bool EmbedImages { get; init; }

    /// <summary>
    /// What happens to pictures without an address of their own (Phase 6 design Q20): by default they go to
    /// <see cref="ImageResolver"/>, or, saving to a file, to a folder beside it.
    /// </summary>
    public MarkdownImageMode Images { get; init; } = MarkdownImageMode.Files;

    /// <summary>
    /// Stores each image (browser-ready bytes, media type and a suggested name in reading order) and returns the address to
    /// write, or null to leave it out with a warning; each distinct image is asked for once.
    /// </summary>
    public Func<MarkdownImage, string?>? ImageResolver { get; init; }

    /// <summary>The folder, beside the Markdown file, that images are written to when saving to a file; "&lt;name&gt;.images" when null.</summary>
    public string? ImagesFolder { get; init; }

    /// <summary>Dots per inch for vector figures (Word's EMF and WMF pictures) written as PNG.</summary>
    public double FigureResolution { get; init; } = 150;

    /// <summary>Writes vector figures as SVG instead of PNG.</summary>
    public bool VectorFiguresAsSvg { get; init; }

    /// <summary>Pictures narrower or shorter than this, in points, are left out as decoration (0 keeps them all).</summary>
    public double MinImageSize { get; init; } = 6;

    /// <summary>Keeps pictures behind the text (page backgrounds and watermarks), which are left out by default.</summary>
    public bool KeepBackgroundImages { get; init; }

    /// <summary>
    /// Writes a picture with the caption beside it as an HTML figure (an img and a figcaption) instead of an image and an
    /// italic paragraph.
    /// </summary>
    public bool CaptionsAsFigures { get; init; }

    /// <summary>
    /// Writes YAML front matter with the title and author when the document has them and did not come with front matter of
    /// its own (front matter that was read is always written back).
    /// </summary>
    public bool WriteFrontMatter { get; init; }
}

/// <summary>
/// Reads Markdown into a <see cref="Document"/> and writes documents as GitHub Flavored Markdown (Phase 6 design Q17):
/// CommonMark 0.31 with GitHub's tables, task lists, strikethrough, autolinks and footnotes, and YAML front matter. Headings take the Heading 1 to 6 styles, code the HTML Code
/// and HTML Preformatted styles, lists Word list definitions built from their nesting. Writing says in warnings what Markdown
/// cannot hold.
/// </summary>
public static class MarkdownFormat
{
    /// <summary>Reads Markdown text.</summary>
    public static Document Parse(string markdown, MarkdownLoadOptions? options = null)
    {
        return Parse(markdown, out _, options);
    }

    /// <summary>Reads Markdown text, reporting what could not be read as written.</summary>
    public static Document Parse(string markdown, out IReadOnlyList<ConversionWarning> warnings, MarkdownLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        options ??= new MarkdownLoadOptions();
        MdDocument syntax = BlockParser.Parse(markdown, options.Extensions);

        // Every CommonMark and GitHub construct maps onto the model, so reading has nothing to report yet.
        warnings = [];
        return MarkdownReader.Build(syntax, options);
    }

    /// <summary>Reads a Markdown file (UTF-8, or the encoding its byte order mark names).</summary>
    public static Document Load(string path, MarkdownLoadOptions? options = null)
    {
        return Load(path, out _, options);
    }

    /// <summary>Reads a Markdown file (UTF-8, or the encoding its byte order mark names), reporting what could not be read as written.</summary>
    public static Document Load(string path, out IReadOnlyList<ConversionWarning> warnings, MarkdownLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Document document = Parse(File.ReadAllText(path, Encoding.UTF8), out warnings, options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads Markdown from a stream (UTF-8, or the encoding its byte order mark names), leaving the stream open.</summary>
    public static Document Load(Stream stream, MarkdownLoadOptions? options = null)
    {
        return Load(stream, out _, options);
    }

    /// <summary>Reads Markdown from a stream, leaving it open, reporting what could not be read as written.</summary>
    public static Document Load(Stream stream, out IReadOnlyList<ConversionWarning> warnings, MarkdownLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Parse(reader.ReadToEnd(), out warnings, options);
    }

    /// <summary>The document as GitHub Flavored Markdown, with what was left out in <paramref name="warnings"/>.</summary>
    public static string ToMarkdown(Document document, out IReadOnlyList<ConversionWarning> warnings, MarkdownSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return MarkdownWriter.Write(document, options ?? new MarkdownSaveOptions(), null, out warnings);
    }

    /// <summary>The document as GitHub Flavored Markdown.</summary>
    public static string ToMarkdown(Document document, MarkdownSaveOptions? options = null)
    {
        return ToMarkdown(document, out _, options);
    }

    /// <summary>Writes the document as a Markdown file (UTF-8, LF line ends).</summary>
    /// <returns>What Markdown could not hold.</returns>
    public static IReadOnlyList<ConversionWarning> Save(Document document, string path, MarkdownSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(document);
        string text = MarkdownWriter.Write(document, options ?? new MarkdownSaveOptions(), path, out IReadOnlyList<ConversionWarning> warnings);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return warnings;
    }

    /// <summary>Writes the document as Markdown to a stream (UTF-8, LF line ends), leaving it open.</summary>
    /// <returns>What Markdown could not hold.</returns>
    public static IReadOnlyList<ConversionWarning> Save(Document document, Stream stream, MarkdownSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string text = ToMarkdown(document, out IReadOnlyList<ConversionWarning> warnings, options);
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        stream.Write(bytes);
        return warnings;
    }
}
