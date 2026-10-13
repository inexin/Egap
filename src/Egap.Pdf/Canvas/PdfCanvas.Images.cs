using System.Globalization;
using System.Security.Cryptography;

using Egami;
using Egami.Drawing;
using Egami.Formats;
using Egami.Operations;
using Egami.Pixels;

using Egap.Pdf.Filters;

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

        PdfReference xObject = Page.Document.WrittenResources.ForInstance(image, () => Page.Document.Objects.Add(JpegStream(image)));
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
        int width = image.Width, height = image.Height;
        bool wide = image.Layout.BitsPerChannel > 8;
        int bytes = wide ? 2 : 1;
        byte[] color = new byte[width * height * 3 * bytes];
        byte[] alpha = new byte[width * height * bytes];
        bool hasAlpha = false, gray = true;
        if (wide)
        {
            using Raster<Rgba16> pixels = image.As<Rgba16>();
            for (int y = 0; y < height; y++)
            {
                Span<Rgba16> row = pixels.Row(y);
                for (int x = 0; x < width; x++)
                {
                    Rgba16 p = row[x];
                    int i = (y * width) + x;
                    Write16(color, (i * 3) + 0, p.R);
                    Write16(color, (i * 3) + 1, p.G);
                    Write16(color, (i * 3) + 2, p.B);
                    Write16(alpha, i, p.A);
                    hasAlpha |= p.A != ushort.MaxValue;
                    gray &= p.R == p.G && p.G == p.B;
                }
            }
        }
        else
        {
            using Raster<Rgba8> pixels = image.As<Rgba8>();
            for (int y = 0; y < height; y++)
            {
                Span<Rgba8> row = pixels.Row(y);
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
        }

        // An ICC profile stays with the colours it describes: gray samples keep an RGB profile's three components.
        PdfObject? profile = Profile(image.Metadata.IccProfile, out int profileComponents);
        int components = profileComponents == 3 ? 3 : profileComponents == 1 || gray ? 1 : 3;
        if (components == 1 && profileComponents != 1)
        {
            profile = null;
        }

        if (components == 1)
        {
            byte[] samples = new byte[width * height * bytes];
            for (int i = 0; i < width * height; i++)
            {
                samples[i * bytes] = color[i * 3 * bytes];
                if (wide)
                {
                    samples[(i * bytes) + 1] = color[(i * 3 * bytes) + 1];
                }
            }

            color = samples;
        }

        PdfDocument document = Page.Document;
        PdfObject space = profile ?? PdfName.Get(components == 1 ? "DeviceGray" : "DeviceRGB");
        PdfStream stream = ImageStream(color, width, height, components, space, wide ? 16 : 8);
        if (hasAlpha)
        {
            stream.Dictionary["SMask"] = document.Objects.Add(ImageStream(alpha, width, height, 1, PdfName.Get("DeviceGray"), wide ? 16 : 8));
        }

        return document.Objects.Add(stream);
    }

    private static void Write16(byte[] target, int index, ushort value)
    {
        target[index * 2] = (byte)(value >> 8);
        target[(index * 2) + 1] = (byte)value;
    }

    /// <summary>
    /// The <c>[/ICCBased stream]</c> colour space for an RGB or gray ICC profile, the stream shared by every image with the
    /// same profile; <see langword="null"/> for none or another kind of profile.
    /// </summary>
    private PdfArray? Profile(ReadOnlyMemory<byte> icc, out int components)
    {
        components = 0;
        if (icc.Length < 132)
        {
            return null;
        }

        ReadOnlySpan<byte> signature = icc.Span.Slice(16, 4);
        components = signature.SequenceEqual("RGB "u8) ? 3 : signature.SequenceEqual("GRAY"u8) ? 1 : 0;
        if (components == 0)
        {
            return null;
        }

        int n = components;
        string key = Convert.ToHexString(SHA256.HashData(icc.Span));
        PdfReference stream = Page.Document.WrittenResources.ForValue(("ICCBased", key), () =>
        {
            PdfStream profile = new(new PdfDictionary { ["N"] = new PdfInteger(n) });
            profile.SetDecodedBytes(icc.Span);
            return Page.Document.Objects.Add(profile);
        });
        return [PdfName.Get("ICCBased"), stream];
    }

    /// <summary>A JPEG's bytes as stored, in an image XObject with <c>/DCTDecode</c>.</summary>
    private static PdfStream JpegStream(EncodedImage image)
    {
        PdfStream stream = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(image.Width),
            ["Height"] = new PdfInteger(image.Height),
            ["ColorSpace"] = PdfName.Get(image.Info.Layout.ChannelOrder == PixelChannelOrder.Gray ? "DeviceGray" : "DeviceRGB"),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["Filter"] = PdfName.Get("DCTDecode"),
        });
        stream.SetEncodedBytes(image.Data.Span);
        return stream;
    }

    /// <summary>An image XObject of 8- or 16-bit samples, Flate-compressed with the PNG Up predictor on every row.</summary>
    private static PdfStream ImageStream(byte[] samples, int width, int height, int components, PdfObject space, int bits)
    {
        int stride = width * components * (bits / 8);
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
            ["ColorSpace"] = space,
            ["BitsPerComponent"] = new PdfInteger(bits),
            ["Filter"] = PdfName.Get("FlateDecode"),
            ["DecodeParms"] = new PdfDictionary
            {
                ["Predictor"] = new PdfInteger(12),
                ["Colors"] = new PdfInteger(components),
                ["BitsPerComponent"] = new PdfInteger(bits),
                ["Columns"] = new PdfInteger(width),
            },
        });
        stream.SetEncodedBytes(FlateFilter.Encode(predicted));
        return stream;
    }
}
