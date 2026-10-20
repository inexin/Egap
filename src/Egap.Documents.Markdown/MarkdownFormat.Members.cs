using Egap.Documents.Markdown;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents;

// CA1708 misreads two extension blocks with different receivers as names differing only by case.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case", Justification = "C# 14 extension blocks")]
public static partial class MarkdownFormat
{
    extension(Document document)
    {
        /// <summary>Reads Markdown text, as <see cref="Parse(string, MarkdownLoadOptions?)"/> does.</summary>
        public static Document ParseMarkdown(string markdown, MarkdownLoadOptions? options = null) => Parse(markdown, options);

        /// <summary>
        /// Reads Markdown text, reporting what could not be read as written, as
        /// <see cref="Parse(string, out IReadOnlyList{ConversionWarning}, MarkdownLoadOptions?)"/> does.
        /// </summary>
        public static Document ParseMarkdown(string markdown, out IReadOnlyList<ConversionWarning> warnings, MarkdownLoadOptions? options = null) =>
            Parse(markdown, out warnings, options);

        /// <summary>Reads a Markdown file, as <see cref="Load(string, MarkdownLoadOptions?)"/> does.</summary>
        public static Document LoadMarkdown(string path, MarkdownLoadOptions? options = null) => Load(path, options);

        /// <summary>
        /// Reads a Markdown file, reporting what could not be read as written, as
        /// <see cref="Load(string, out IReadOnlyList{ConversionWarning}, MarkdownLoadOptions?)"/> does.
        /// </summary>
        public static Document LoadMarkdown(string path, out IReadOnlyList<ConversionWarning> warnings, MarkdownLoadOptions? options = null) =>
            Load(path, out warnings, options);

        /// <summary>Reads Markdown from a stream, leaving it open, as <see cref="Load(Stream, MarkdownLoadOptions?)"/> does.</summary>
        public static Document LoadMarkdown(Stream stream, MarkdownLoadOptions? options = null) => Load(stream, options);

        /// <summary>
        /// The document as GitHub Flavored Markdown, with what was left out in <paramref name="warnings"/>. Also callable as
        /// <c>MarkdownFormat.ToMarkdown(document, out warnings, options)</c>.
        /// </summary>
        public string ToMarkdown(out IReadOnlyList<ConversionWarning> warnings, MarkdownSaveOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(document);
            return MarkdownWriter.Write(document, options ?? new MarkdownSaveOptions(), null, out warnings);
        }

        /// <summary>The document as GitHub Flavored Markdown. Also callable as <c>MarkdownFormat.ToMarkdown(document, options)</c>.</summary>
        public string ToMarkdown(MarkdownSaveOptions? options = null) => document.ToMarkdown(out _, options);

        /// <summary>Writes the document as a Markdown file, as <see cref="Save(Document, string, MarkdownSaveOptions?)"/> does.</summary>
        /// <returns>What Markdown could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsMarkdown(string path, MarkdownSaveOptions? options = null) => Save(document, path, options);

        /// <summary>Writes the document as Markdown to a stream, as <see cref="Save(Document, Stream, MarkdownSaveOptions?)"/> does.</summary>
        /// <returns>What Markdown could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsMarkdown(Stream stream, MarkdownSaveOptions? options = null) => Save(document, stream, options);
    }

    extension(PdfConvert)
    {
        /// <summary>
        /// Lays out Markdown (CommonMark with GitHub's tables, task lists and strikethrough) and makes a PDF of it, open for more
        /// changes before it is saved. Images are read from <see cref="PdfConvertOptions.BaseDirectory"/> only.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is null.</exception>
        public static PdfDocument FromMarkdown(string markdown, PdfConvertOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(markdown);
            options ??= PdfConvertOptions.Default;
            return PdfConvert.ToPdf(Parse(markdown, InFolder(options.BaseDirectory)), options);
        }

        /// <summary>Converts Markdown to a PDF file.</summary>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static void FromMarkdown(string markdown, string outputPath, PdfConvertOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(outputPath);
            using PdfDocument pdf = PdfConvert.FromMarkdown(markdown, options);
            pdf.Save(outputPath, options?.Export?.Save);
        }
    }

    /// <summary>Load options whose images come from <paramref name="folder"/> and nowhere else; none at all when it is null.</summary>
    internal static MarkdownLoadOptions InFolder(string? folder)
    {
        if (folder is null)
        {
            return new MarkdownLoadOptions();
        }

        string root = Path.GetFullPath(folder);
        return new MarkdownLoadOptions
        {
            ImageResolver = link => Uri.TryCreate(link, UriKind.Absolute, out Uri? absolute) && !absolute.IsFile ? null
                : PdfConvert.ReadInside(root, Uri.UnescapeDataString(absolute?.LocalPath ?? link)) is { } bytes ? new ImageData(bytes, ImageData.GuessContentType(bytes)) : null,
        };
    }
}
