using System.Globalization;

using Egami;
using Egami.Drawing;
using Egami.Metadata;
using Egami.Pixels;

using Path = System.IO.Path;

namespace Egap.Pdf;

/// <summary>How images become pages in <see cref="PdfDocument.FromImages(IEnumerable{string}, PdfImagePageOptions?)"/>.</summary>
public sealed record PdfImagePageOptions
{
    /// <summary>Gets the defaults: each page the size of its image.</summary>
    public static PdfImagePageOptions Default { get; } = new();

    /// <summary>
    /// Gets the page size each image is fitted onto, keeping its proportions and centred; null (the default) for pages the
    /// size of the image at its resolution.
    /// </summary>
    public PdfPageSize? PageSize { get; init; }

    /// <summary>Gets the space left round the image on each side. Defaults to none.</summary>
    public Length Margin { get; init; }

    /// <summary>
    /// Gets whether a page of <see cref="PageSize"/> is turned to landscape for an image wider than it is tall. Defaults to
    /// true.
    /// </summary>
    public bool AutoRotate { get; init; } = true;

    /// <summary>Gets the resolution assumed for an image that does not store one, in dots per inch. Defaults to 96.</summary>
    public double Dpi { get; init; } = 96;
}

/// <summary>Pages made from images, and pages written as image files.</summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// A new document with a page for each image file: JPEG files are embedded as they are stored, other formats Egami reads
    /// (PNG, TIFF, WebP, GIF and more) are decoded and embedded losslessly, and each frame of a multi-page TIFF or GIF gets its
    /// own page.
    /// </summary>
    /// <example>
    /// <code>
    /// using PdfDocument scans = PdfDocument.FromImages(Directory.GetFiles("scans", "*.jpg").Order(),
    ///     new PdfImagePageOptions { PageSize = PdfPageSize.A4, Margin = Length.Mm(10) });
    /// scans.Save("scans.pdf");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <exception cref="ArgumentException">There is no image, or a size in the options is not positive.</exception>
    public static PdfDocument FromImages(IEnumerable<string> paths, PdfImagePageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return FromImages(paths.Select(p => (ReadOnlyMemory<byte>)File.ReadAllBytes(p)), options);
    }

    /// <summary>A new document with a page for each encoded image, as <see cref="FromImages(IEnumerable{string}, PdfImagePageOptions?)"/> makes.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> is null.</exception>
    /// <exception cref="ArgumentException">There is no image, or a size in the options is not positive.</exception>
    public static PdfDocument FromImages(IEnumerable<ReadOnlyMemory<byte>> images, PdfImagePageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        options ??= PdfImagePageOptions.Default;
        if (!(options.Dpi > 0) || options.Margin < 0 || options.PageSize is { } size && !(size.Width > 2 * options.Margin && size.Height > 2 * options.Margin))
        {
            throw new ArgumentException("The resolution must be positive and the margins smaller than the page.", nameof(options));
        }

        PdfDocument pdf = Create();
        try
        {
            foreach (ReadOnlyMemory<byte> image in images)
            {
                if (image.Span is [0xFF, 0xD8, 0xFF, ..])
                {
                    EncodedImage encoded = EncodedImage.Load(image);
                    AddImagePage(pdf, encoded.Width, encoded.Height, JfifResolution(image.Span), options, (canvas, box) => canvas.DrawImage(encoded, box));
                    continue;
                }

                foreach (Raster frame in Raster.LoadAll(image))
                {
                    AddImagePage(pdf, frame.Width, frame.Height, frame.Metadata?.Resolution, options, (canvas, box) => canvas.DrawImage(frame, box));
                    frame.Dispose();
                }
            }

            if (pdf.Pages.Count == 0)
            {
                throw new ArgumentException("There are no images.", nameof(images));
            }

            return pdf;
        }
        catch
        {
            pdf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Renders pages to image files, one per page, in the format the file extension names (<c>.png</c>, <c>.jpg</c>,
    /// <c>.webp</c>, <c>.tif</c> and the others Egami writes). <paramref name="path"/> holds <c>{page}</c> where the page
    /// number goes, optionally with a format such as <c>{page:000}</c>; without it, <c>-{page}</c> is added before the
    /// extension.
    /// </summary>
    /// <param name="path">The file path pattern, such as <c>"out/page-{page}.png"</c>.</param>
    /// <param name="options">How to render; 150 dots per inch by default.</param>
    /// <param name="pages">The pages to render; all of them when null.</param>
    /// <param name="cancellationToken">Stops the work between and inside pages.</param>
    /// <returns>The paths written, in page order.</returns>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Open("report.pdf");
    /// IReadOnlyList&lt;string&gt; files = pdf.RenderPagesToFiles("thumbs/page-{page:00}.png", new PdfRenderOptions { Dpi = 72 });
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public IReadOnlyList<string> RenderPagesToFiles(string path, PdfRenderOptions? options = null, PdfPageRange? pages = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!path.Contains("{page", StringComparison.Ordinal))
        {
            path = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + "-{page}" + Path.GetExtension(path));
        }

        options ??= new PdfRenderOptions { Dpi = 150 };
        List<string> written = [];
        foreach (int index in (pages ?? PdfPageRange.All).Resolve(Pages.Count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = FillPageNumber(path, index + 1);
            string? folder = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            using Raster<Rgba8> raster = Pages[index].Render(options, cancellationToken);
            raster.Save(file);
            written.Add(file);
        }

        return written;
    }

    private static string FillPageNumber(string pattern, int number)
    {
        int start = pattern.IndexOf("{page", StringComparison.Ordinal);
        int end = pattern.IndexOf('}', start);
        if (end < 0)
        {
            throw new ArgumentException($"The path '{pattern}' has an unclosed {{page}} field.", nameof(pattern));
        }

        string field = pattern[(start + 5)..end];
        string format = field.StartsWith(':') ? field[1..] : string.Empty;
        return pattern[..start] + number.ToString(format, CultureInfo.InvariantCulture) + pattern[(end + 1)..];
    }

    /// <summary>The density a JPEG's JFIF segment gives, when it gives one in dots per inch or centimetre.</summary>
    private static Resolution? JfifResolution(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg is not [0xFF, 0xD8, 0xFF, 0xE0, _, _, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, _, _, var units, var xh, var xl, var yh, var yl, ..])
        {
            return null;
        }

        double x = (xh << 8) | xl, y = (yh << 8) | yl;
        return units switch
        {
            1 when x > 0 && y > 0 => new Resolution(x, y, ResolutionUnit.PerInch),
            2 when x > 0 && y > 0 => new Resolution(x, y, ResolutionUnit.PerCentimeter),
            _ => null,
        };
    }

    private static void AddImagePage(PdfDocument pdf, int width, int height, Resolution? resolution, PdfImagePageOptions options, Action<PdfCanvas, RectF> draw)
    {
        (double dpiX, double dpiY) = (options.Dpi, options.Dpi);
        if (resolution is { Unit: not ResolutionUnit.None } r && r.ToPerInch() is { X: > 0, Y: > 0 } perInch)
        {
            (dpiX, dpiY) = (perInch.X, perInch.Y);
        }

        double imageWidth = width * 72 / dpiX, imageHeight = height * 72 / dpiY;
        double margin = options.Margin;
        PdfRectangle mediaBox;
        RectF box;
        if (options.PageSize is { } size)
        {
            if (options.AutoRotate && (imageWidth > imageHeight) != (size.Width > size.Height))
            {
                size = size.Turned;
            }

            double scale = Math.Min((size.Width - (2 * margin)) / imageWidth, (size.Height - (2 * margin)) / imageHeight);
            double w = imageWidth * scale, h = imageHeight * scale;
            mediaBox = size.ToRectangle();
            box = new RectF((float)((size.Width - w) / 2), (float)((size.Height - h) / 2), (float)w, (float)h);
        }
        else
        {
            mediaBox = new PdfRectangle(0, 0, imageWidth + (2 * margin), imageHeight + (2 * margin));
            box = new RectF((float)margin, (float)margin, (float)imageWidth, (float)imageHeight);
        }

        PdfPage page = pdf.Pages.AddBlank(mediaBox);
        using PdfCanvas canvas = new(page);
        draw(canvas, box);
    }
}
