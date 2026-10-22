using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Rendering;

namespace Egap.Pdf;

public sealed partial class PdfPage
{
    /// <summary>
    /// The page's width in points as shown: the crop box scaled by <see cref="UserUnit"/> and turned by <see cref="Rotate"/>,
    /// which is the width of a render at 72 dpi before rounding up to whole pixels.
    /// </summary>
    public double DisplayWidth => RenderGeometry.Of(this, PdfRenderOptions.Default).PageWidth;

    /// <summary>The page's height in points as shown, like <see cref="DisplayWidth"/>.</summary>
    public double DisplayHeight => RenderGeometry.Of(this, PdfRenderOptions.Default).PageHeight;

    /// <summary>Renders the page to an 8-bit RGBA raster (Phase 4 design Q3, Q4).</summary>
    /// <param name="options">The resolution, box, rotation, region, background and the rest; <see cref="PdfRenderOptions.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Checked between content operators.</param>
    /// <exception cref="PdfLimitException">The output would have more than <see cref="PdfRenderOptions.MaxOutputPixels"/> pixels, or a limit was passed in strict mode.</exception>
    /// <exception cref="PdfException">The page has a problem and <see cref="PdfRenderOptions.Strict"/> is set.</exception>
    /// <exception cref="OperationCanceledException">The render was cancelled.</exception>
    public Raster<Rgba8> Render(PdfRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Render<Rgba8>(options, cancellationToken);
    }

    /// <summary>Renders the page to a raster of any Egami pixel type (design Q4).</summary>
    /// <param name="options">The resolution, box, rotation, region, background and the rest; <see cref="PdfRenderOptions.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Checked between content operators.</param>
    /// <exception cref="PdfLimitException">The output would have more than <see cref="PdfRenderOptions.MaxOutputPixels"/> pixels, or a limit was passed in strict mode.</exception>
    /// <exception cref="PdfException">The page has a problem and <see cref="PdfRenderOptions.Strict"/> is set.</exception>
    /// <exception cref="OperationCanceledException">The render was cancelled.</exception>
    public Raster<TPixel> Render<TPixel>(PdfRenderOptions? options = null, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        options ??= PdfRenderOptions.Default;
        RenderGeometry geometry = RenderGeometry.Of(this, options);
        if ((long)geometry.Width * geometry.Height > options.MaxOutputPixels)
        {
            throw new PdfLimitException(nameof(PdfRenderOptions.MaxOutputPixels), $"A render of {geometry.Width} by {geometry.Height} pixels passes the limit of {options.MaxOutputPixels:N0}; render it in tiles through the region.");
        }

        if (options.SimulateOverprint)
        {
            using PdfSeparations plates = RenderSeparations(options with { SimulateOverprint = false }, cancellationToken: cancellationToken);
            return plates.Inks.Proof<TPixel>();
        }

        Raster<TPixel> raster = Raster.Create<TPixel>(geometry.Width, geometry.Height);
        try
        {
            using RasterCanvas<TPixel> canvas = new(raster);
            canvas.Clear(options.Background);
            Draw(canvas, geometry.PageToDevice, options, background: false, cancellationToken);
        }
        catch
        {
            raster.Dispose();
            throw;
        }

        return raster;
    }

    /// <summary>
    /// Draws the page onto any Egami canvas (design Q2): a raster, Egami.Svg's SVG canvas, a picture recorder or a PDF
    /// canvas. The page is placed as <see cref="Render(PdfRenderOptions?, CancellationToken)"/> would place it in its raster, with (0, 0) at the
    /// canvas's top left, so at the default 72 dpi a canvas of <see cref="DisplayWidth"/> by <see cref="DisplayHeight"/> holds the page exactly. The
    /// background is filled first unless it is transparent.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="options">The resolution, box, rotation, region, background and the rest; <see cref="PdfRenderOptions.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Checked between content operators.</param>
    /// <exception cref="PdfException">The page has a problem and <see cref="PdfRenderOptions.Strict"/> is set.</exception>
    /// <exception cref="OperationCanceledException">The render was cancelled.</exception>
    public void RenderTo(Canvas canvas, PdfRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        options ??= PdfRenderOptions.Default;
        if (options.SimulateOverprint)
        {
            // The soft proof is pixels: it is drawn as an image where the page goes.
            using Raster<Rgba8> proof = Render<Rgba8>(options, cancellationToken);
            canvas.DrawImage(proof, new RectF(0, 0, proof.Width, proof.Height));
            return;
        }

        Draw(canvas, RenderGeometry.Of(this, options).PageToDevice, options, background: true, cancellationToken);
    }

    /// <summary>
    /// Records the page once as an Egami picture (design Q16), a display list: parsing, fonts and image decoding are done,
    /// and the picture replays on any thread, at any scale and for any tile. It is in the render's pixels at
    /// <paramref name="options"/>' resolution (72 dpi, one unit per point, by default), so <c>picture.ToRaster(2)</c> of the
    /// default gives the page at 144 dpi.
    /// </summary>
    /// <param name="options">The resolution, box, rotation, background and the rest; <see cref="PdfRenderOptions.Default"/> when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Checked between content operators.</param>
    /// <exception cref="PdfException">The page has a problem and <see cref="PdfRenderOptions.Strict"/> is set.</exception>
    /// <exception cref="OperationCanceledException">The recording was cancelled.</exception>
    public Picture ToPicture(PdfRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= PdfRenderOptions.Default;
        RenderGeometry geometry = RenderGeometry.Of(this, options);
        PictureRecorder recorder = new(new RectF(0, 0, geometry.Width, geometry.Height));
        Draw(recorder, geometry.PageToDevice, options, background: true, cancellationToken);
        return recorder.Finish();
    }

    private void Draw(Canvas canvas, Matrix3x2 pageToDevice, PdfRenderOptions options, bool background, CancellationToken cancellationToken)
    {
        // Fonts that drawing on this document embedded are written only on save; a render needs their glyphs now.
        Document.WrittenResources.Finish();
        RenderSettings settings = RenderSettings.From(options) with
        {
            Layers = Document.Catalog.Get<PdfDictionary>("OCProperties") is null && options.Layers is null ? null : new OptionalContent(Document, options.Intent, options.Layers, options.LayerConfiguration),
            Cancellation = cancellationToken,
        };
        PdfReadOptions read = options.Strict ? Document.Options with { Strict = true } : Document.Options;
        int saves = canvas.Save();
        try
        {
            Matrix3x2 outer = canvas.Matrix;
            Matrix3x2 device = pageToDevice * outer;
            PdfRectangle box = RenderGeometry.BoxOf(this, options.Box);
            canvas.SetMatrix(device);
            canvas.ClipRect(new RectF((float)box.Left, (float)box.Bottom, (float)box.Width, (float)box.Height), antialias: false);
            if (background && options.Background.A > 0)
            {
                canvas.FillRect(new RectF((float)box.Left, (float)box.Bottom, (float)box.Width, (float)box.Height), new SolidPaint(options.Background) { Antialias = false });
            }

            if (Dictionary.Get<PdfDictionary>("Group")?.GetName("S")?.Value == "Transparency")
            {
                // A page with a transparency group is that group (11.4.7): drawn on its own transparent backdrop and put on
                // the paper afterwards, so blend modes see no white where nothing is drawn yet, as in PDFium and pdf.js.
                canvas.SaveLayer(new LayerOptions { Isolated = true });
            }

            PageRenderer renderer = new(canvas, device, settings, Document.RenderCache);
            try
            {
                renderer.Render(GetContent(), Resources, read);
                renderer.RenderAnnotations(this, options.Annotations);
            }
            finally
            {
                if (options.OnWarning is Action<PdfWarning> report)
                {
                    foreach (PdfWarning warning in renderer.Collected)
                    {
                        report(warning);
                    }
                }
            }
        }
        finally
        {
            canvas.RestoreToCount(saves);
        }
    }
}
