using System.Text.RegularExpressions;

using Egap.Documents;

namespace Egap.Pdf.Conversion;

/// <summary>The space between the page edges and the content, on each side.</summary>
/// <param name="Top">The top margin.</param>
/// <param name="Right">The right margin.</param>
/// <param name="Bottom">The bottom margin.</param>
/// <param name="Left">The left margin.</param>
public readonly record struct PageMargins(Length Top, Length Right, Length Bottom, Length Left)
{
    /// <summary>The same margin on every side.</summary>
    public static PageMargins All(Length margin) => new(margin, margin, margin, margin);

    /// <summary>One margin for the top and bottom and another for the left and right.</summary>
    public static PageMargins Symmetric(Length vertical, Length horizontal) => new(vertical, horizontal, vertical, horizontal);
}

/// <summary>How <see cref="PdfConvert"/> turns HTML, Markdown, DOCX and images into PDF.</summary>
public sealed record PdfConvertOptions
{
    /// <summary>Gets the defaults: the input's own page size and margins, or A4 with 2 cm margins where it has none.</summary>
    public static PdfConvertOptions Default { get; } = new();

    /// <summary>
    /// Gets the page size every page is laid out on, in place of the input's own (a DOCX file's sections, an HTML
    /// <c>@page</c> rule); null (the default) to keep it.
    /// </summary>
    public PdfPageSize? PageSize { get; init; }

    /// <summary>Gets the page margins every page is laid out with, in place of the input's own; null (the default) to keep them.</summary>
    public PageMargins? Margins { get; init; }

    /// <summary>
    /// Gets the folder relative links to images and style sheets in HTML or Markdown given as text are read from; null
    /// (the default) to read none. Converting a file reads them from the file's own folder. Only files inside the folder
    /// are read, and nothing from the network.
    /// </summary>
    public string? BaseDirectory { get; init; }

    /// <summary>
    /// Gets the header put in the top margin of every page, in place of the input's own headers and top margin boxes;
    /// null (the default) to keep those.
    /// </summary>
    public PageBand? Header { get; init; }

    /// <summary>
    /// Gets the footer put in the bottom margin of every page, in place of the input's own footers and bottom margin boxes;
    /// null (the default) to keep those.
    /// </summary>
    public PageBand? Footer { get; init; }

    /// <summary>Gets the PDF/A level to write; <see cref="PdfAConformance.None"/> (the default) for none.</summary>
    public PdfAConformance Conformance { get; init; }

    /// <summary>Gets whether the PDF is tagged for accessibility. Defaults to false.</summary>
    public bool Tagged { get; init; }

    /// <summary>
    /// Gets the rest of the export settings (outline, links, layout and save options); null for the defaults.
    /// <see cref="Conformance"/> and <see cref="Tagged"/> are taken from these options when they are set here.
    /// </summary>
    public PdfExportOptions? Export { get; init; }

    internal PdfExportOptions ExportOptions()
    {
        PdfExportOptions export = Export ?? PdfExportOptions.Default;
        return export with
        {
            Conformance = Conformance != PdfAConformance.None ? Conformance : export.Conformance,
            Tagged = Tagged || export.Tagged,
        };
    }
}

/// <summary>
/// One-line conversions to PDF: HTML, Markdown, DOCX and images in, a PDF file or an open <see cref="PdfDocument"/> out.
/// Each reads the input with Egap.Documents, lays it out and exports it as <see cref="PdfExport"/> does.
/// </summary>
/// <example>
/// <code>
/// PdfConvert.FromHtml("&lt;h1&gt;Invoice&lt;/h1&gt;&lt;p&gt;Total: 120 EUR&lt;/p&gt;", "invoice.pdf",
///     new PdfConvertOptions { PageSize = PdfPageSize.A4, Margins = PageMargins.All(Length.Mm(20)) });
/// PdfConvert.FromFile("report.docx", "report.pdf");
/// </code>
/// </example>
public static partial class PdfConvert
{
    /// <summary>Lays out HTML and makes a PDF of it, open for more changes before it is saved.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    public static PdfDocument FromHtml(string html, PdfConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= PdfConvertOptions.Default;
        HtmlLoadOptions load = HtmlOptions(options.BaseDirectory);
        return ToPdf(HtmlFormat.Parse(html, load), options, new Bands(null, load, html));
    }

    /// <summary>Converts HTML to a PDF file.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void FromHtml(string html, string outputPath, PdfConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        using PdfDocument pdf = FromHtml(html, options);
        pdf.Save(outputPath, options?.Export?.Save);
    }

    /// <summary>Lays out Markdown (CommonMark with GitHub's tables, task lists and strikethrough) and makes a PDF of it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is null.</exception>
    public static PdfDocument FromMarkdown(string markdown, PdfConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        options ??= PdfConvertOptions.Default;
        return ToPdf(MarkdownFormat.Parse(markdown, MarkdownOptions(options.BaseDirectory)), options);
    }

    /// <summary>Converts Markdown to a PDF file.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void FromMarkdown(string markdown, string outputPath, PdfConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        using PdfDocument pdf = FromMarkdown(markdown, options);
        pdf.Save(outputPath, options?.Export?.Save);
    }

    /// <summary>
    /// Makes a PDF of a file: DOCX (and DOTX, DOCM), HTML and Markdown are laid out; an image file (JPEG, PNG, TIFF, WebP,
    /// GIF and the other formats Egami reads) becomes one page per frame. HTML and Markdown read their images from the
    /// file's own folder.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="DocumentException">The file is not a document or image Egap can read.</exception>
    public static PdfDocument FromFile(string path, PdfConvertOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= PdfConvertOptions.Default;
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string extension = Path.GetExtension(path).ToUpperInvariant();
        Document document;
        Bands bands = new(path, null, null);
        switch (extension)
        {
            case ".HTML" or ".HTM" or ".XHTML":
                HtmlLoadOptions load = HtmlOptions(options.BaseDirectory ?? folder);
                document = HtmlFormat.Load(path, load);
                bands = new Bands(path, load, options.Header?.HtmlContent is null && options.Footer?.HtmlContent is null ? null : File.ReadAllText(path));
                break;
            case ".MD" or ".MARKDOWN" or ".MDOWN" or ".MKD":
                document = MarkdownFormat.Load(path, MarkdownOptions(options.BaseDirectory ?? folder));
                break;
            case ".DOCX" or ".DOTX" or ".DOCM" or ".DOTM":
                document = DocxFormat.Load(path);
                break;
            default:
                byte[] bytes = File.ReadAllBytes(path);
                if (IsImage(bytes))
                {
                    return FromImages([bytes], options);
                }

                document = Document.Load(path);
                break;
        }

        return ToPdf(document, options, bands);
    }

    /// <summary>Converts a file to a PDF file, as <see cref="FromFile(string, PdfConvertOptions?)"/> reads it.</summary>
    /// <exception cref="ArgumentException">A path is empty.</exception>
    /// <exception cref="DocumentException">The file is not a document or image Egap can read.</exception>
    public static void FromFile(string inputPath, string outputPath, PdfConvertOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        using PdfDocument pdf = FromFile(inputPath, options);
        pdf.Save(outputPath, options?.Export?.Save);
    }

    /// <summary>
    /// A page for each image file, as <see cref="PdfDocument.FromImages(IEnumerable{string}, PdfImagePageOptions?)"/>
    /// makes them, fitted onto <see cref="PdfConvertOptions.PageSize"/> within the margins when one is given.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public static PdfDocument FromImages(IEnumerable<string> paths, PdfConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return FromImages(paths.Select(p => (ReadOnlyMemory<byte>)File.ReadAllBytes(p)), options);
    }

    /// <summary>Lays a document out on the options' page size and margins and exports it.</summary>
    internal static PdfDocument ToPdf(Document document, PdfConvertOptions options, Bands bands = default)
    {
        if (options.Header is not null || options.Footer is not null)
        {
            PageBandContext context = new(document, bands.File, bands.Load, bands.Source is null ? null : StyleSheets(bands.Source));
            options.Header?.Apply(document, header: true, context);
            options.Footer?.Apply(document, header: false, context);
        }

        foreach (Section section in document.Sections)
        {
            if (options.PageSize is { } size)
            {
                section.PageSetup.PageWidth = size.Width;
                section.PageSetup.PageHeight = size.Height;
                section.PageSetup.Orientation = size.Width > size.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
            }

            if (options.Margins is { } margins)
            {
                section.PageSetup.MarginTop = margins.Top;
                section.PageSetup.MarginRight = margins.Right;
                section.PageSetup.MarginBottom = margins.Bottom;
                section.PageSetup.MarginLeft = margins.Left;
            }
        }

        return document.ToPdf(options.ExportOptions());
    }

    /// <summary>Where a document came from, for the placeholders and the style sheets of HTML bands.</summary>
    internal readonly record struct Bands(string? File, HtmlLoadOptions? Load, string? Source);

    /// <summary>The style and style sheet link elements of an HTML source, for a band to be styled as the page is.</summary>
    private static string StyleSheets(string html)
    {
        return string.Concat(StyleElements().Matches(html).Select(m => m.Value));
    }

    [GeneratedRegex(@"<style\b[^>]*>.*?</style\s*>|<link\b[^>]*\bstylesheet\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleElements();

    internal static HtmlLoadOptions HtmlOptions(string? folder, IReadOnlyList<string>? userStyleSheets = null)
    {
        if (folder is null)
        {
            return new HtmlLoadOptions { UserStyleSheets = userStyleSheets ?? [] };
        }

        string root = Path.GetFullPath(folder);
        return new HtmlLoadOptions
        {
            BaseUri = new Uri(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar),
            ResourceResolver = uri => uri.IsFile ? ReadInside(root, uri.LocalPath) : null,
            UserStyleSheets = userStyleSheets ?? [],
        };
    }

    private static PdfDocument FromImages(IEnumerable<ReadOnlyMemory<byte>> images, PdfConvertOptions? options)
    {
        PdfImagePageOptions pages = new()
        {
            PageSize = options?.PageSize,
            Margin = options?.Margins is { } m ? Math.Max(Math.Max(m.Top, m.Bottom), Math.Max(m.Left, m.Right)) : 0,
        };
        return PdfDocument.FromImages(images, pages);
    }

    private static MarkdownLoadOptions MarkdownOptions(string? folder)
    {
        if (folder is null)
        {
            return new MarkdownLoadOptions();
        }

        string root = Path.GetFullPath(folder);
        return new MarkdownLoadOptions
        {
            ImageResolver = link => Uri.TryCreate(link, UriKind.Absolute, out Uri? absolute) && !absolute.IsFile ? null
                : ReadInside(root, Uri.UnescapeDataString(absolute?.LocalPath ?? link)) is { } bytes ? new ImageData(bytes, ImageData.GuessContentType(bytes)) : null,
        };
    }

    /// <summary>A file's bytes when it lies inside the folder; null for anything outside it or missing.</summary>
    private static byte[]? ReadInside(string root, string path)
    {
        string full = Path.GetFullPath(Path.Combine(root, path));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(full)
            ? File.ReadAllBytes(full)
            : null;
    }

    private static bool IsImage(ReadOnlySpan<byte> bytes)
    {
        return bytes is [0xFF, 0xD8, 0xFF, ..]
            or [0x89, (byte)'P', (byte)'N', (byte)'G', ..]
            or [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..]
            or [(byte)'I', (byte)'I', 42, 0, ..]
            or [(byte)'M', (byte)'M', 0, 42, ..]
            or [(byte)'B', (byte)'M', ..]
            or [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..];
    }
}
