using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Images (8.9): XObjects and inline images decode through Phase 3's decoder (colour spaces, decode arrays, soft, explicit
/// and colour key masks) and fill the unit square of user space. A stencil mask paints the fill colour through its mask with
/// <see cref="Canvas.DrawImageMask"/>. JPEG and JPEG 2000 images drawn small decode at a reduced size, and decoded images
/// are cached on the document under a budget (design Q14).
/// </summary>
internal sealed partial class PageRenderer
{
    /// <summary>Image space has its first row at the top of the unit square: y runs down from 1 to 0.</summary>
    private static readonly PdfMatrix ImageSpace = new(1, 0, 0, -1, 0, 1);

    protected override void OnImage(PdfStream? image, PdfInlineImage? inlineImage, PdfName? name)
    {
        PdfGraphicsState state = State;
        PdfImage source = image is not null ? new PdfImage(image, Resources) : new PdfImage(inlineImage!, Resources);
        if (Device(state.Matrix) is not Matrix3x2 device)
        {
            return;
        }

        // How large the unit square is on the device, for the reduction and the sampling.
        float wide = new Vector2(device.M11, device.M12).Length(), high = new Vector2(device.M21, device.M22).Length();
        int reduction = Reduction(source, wide, high);
        PdfImageOptions options = reduction == 1 ? PdfImageOptions.Default : new PdfImageOptions
        {
            MinimumSize = new Size((source.Width + reduction - 1) / reduction, (source.Height + reduction - 1) / reduction),
        };

        Raster raster;
        try
        {
            raster = image is not null
                ? cache.Images.GetOrDecode(image, reduction, settings.ImageCacheLimit, () => source.ToRaster(options))
                : source.ToRaster(options);
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            Warn(PdfWarningCode.MalformedContent, $"The image {(name is null ? "inline" : "/" + name.Value)} could not be drawn: {e.Message}");
            return;
        }

        if (!SetUp(state.Clips, 0, ImageSpace * state.Matrix))
        {
            return;
        }

        bool interpolate = source.Interpolate || wide < raster.Width || high < raster.Height;
        SamplingOptions sampling = new() { Filter = !settings.AntialiasImages ? ResampleFilter.Nearest : interpolate ? ResampleFilter.Bilinear : ResampleFilter.Nearest };
        RectF unit = new(0, 0, 1, 1);
        if (source.IsStencilMask)
        {
            if (ColorPaint(state.FillColor, state.FillAlpha) is SolidPaint paint)
            {
                canvas.DrawImageMask(raster, unit, paint with { Antialias = settings.AntialiasImages }, sampling);
            }

            return;
        }

        canvas.DrawImage(raster, unit, sampling, state.FillAlpha < 1 ? new SolidPaint(new RgbaF32(1, 1, 1, 1)) { Opacity = (float)state.FillAlpha } : null);
    }

    /// <summary>
    /// The power of two a JPEG or JPEG 2000 image may be reduced by and still have a pixel for each device pixel; 1 for other
    /// images, which the canvas resamples (design Q14).
    /// </summary>
    private static int Reduction(PdfImage image, float wide, float high)
    {
        if (image.ImageCodec?.Value is not ("DCTDecode" or "JPXDecode") || image.Width <= 0 || image.Height <= 0)
        {
            return 1;
        }

        int reduction = 1;
        while (reduction < 8 && image.Width / (reduction * 2f) >= wide && image.Height / (reduction * 2f) >= high)
        {
            reduction *= 2;
        }

        return reduction;
    }
}
