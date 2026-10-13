using System.Globalization;
using System.Security.Cryptography;

using Egami;
using Egami.Drawing;
using Egami.Formats;
using Egami.Operations;
using Egami.Pixels;

using Egap.Pdf.Filters;
using Egap.Pdf.Images;

namespace Egap.Pdf;

/// <summary>
/// Images on the PDF canvas. A raster becomes an image XObject written once however often it is drawn: 8 or 16 bits as the
/// raster has them, gray when every pixel is, alpha as a soft mask, and the raster's ICC profile as an ICCBased colour space
/// when it is an RGB or gray profile. A JPEG drawn as an <see cref="EncodedImage"/> is embedded as it is stored, with
/// <c>/DCTDecode</c>, when it is a plain 8-bit gray or colour JPEG (Phase 3 task 7's hook); anything else is decoded first.
/// </summary>
public sealed partial class PdfCanvas
{
    /// <inheritdoc/>
    protected override void OnDrawEncodedImage(EncodedImage image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (Forward(c => c.DrawImage(image, source, destination, sampling, paint)))
        {
            return;
        }

        RasterInfo info = image.Info;
        bool passthrough = info.Format.Name == "JPEG" && info.Layout.BitsPerChannel == 8 && info.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.Rgb
            && info.Orientation == Egami.Metadata.ImageOrientation.Normal;
        if (!passthrough || (paint is not null && (paint.Blur is not null || !PdfBlendModes.ContainsKey(paint.BlendMode))))
        {
            base.OnDrawEncodedImage(image, source, destination, sampling, paint);
            return;
        }

        PdfReference xObject = Page.Document.WrittenResources.ForInstance(image, () => Page.Document.Objects.Add(ImageEncoder.JpegStream(image.Data.Span, image.Width, image.Height, image.Info.Layout.ChannelOrder == PixelChannelOrder.Gray)));
        PlaceImage(xObject, image.Width, image.Height, source, destination, paint);
    }

    /// <summary>Draws part of a raster into a local rectangle: the whole image is placed and clipped to the part.</summary>
    private void DrawRaster(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (paint is not null && (paint.Blur is not null || !PdfBlendModes.ContainsKey(paint.BlendMode)))
        {
            Unsupported(paint, (c, p) => c.DrawImage(image, source, destination, sampling, p));
            return;
        }

        PlaceImage(ImageXObject(image), image.Width, image.Height, source, destination, paint);
    }

    /// <summary>Places an image XObject so that its <paramref name="source"/> part fills <paramref name="destination"/>.</summary>
    private void PlaceImage(PdfReference xObject, int imageWidth, int imageHeight, RectF source, RectF destination, Paint? paint)
    {
        PdfName name = AddResource("XObject", xObject);
        GraphicsGroup g = OpenGroup();
        SetAlpha(g, paint?.Opacity ?? 1, paint?.BlendMode ?? BlendMode.SourceOver);

        float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
        float width = imageWidth * sx, height = imageHeight * sy;
        float x = destination.X - (source.X * sx), y = destination.Y - (source.Y * sy);
        bool whole = source == new RectF(0, 0, imageWidth, imageHeight);
        content.Save();
        if (!whole)
        {
            content.Rectangle(destination.X, destination.Y, destination.Width, destination.Height).Clip().EndPath();
        }

        // The image fills the unit square with its first row at the top; the canvas is y down.
        content.Transform(width, 0, 0, -height, x, y + height).Draw(name).Restore();
    }

    /// <summary>Places an image over a device rectangle, outside any transform group: for fallbacks.</summary>
    private void PlaceDeviceImage(Raster image, RectF device)
    {
        CloseGroup();
        PdfName name = AddResource("XObject", ImageXObject(image));
        content.Save().Transform(device.Width, 0, 0, -device.Height, device.X, device.Y + device.Height).Draw(name).Restore();
    }

    /// <summary>
    /// Draws a call that PDF cannot express into an image at <paramref name="scale"/> pixels per unit and places it over the
    /// device clip bounds (Phase 3 design Q14), with a warning. Only the part of the image the call drew on is kept, so a small
    /// blurred shape on a large page is a small image.
    /// </summary>
    private void Fallback(Action<Canvas> draw, string what, float scale)
    {
        FallbackImage? image = RasterizeFallback(draw, scale);
        float dpi = scale * 72;
        Warn(PdfWarningCode.RasterFallback, string.Create(CultureInfo.InvariantCulture, $"PDF cannot express {what}; it was drawn as an image at {dpi:0.#} dpi."));
        if (image is null)
        {
            return;
        }

        using Raster<RgbaF32> pixels = image.Image;
        if (Drawn(pixels) is not Rect drawn)
        {
            return;
        }

        // The drawn part widened to whole device units, so the part's edges fall on the page's pixel grid at 72 dpi, as the
        // whole image's do: viewers resample an image placed between pixels a little differently.
        RectF bounds = image.DeviceBounds;
        float unit = bounds.Width / pixels.Width;
        float left = MathF.Max(bounds.Left, MathF.Floor(bounds.X + (drawn.X * unit)));
        float top = MathF.Max(bounds.Top, MathF.Floor(bounds.Y + (drawn.Y * unit)));
        float right = MathF.Min(bounds.Right, MathF.Ceiling(bounds.X + ((drawn.X + drawn.Width) * unit)));
        float bottom = MathF.Min(bounds.Bottom, MathF.Ceiling(bounds.Y + ((drawn.Y + drawn.Height) * unit)));
        int x0 = Math.Clamp((int)MathF.Round((left - bounds.X) / unit), 0, pixels.Width);
        int y0 = Math.Clamp((int)MathF.Round((top - bounds.Y) / unit), 0, pixels.Height);
        int x1 = Math.Clamp((int)MathF.Round((right - bounds.X) / unit), x0, pixels.Width);
        int y1 = Math.Clamp((int)MathF.Round((bottom - bounds.Y) / unit), y0, pixels.Height);
        RectF device = new(left, top, right - left, bottom - top);
        if (x1 == x0 || y1 == y0)
        {
            (x0, y0, x1, y1, device) = (0, 0, pixels.Width, pixels.Height, bounds);
        }

        // Eight bits a channel: the fallback stands for drawing on an 8-bit page, and 16 would double the bytes.
        using Raster<Rgba8> part = Raster.Create<Rgba8>(x1 - x0, y1 - y0);
        for (int y = y0; y < y1; y++)
        {
            ReadOnlySpan<RgbaF32> from = pixels.Row(y).Slice(x0, x1 - x0);
            Span<Rgba8> to = part.Row(y - y0);
            for (int x = 0; x < from.Length; x++)
            {
                to[x] = from[x].ToRgba8();
            }
        }

        PlaceDeviceImage(part, device);
    }

    /// <summary>The smallest rectangle holding every pixel with any alpha, or <see langword="null"/> when nothing was drawn.</summary>
    private static Rect? Drawn(Raster<RgbaF32> pixels)
    {
        int left = pixels.Width, top = -1, right = -1, bottom = -1;
        for (int y = 0; y < pixels.Height; y++)
        {
            ReadOnlySpan<RgbaF32> row = pixels.Row(y);
            int first = -1, last = -1;
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x].A > 0)
                {
                    first = first < 0 ? x : first;
                    last = x;
                }
            }

            if (first >= 0)
            {
                top = top < 0 ? y : top;
                bottom = y;
                left = Math.Min(left, first);
                right = Math.Max(right, last);
            }
        }

        return top < 0 ? null : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>The raster's image XObject, written the first time it is drawn.</summary>
    private PdfReference ImageXObject(Raster image)
    {
        return Page.Document.WrittenResources.ForInstance(image, () => WriteImage(image));
    }

    private PdfReference WriteImage(Raster image)
    {
        return Page.Document.Objects.Add(ImageEncoder.Encode(Page.Document, image));
    }
}
