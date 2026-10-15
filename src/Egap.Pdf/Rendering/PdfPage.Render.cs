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
    public Raster<Rgba8> Render(PdfRenderOptions? options = null)
    {
        return Render<Rgba8>(options);
    }

    /// <summary>Renders the page to a raster of any Egami pixel type (design Q4).</summary>
    /// <param name="options">The resolution, box, rotation, region, background and the rest; <see cref="PdfRenderOptions.Default"/> when <see langword="null"/>.</param>
    public Raster<TPixel> Render<TPixel>(PdfRenderOptions? options = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        options ??= PdfRenderOptions.Default;
        RenderGeometry geometry = RenderGeometry.Of(this, options);
        Raster<TPixel> raster = Raster.Create<TPixel>(geometry.Width, geometry.Height);
        try
        {
            using RasterCanvas<TPixel> canvas = new(raster);
            canvas.Clear(options.Background);
            Draw(canvas, geometry.PageToDevice, options, background: false);
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
    /// canvas. The page is placed as <see cref="Render(PdfRenderOptions?)"/> would place it in its raster, with (0, 0) at the
    /// canvas's top left, so at the default 72 dpi a canvas of <see cref="DisplayWidth"/> by <see cref="DisplayHeight"/> holds the page exactly. The
    /// background is filled first unless it is transparent.
    /// </summary>
    public void RenderTo(Canvas canvas, PdfRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        options ??= PdfRenderOptions.Default;
        Draw(canvas, RenderGeometry.Of(this, options).PageToDevice, options, background: true);
    }

    private void Draw(Canvas canvas, Matrix3x2 pageToDevice, PdfRenderOptions options, bool background)
    {
        // Fonts that drawing on this document embedded are written only on save; a render needs their glyphs now.
        Document.WrittenResources.Finish();
        RenderSettings settings = RenderSettings.From(options);
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

            PageRenderer renderer = new(canvas, device, settings, Document.RenderCache);
            renderer.Render(GetContent(), Resources, Document.Options);
            renderer.RenderAnnotations(this, options.Annotations);
        }
        finally
        {
            canvas.RestoreToCount(saves);
        }
    }
}
