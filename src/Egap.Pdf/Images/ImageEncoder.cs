using System.Security.Cryptography;

using Egami;
using Egami.Pixels;

using Egap.Pdf.Filters;

namespace Egap.Pdf.Images;

/// <summary>
/// Writes rasters as image XObjects, for the canvas and for image replacement: 8- or 16-bit samples, gray when every pixel
/// is, the raster's RGB or gray ICC profile as ICCBased, alpha as a soft mask, the PNG Up predictor and Flate; or a JPEG's
/// bytes as stored with DCTDecode.
/// </summary>
internal static class ImageEncoder
{
    public static PdfStream Encode(PdfDocument document, Raster image)
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
        PdfObject? profile = Profile(document, image.Metadata.IccProfile, out int profileComponents);
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

        PdfObject space = profile ?? PdfName.Get(components == 1 ? "DeviceGray" : "DeviceRGB");
        PdfStream stream = ImageStream(color, width, height, components, space, wide ? 16 : 8);
        if (hasAlpha)
        {
            stream.Dictionary["SMask"] = document.Objects.Add(ImageStream(alpha, width, height, 1, PdfName.Get("DeviceGray"), wide ? 16 : 8));
        }

        return stream;
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
    private static PdfArray? Profile(PdfDocument document, ReadOnlyMemory<byte> icc, out int components)
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
        PdfReference stream = document.WrittenResources.ForValue(("ICCBased", key), () =>
        {
            PdfStream profile = new(new PdfDictionary { ["N"] = new PdfInteger(n) });
            profile.SetDecodedBytes(icc.Span);
            return document.Objects.Add(profile);
        });
        return [PdfName.Get("ICCBased"), stream];
    }

    /// <summary>
    /// A raster as a JPEG image XObject at a quality: gray when every pixel is, else RGB; alpha as a Flate soft mask.
    /// </summary>
    public static PdfStream EncodeJpeg(PdfDocument document, Raster image, int quality)
    {
        using Raster<Rgba8> pixels = image.As<Rgba8>();
        bool gray = true, hasAlpha = false;
        byte[] alpha = new byte[pixels.Width * pixels.Height];
        for (int y = 0; y < pixels.Height; y++)
        {
            Span<Rgba8> row = pixels.Row(y);
            for (int x = 0; x < pixels.Width; x++)
            {
                gray &= row[x].R == row[x].G && row[x].G == row[x].B;
                hasAlpha |= row[x].A != 255;
                alpha[(y * pixels.Width) + x] = row[x].A;
            }
        }

        using MemoryStream jpeg = new();
        Egami.Formats.Jpeg.JpegWriterOptions options = new() { Quality = quality };
        if (gray)
        {
            using Raster<Gray8> g = pixels.As<Gray8>();
            g.Save(jpeg, Egami.Formats.Jpeg.JpegFormat.Info, options);
        }
        else
        {
            using Raster<Rgb8> rgb = pixels.As<Rgb8>();
            rgb.Save(jpeg, Egami.Formats.Jpeg.JpegFormat.Info, options);
        }

        PdfStream stream = JpegStream(jpeg.ToArray(), pixels.Width, pixels.Height, gray);
        if (hasAlpha)
        {
            stream.Dictionary["SMask"] = document.Objects.Add(ImageStream(alpha, pixels.Width, pixels.Height, 1, PdfName.Get("DeviceGray"), 8));
        }

        return stream;
    }

    /// <summary>
    /// JPEG bytes as stored, their size and colour from the frame header: gray, RGB, or CMYK with <c>/Decode</c> inverting
    /// the inks when an Adobe marker says they are stored inverted.
    /// </summary>
    public static PdfStream JpegStream(ReadOnlySpan<byte> jpeg)
    {
        (int width, int height, int components) = JpegFrame(jpeg);
        if (components is not (1 or 3 or 4) || width <= 0 || height <= 0)
        {
            throw new ArgumentException("The bytes are not a JPEG with a gray, RGB or CMYK frame.", nameof(jpeg));
        }

        PdfStream stream = JpegStream(jpeg, width, height, components == 1);
        if (components == 4)
        {
            stream.Dictionary["ColorSpace"] = PdfName.Get("DeviceCMYK");
            if (jpeg.IndexOf("Adobe"u8) >= 0)
            {
                stream.Dictionary["Decode"] = new PdfArray { new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0) };
            }
        }

        return stream;
    }

    /// <summary>A JPEG's width, height and component count from its first frame header (SOF0 to SOF15).</summary>
    private static (int Width, int Height, int Components) JpegFrame(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return default;
        }

        int at = 2;
        while (at + 4 <= jpeg.Length)
        {
            if (jpeg[at] != 0xFF)
            {
                at++;
                continue;
            }

            byte marker = jpeg[at + 1];
            if (marker is 0xFF or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += marker == 0xFF ? 1 : 2;
                continue;
            }

            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC) && at + 10 <= jpeg.Length)
            {
                return ((jpeg[at + 7] << 8) | jpeg[at + 8], (jpeg[at + 5] << 8) | jpeg[at + 6], jpeg[at + 9]);
            }

            at += 2 + length;
        }

        return default;
    }

    /// <summary>A JPEG's bytes as stored, in an image XObject with <c>/DCTDecode</c>.</summary>
    public static PdfStream JpegStream(ReadOnlySpan<byte> jpeg, int width, int height, bool gray)
    {
        PdfStream stream = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(width),
            ["Height"] = new PdfInteger(height),
            ["ColorSpace"] = PdfName.Get(gray ? "DeviceGray" : "DeviceRGB"),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["Filter"] = PdfName.Get("DCTDecode"),
        });
        stream.SetEncodedBytes(jpeg);
        return stream;
    }

    /// <summary>An image XObject of 8- or 16-bit samples, Flate-compressed with the PNG Up predictor on every row.</summary>
    public static PdfStream ImageStream(byte[] samples, int width, int height, int components, PdfObject space, int bits)
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
