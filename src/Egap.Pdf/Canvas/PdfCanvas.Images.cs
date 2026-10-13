using Egami;
using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Egap.Pdf.Filters;

namespace Egap.Pdf;

/// <summary>Images on the PDF canvas: rasters become image XObjects, written once per raster however often they are drawn.</summary>
public sealed partial class PdfCanvas
{
    /// <summary>Draws part of a raster into a local rectangle: the whole image is placed and clipped to the part.</summary>
    private void DrawRaster(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (paint is not null && (paint.Blur is not null || !PdfBlendModes.ContainsKey(paint.BlendMode)))
        {
            Unsupported(paint, c => c.DrawImage(image, source, destination, sampling, paint));
            return;
        }

        PdfName name = Page.AddResource("XObject", ImageXObject(image));
        GraphicsGroup g = OpenGroup();
        SetAlpha(g, paint?.Opacity ?? 1, paint?.BlendMode ?? BlendMode.SourceOver);

        float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
        float width = image.Width * sx, height = image.Height * sy;
        float x = destination.X - (source.X * sx), y = destination.Y - (source.Y * sy);
        bool whole = source == new RectF(0, 0, image.Width, image.Height);
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
        PdfName name = Page.AddResource("XObject", ImageXObject(image));
        content.Save().Transform(device.Width, 0, 0, -device.Height, device.X, device.Y + device.Height).Draw(name).Restore();
    }

    /// <summary>
    /// Draws a call that PDF cannot express into an image at the fallback resolution and places it over the device clip
    /// bounds (Phase 3 design Q14), with a warning.
    /// </summary>
    private void Fallback(Action<Canvas> draw, string what)
    {
        FallbackImage? image = RasterizeFallback(draw, options.FallbackDpi / 72f);
        Warn(PdfWarningCode.RasterFallback, $"PDF cannot express {what}; it was drawn as an image at {options.FallbackDpi} dpi.");
        if (image is not null)
        {
            using Raster<RgbaF32> pixels = image.Image;
            PlaceDeviceImage(pixels, image.DeviceBounds);
        }
    }

    /// <summary>The raster's image XObject, written the first time it is drawn: 8-bit RGB or gray with alpha as a soft mask.</summary>
    private PdfReference ImageXObject(Raster image)
    {
        return Page.Document.WrittenResources.ForInstance(image, () => WriteImage(image));
    }

    private PdfReference WriteImage(Raster image)
    {
        using Raster<Rgba8> rgba = image.As<Rgba8>();
        int width = rgba.Width, height = rgba.Height;
        byte[] color = new byte[width * height * 3];
        byte[] alpha = new byte[width * height];
        bool hasAlpha = false, gray = true;
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = rgba.Row(y);
            for (int x = 0; x < width; x++)
            {
                Rgba8 p = row[x];
                int i = (y * width) + x;
                color[(i * 3) + 0] = p.R;
                color[(i * 3) + 1] = p.G;
                color[(i * 3) + 2] = p.B;
                alpha[i] = p.A;
                hasAlpha |= p.A != 255;
                gray &= p.R == p.G && p.G == p.B;
            }
        }

        if (gray)
        {
            byte[] g = new byte[width * height];
            for (int i = 0; i < g.Length; i++)
            {
                g[i] = color[i * 3];
            }

            color = g;
        }

        PdfDocument document = Page.Document;
        PdfStream stream = ImageStream(color, width, height, gray ? 1 : 3, gray ? "DeviceGray" : "DeviceRGB");
        if (hasAlpha)
        {
            stream.Dictionary["SMask"] = document.Objects.Add(ImageStream(alpha, width, height, 1, "DeviceGray"));
        }

        return document.Objects.Add(stream);
    }

    /// <summary>An image XObject of 8-bit samples, Flate-compressed with the PNG Up predictor on every row.</summary>
    private static PdfStream ImageStream(byte[] samples, int width, int height, int components, string space)
    {
        int stride = width * components;
        byte[] predicted = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1);
            predicted[row] = 2;
            for (int x = 0; x < stride; x++)
            {
                byte above = y > 0 ? samples[((y - 1) * stride) + x] : (byte)0;
                predicted[row + 1 + x] = (byte)(samples[(y * stride) + x] - above);
            }
        }

        PdfStream stream = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(width),
            ["Height"] = new PdfInteger(height),
            ["ColorSpace"] = PdfName.Get(space),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["Filter"] = PdfName.Get("FlateDecode"),
            ["DecodeParms"] = new PdfDictionary
            {
                ["Predictor"] = new PdfInteger(12),
                ["Colors"] = new PdfInteger(components),
                ["Columns"] = new PdfInteger(width),
            },
        });
        stream.SetEncodedBytes(FlateFilter.Encode(predicted));
        return stream;
    }

    /// <summary>Paints other than a solid colour: shadings and patterns (task 9) until then fall back.</summary>
    private static bool BeginShaded(Paint paint, Stroke? stroke)
    {
        _ = paint;
        _ = stroke;
        return false;
    }
}
