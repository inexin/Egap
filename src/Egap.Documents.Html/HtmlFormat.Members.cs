using Egap.Documents.Html;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents;

// CA1708 misreads two extension blocks with different receivers as names differing only by case.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case", Justification = "C# 14 extension blocks")]
public static partial class HtmlFormat
{
    extension(Document document)
    {
        /// <summary>Reads HTML from a string, as <see cref="Parse(string, HtmlLoadOptions?)"/> does.</summary>
        public static Document ParseHtml(string html, HtmlLoadOptions? options = null) => Parse(html, options);

        /// <summary>
        /// Reads HTML from a string, reporting what could only be approximated or was left out, as
        /// <see cref="Parse(string, out IReadOnlyList{ConversionWarning}, HtmlLoadOptions?)"/> does.
        /// </summary>
        public static Document ParseHtml(string html, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null) =>
            Parse(html, out warnings, options);

        /// <summary>Reads an HTML file, as <see cref="Load(string, HtmlLoadOptions?)"/> does.</summary>
        public static Document LoadHtml(string path, HtmlLoadOptions? options = null) => Load(path, options);

        /// <summary>
        /// Reads an HTML file, reporting what could only be approximated or was left out, as
        /// <see cref="Load(string, out IReadOnlyList{ConversionWarning}, HtmlLoadOptions?)"/> does.
        /// </summary>
        public static Document LoadHtml(string path, out IReadOnlyList<ConversionWarning> warnings, HtmlLoadOptions? options = null) =>
            Load(path, out warnings, options);

        /// <summary>Reads HTML from a stream, as <see cref="Load(Stream, HtmlLoadOptions?)"/> does.</summary>
        public static Document LoadHtml(Stream stream, HtmlLoadOptions? options = null) => Load(stream, options);

        /// <summary>
        /// The document as an HTML5 page with one style sheet, reporting what HTML could not hold. Also callable as
        /// <c>HtmlFormat.ToHtml(document, out warnings, options)</c>.
        /// </summary>
        public string ToHtml(out IReadOnlyList<ConversionWarning> warnings, HtmlSaveOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(document);
            return HtmlWriter.Write(document, options ?? new HtmlSaveOptions(), out warnings);
        }

        /// <summary>The document as an HTML5 page. Also callable as <c>HtmlFormat.ToHtml(document, options)</c>.</summary>
        public string ToHtml(HtmlSaveOptions? options = null) => document.ToHtml(out _, options);

        /// <summary>Saves the document as an HTML file, as <see cref="Save(Document, string, HtmlSaveOptions?)"/> does.</summary>
        /// <returns>What HTML could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsHtml(string path, HtmlSaveOptions? options = null) => Save(document, path, options);

        /// <summary>Writes the document as HTML to a stream, as <see cref="Save(Document, Stream, HtmlSaveOptions?)"/> does.</summary>
        /// <returns>What HTML could not hold.</returns>
        public IReadOnlyList<ConversionWarning> SaveAsHtml(Stream stream, HtmlSaveOptions? options = null) => Save(document, stream, options);
    }

    extension(PdfConvert)
    {
        /// <summary>
        /// Lays out HTML and makes a PDF of it, open for more changes before it is saved. Images and style sheets are read
        /// from <see cref="PdfConvertOptions.BaseDirectory"/> only; HTML page bands are styled with the page's style sheets.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
        public static PdfDocument FromHtml(string html, PdfConvertOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(html);
            options ??= PdfConvertOptions.Default;
            PdfConvert.Bands bands = new(null, options.BaseDirectory, html, (band, folder) => Parse(band, InFolder(folder)));
            return PdfConvert.ToPdf(Parse(html, InFolder(options.BaseDirectory)), options, bands);
        }

        /// <summary>Converts HTML to a PDF file.</summary>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static void FromHtml(string html, string outputPath, PdfConvertOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(outputPath);
            using PdfDocument pdf = PdfConvert.FromHtml(html, options);
            pdf.Save(outputPath, options?.Export?.Save);
        }
    }

    /// <summary>
    /// Load options whose images and style sheets come from <paramref name="folder"/> and nowhere else; none at all when it is
    /// null.
    /// </summary>
    internal static HtmlLoadOptions InFolder(string? folder, IReadOnlyList<string>? userStyleSheets = null)
    {
        if (folder is null)
        {
            return new HtmlLoadOptions { UserStyleSheets = userStyleSheets ?? [] };
        }

        string root = Path.GetFullPath(folder);
        return new HtmlLoadOptions
        {
            BaseUri = new Uri(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar),
            ResourceResolver = uri => uri.IsFile ? PdfConvert.ReadInside(root, uri.LocalPath) : null,
            UserStyleSheets = userStyleSheets ?? [],
        };
    }
}
