using System.Numerics;

using Egami;
using Egami.Operations;
using Egami.Pixels;

using Egap.Pdf.Rendering;

namespace Egap.Pdf.Ocr;

/// <summary>The image of a page an OCR engine reads, and how its pixels map onto the page.</summary>
/// <param name="Image">The pixels, origin at the top left.</param>
/// <param name="ImageToPage">The image's pixels to the page's user space.</param>
/// <param name="Dpi">The image's resolution on the page.</param>
/// <param name="Source">Whether the pixels are the page's own image or a render.</param>
internal sealed record OcrImage(Raster<Rgba8> Image, Matrix3x2 ImageToPage, double Dpi, PdfOcrImageSource Source) : IDisposable
{
    /// <summary>The share of the crop box one image must cover for its own pixels to be read instead of a render.</summary>
    private const double Coverage = 0.9;

    /// <summary>The lowest resolution of a scanned image read as it is; lower ones are rendered at the options' resolution.</summary>
    private const double MinScanDpi = 150;

    public void Dispose()
    {
        Image.Dispose();
    }

    /// <summary>
    /// The page's image for the engine (design Q5): its one scanned image as stored when that covers the page at a usable
    /// resolution and the page shows no text, else a render at the options' resolution; then fitted to the engine's largest
    /// image and straightened when the options ask.
    /// </summary>
    public static OcrImage For(PdfPage page, bool hasText, OcrEngineLimits limits, PdfOcrOptions options, CancellationToken cancellationToken)
    {
        OcrImage image = (hasText ? null : Scan(page, options)) ?? Render(page, options, cancellationToken);
        image = image.Fit(limits.MaxImageDimension, cancellationToken);
        return options.Deskew ? image.Straighten(cancellationToken) : image;
    }

    /// <summary>The page's one scanned image, read as it is, or null when there is none to use.</summary>
    private static OcrImage? Scan(PdfPage page, PdfOcrOptions options)
    {
        PdfRectangle crop = page.CropBox;
        double area = crop.Width * crop.Height;
        if (area <= 0)
        {
            return null;
        }

        List<PdfPlacedImage> placed;
        try
        {
            placed = [.. page.GetImages().Where(i => !i.Image.IsStencilMask)];
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return null;
        }

        List<PdfPlacedImage> covering = [.. placed.Where(i => Bounds(i.Matrix).Intersect(crop) is { IsEmpty: false } r && r.Width * r.Height >= area * Coverage)];
        if (covering.Count != 1)
        {
            return null;
        }

        PdfPlacedImage scan = covering[0];
        PdfImage image = scan.Image;
        PdfMatrix m = scan.Matrix;
        double dpiX = image.Width / (Math.Sqrt((m.A * m.A) + (m.B * m.B)) / 72), dpiY = image.Height / (Math.Sqrt((m.C * m.C) + (m.D * m.D)) / 72);
        if (image.Width <= 0 || image.Height <= 0 || Math.Min(dpiX, dpiY) < MinScanDpi || (long)image.Width * image.Height > options.MaxImagePixels)
        {
            return null;
        }

        Raster<Rgba8> raster;
        try
        {
            raster = image.ToRaster<Rgba8>();
        }
        catch (Exception e) when (e is PdfException or EgamiException && e is not PdfLimitException)
        {
            return null;
        }

        // Pixels from the top left to the image's unit square (row 0 at its top), then the placement onto the page.
        Matrix3x2 toUnit = new(1f / raster.Width, 0, 0, -1f / raster.Height, 0, 1);
        return new OcrImage(raster, toUnit * ToMatrix(m), Math.Round(Math.Min(dpiX, dpiY), 1), PdfOcrImageSource.PageImage);
    }

    private static OcrImage Render(PdfPage page, PdfOcrOptions options, CancellationToken cancellationToken)
    {
        PdfRectangle crop = page.CropBox;
        double inches = crop.Width * crop.Height * page.UserUnit * page.UserUnit / (72 * 72);
        double dpi = options.Dpi;
        if (inches > 0 && inches * dpi * dpi > options.MaxImagePixels)
        {
            dpi = Math.Max(1, Math.Floor(Math.Sqrt(options.MaxImagePixels / inches)));
        }

        PdfRenderOptions render = new() { Dpi = dpi, Annotations = PdfAnnotationRendering.None, Background = new RgbaF32(1, 1, 1, 1) };
        Raster<Rgba8> raster = page.Render(render, cancellationToken);
        Matrix3x2.Invert(RenderGeometry.Of(page, render).PageToDevice, out Matrix3x2 toPage);
        return new OcrImage(raster, toPage, dpi, PdfOcrImageSource.Rendered);
    }

    /// <summary>The image scaled down to the engine's largest side, boxes scaled back up (design Q8).</summary>
    private OcrImage Fit(int? maxDimension, CancellationToken cancellationToken)
    {
        int largest = Math.Max(Image.Width, Image.Height);
        if (maxDimension is not int max || largest <= max)
        {
            return this;
        }

        double scale = (double)max / largest;
        int width = Math.Max(1, (int)Math.Floor(Image.Width * scale)), height = Math.Max(1, (int)Math.Floor(Image.Height * scale));
        Raster<Rgba8> smaller = Image.Derive(ops => ops.Resize(width, height), cancellationToken);
        Matrix3x2 back = Matrix3x2.CreateScale((float)Image.Width / width, (float)Image.Height / height);
        Dispose();
        return new OcrImage(smaller, back * ImageToPage, Dpi * width / Image.Width, Source);
    }

    /// <summary>The image turned level by its detected skew, the turn undone on the way back to the page (design Q5).</summary>
    private OcrImage Straighten(CancellationToken cancellationToken)
    {
        float angle = Image.DetectSkew();
        if (angle == 0)
        {
            return this;
        }

        // The same turn Egami's Deskew makes, about the centre at the same size; its inverse maps the engine's words back.
        Vector2 center = new(Image.Width / 2f, Image.Height / 2f);
        Homography turn = Homography.CreateRotation(-angle * Math.PI / 180, center);
        Homography back = Homography.CreateRotation(angle * Math.PI / 180, center);
        Rgba8 fill = Image.GetPixel(0, 0);
        Raster<Rgba8> level = Image.Derive(ops => ops.Transform(turn, new TransformOptions { Background = fill, Viewport = new Rect(0, 0, Image.Width, Image.Height) }), cancellationToken);
        Dispose();
        return new OcrImage(level, ToMatrix(back) * ImageToPage, Dpi, Source);
    }

    /// <summary>The page-space box of an image's unit square.</summary>
    private static PdfRectangle Bounds(PdfMatrix m)
    {
        PdfPoint[] corners = [m.Transform(new PdfPoint(0, 0)), m.Transform(new PdfPoint(1, 0)), m.Transform(new PdfPoint(0, 1)), m.Transform(new PdfPoint(1, 1))];
        return new PdfRectangle(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    internal static Matrix3x2 ToMatrix(PdfMatrix m)
    {
        return new Matrix3x2((float)m.A, (float)m.B, (float)m.C, (float)m.D, (float)m.E, (float)m.F);
    }

    /// <summary>An affine homography as a matrix, read off where it maps the origin and the two unit points.</summary>
    private static Matrix3x2 ToMatrix(Homography h)
    {
        Vector2 o = h.Map(Vector2.Zero), x = h.Map(Vector2.UnitX) - o, y = h.Map(Vector2.UnitY) - o;
        return new Matrix3x2(x.X, x.Y, y.X, y.Y, o.X, o.Y);
    }
}

/// <summary>What the driver needs to know of an engine: its largest image.</summary>
internal readonly record struct OcrEngineLimits(int? MaxImageDimension);

/// <summary>Text a page shows, sorted by how it is drawn: visible, or invisible (render mode 3, as OCR layers are).</summary>
internal sealed class PageTextCounter : PdfContentWalker
{
    public int Visible { get; private set; }

    public int Invisible { get; private set; }

    public static PageTextCounter Of(PdfPage page)
    {
        PageTextCounter counter = new();
        counter.Walk(page);
        return counter;
    }

    protected override void OnGlyph(PdfGlyph glyph)
    {
        if (State.TextRenderMode == 3)
        {
            Invisible++;
        }
        else if (State.TextRenderMode != 7)
        {
            Visible++;
        }
    }

    protected override bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure) => false;
}

/// <summary>
/// Removes an earlier invisible text layer (design Q4's Redo): every text-showing operator drawn in render mode 3, from the
/// page's content and from the forms it draws, which is how OCR layers are written. Visible text is kept.
/// </summary>
internal sealed class InvisibleTextRemover
{
    private static readonly HashSet<string> Shows = ["Tj", "TJ", "'", "\""];

    private readonly PdfDocument document;
    private readonly HashSet<PdfStream> done = new(ReferenceEqualityComparer.Instance);
    private int removed;

    private InvisibleTextRemover(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>Removes the page's invisible text; how many glyphs went.</summary>
    public static int Remove(PdfPage page)
    {
        InvisibleTextRemover remover = new(page.Document);
        if (remover.Rewrite(page.GetContent(), page.Resources, 0) is PdfContent content)
        {
            page.SetContent(content);
        }

        return remover.removed;
    }

    /// <summary>The content without its invisible text, or null when it has none; forms it draws are rewritten in place.</summary>
    private PdfContent? Rewrite(PdfContent content, PdfDictionary? resources, int inheritedMode)
    {
        Recorder recorder = new(inheritedMode);
        recorder.Walk(content, resources, PdfMatrix.Identity, document.Options);
        foreach ((PdfStream form, int mode) in recorder.Forms)
        {
            Form(form, mode);
        }

        if (recorder.Invisible.Count == 0)
        {
            return null;
        }

        PdfContent result = [];
        for (int i = 0; i < content.Count; i++)
        {
            if (!(Shows.Contains(content[i].Operator) && recorder.Invisible.TryGetValue(i, out int glyphs)))
            {
                result.Add(content[i]);
                continue;
            }

            removed += glyphs;
        }

        return result;
    }

    private void Form(PdfStream form, int mode)
    {
        if (!done.Add(form))
        {
            return;
        }

        PdfContent? content;
        try
        {
            content = Rewrite(PdfContent.Read(form), form.Dictionary.Get<PdfDictionary>("Resources"), mode);
        }
        catch (PdfException e) when (e is not PdfLimitException && !document.Options.Strict)
        {
            content = null;
        }

        if (content is not null)
        {
            form.Dictionary.Remove("DecodeParms");
            form.Dictionary["Filter"] = PdfName.Get("FlateDecode");
            form.SetDecodedBytes(content.ToBytes());
        }
    }

    /// <summary>Records which operators of one content stream show invisible glyphs, and the mode each form is drawn in.</summary>
    private sealed class Recorder(int inheritedMode) : PdfContentWalker
    {
        private int index = -1;
        private bool modeSet;

        /// <summary>The invisible glyphs by the index of the operator that showed them.</summary>
        public Dictionary<int, int> Invisible { get; } = [];

        public Dictionary<PdfStream, int> Forms { get; } = new(ReferenceEqualityComparer.Instance);

        private int Mode => modeSet ? State.TextRenderMode : inheritedMode;

        protected override void OnOperation(PdfOperation operation)
        {
            if (Depth == 0)
            {
                index++;
                modeSet |= operation.Operator == "Tr";
            }
        }

        protected override void OnGlyph(PdfGlyph glyph)
        {
            if (Depth == 0 && Mode == 3)
            {
                Invisible[index] = Invisible.GetValueOrDefault(index) + 1;
            }
        }

        protected override bool OnBeginForm(PdfStream form, PdfName name)
        {
            Forms.TryAdd(form, Mode);
            return false;
        }

        protected override bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure) => false;
    }
}
