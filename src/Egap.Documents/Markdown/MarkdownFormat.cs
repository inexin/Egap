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

/// <summary>
/// Reads Markdown into a <see cref="Document"/> (Phase 6 design Q17): CommonMark 0.31 with GitHub's tables, task lists,
/// strikethrough, autolinks and footnotes, and YAML front matter. Headings take the Heading 1 to 6 styles, code the HTML Code
/// and HTML Preformatted styles, lists Word list definitions built from their nesting.
/// </summary>
public static class MarkdownFormat
{
    /// <summary>Reads Markdown text.</summary>
    public static Document Parse(string markdown, MarkdownLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        options ??= new MarkdownLoadOptions();
        MdDocument syntax = BlockParser.Parse(markdown, options.Extensions);
        return MarkdownReader.Build(syntax, options);
    }

    /// <summary>Reads a Markdown file (UTF-8, or the encoding its byte order mark names).</summary>
    public static Document Load(string path, MarkdownLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Document document = Parse(File.ReadAllText(path, Encoding.UTF8), options);
        document.FileName = path;
        return document;
    }

    /// <summary>Reads Markdown from a stream (UTF-8, or the encoding its byte order mark names), leaving the stream open.</summary>
    public static Document Load(Stream stream, MarkdownLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Parse(reader.ReadToEnd(), options);
    }
}
