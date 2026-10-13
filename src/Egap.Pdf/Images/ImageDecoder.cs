using System.Buffers.Binary;

using Egami;
using Egami.Formats;
using Egami.Formats.Jbig2;
using Egami.Formats.Jpeg;
using Egami.Formats.Jpeg2000;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egap.Pdf.Images;

/// <summary>
/// Decodes an image to an Egami raster (Phase 3 design Q11): the stream's filters, then its image codec through Egami
/// (DCT, JPX, JBIG2 with its globals, CCITT through a TIFF wrapper), then the samples unpacked at 1 to 16 bits, the
/// <c>/Decode</c> array applied, the colour space converted to sRGB (or kept for gray, RGB and CMYK when asked), and the
/// soft mask, explicit mask, colour key mask or <c>/SMaskInData</c> alpha applied. Anything that cannot be decoded throws
/// <see cref="PdfException"/>.
/// </summary>
internal static class ImageDecoder
{
    /// <summary>The most pixels an image may have, against dimensions that would exhaust memory.</summary>
    private const long MaxPixels = 1L << 28;

    public static Raster Decode(PdfImage image, PdfImageOptions options)
    {
        try
        {
            return DecodeCore(image, options, 0);
        }
        catch (Exception e) when (e is InvalidDataException or EgamiException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            throw new PdfException($"The image could not be decoded: {e.Message}", e);
        }
    }

    private static Raster DecodeCore(PdfImage image, PdfImageOptions options, int depth)
    {
        PdfDictionary d = image.Parameters;
        (Samples samples, ImageColorSpace? ownSpace, byte[]? ownAlpha) = ReadSamples(image, d);
        int width = samples.Width, height = samples.Height;
        if (image.IsStencilMask)
        {
            // A stencil mask paints the current colour where its decoded sample is 0 (8.9.6.2); alone it is black on clear.
            bool inverted = Numbers(d.Get<PdfArray>("Decode")) is [> 0.5, ..];
            Raster<Rgba8> stencil = Raster.Create<Rgba8>(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<Rgba8> row = stencil.Row(y);
                for (int x = 0; x < width; x++)
                {
                    bool paints = (samples.Get(x, y, 0) == 0) ^ inverted;
                    row[x] = new Rgba8(0, 0, 0, paints ? (byte)255 : (byte)0);
                }
            }

            return stencil;
        }

        ImageColorSpace space = ownSpace ?? ImageColorSpace.Read(d.Get("ColorSpace"), image.Resources, options.CmykProfile);
        int n = space.Components;
        if (samples.Components < n)
        {
            throw new InvalidDataException($"The image has {samples.Components} components where its colour space needs {n}.");
        }

        // An Indexed JPEG 2000 image's indices come scaled from their precision to 8 bits; its default range scales them back.
        double[] decode = Numbers(d.Get<PdfArray>("Decode")) is { } given && given.Length >= 2 * n ? given
            : space.Family == ColorFamily.Indexed && samples.SourceBits < samples.Bits ? [0, (1 << samples.SourceBits) - 1]
            : space.DefaultDecode(samples.Bits);
        double max = (1 << samples.Bits) - 1;

        // Alpha: SMask, then /Mask (a stencil stream or colour key ranges), then the JPX image's own.
        byte[]? alpha = null;
        double[]? matte = null;
        if (options.ApplyMasks && depth == 0)
        {
            if (d.Get("SMask")?.Resolve() is PdfStream soft && soft.Dictionary.GetName("Subtype")?.Value == "Image")
            {
                alpha = MaskAlpha(new PdfImage(soft, image.Resources), width, height, options, stencil: false);
                matte = Numbers(soft.Dictionary.Get<PdfArray>("Matte"));
            }
            else if (d.Get("Mask")?.Resolve() is PdfStream mask)
            {
                alpha = MaskAlpha(new PdfImage(mask, image.Resources), width, height, options, stencil: true);
            }
            else if (d.Get("Mask")?.Resolve() is PdfArray keys && Numbers(keys) is { } ranges && ranges.Length >= 2 * n)
            {
                alpha = new byte[width * height];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        bool inside = true;
                        for (int c = 0; c < n && inside; c++)
                        {
                            int v = samples.Get(x, y, c);
                            inside = v >= ranges[2 * c] && v <= ranges[(2 * c) + 1];
                        }

                        alpha[(y * width) + x] = inside ? (byte)0 : (byte)255;
                    }
                }
            }
            else if (ownAlpha is not null)
            {
                alpha = ownAlpha;
            }
        }

        bool keep = !options.ConvertColor && space.Family is ColorFamily.Gray or ColorFamily.Rgb or ColorFamily.Cmyk && matte is null;
        return keep ? Native(samples, space, decode, alpha) : Srgb(samples, space, decode, max, alpha, matte);
    }

    /// <summary>The image in sRGB: 16 bits a channel for 16-bit samples, else 8, with alpha when a mask gives it.</summary>
    private static Raster Srgb(Samples samples, ImageColorSpace space, double[] decode, double max, byte[]? alpha, double[]? matte)
    {
        int width = samples.Width, height = samples.Height, n = space.Components;
        bool wide = samples.Bits == 16;
        Raster<Rgba8>? narrow = wide ? null : Raster.Create<Rgba8>(width, height);
        Raster<Rgba16>? deep = wide ? Raster.Create<Rgba16>(width, height) : null;

        // One-component spaces at up to 8 bits convert through a table of every sample value.
        double[][]? table = n == 1 && samples.Bits <= 8 && matte is null ? Table(space, decode, samples.Bits) : null;
        double[] components = new double[n];
        double[] rgb = new double[3];
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row8 = narrow is not null ? narrow.Row(y) : default;
            Span<Rgba16> row16 = deep is not null ? deep.Row(y) : default;
            for (int x = 0; x < width; x++)
            {
                double a = alpha is null ? 1 : alpha[(y * width) + x] / 255.0;
                double[] color;
                if (table is not null)
                {
                    color = table[samples.Get(x, y, 0)];
                }
                else
                {
                    for (int c = 0; c < n; c++)
                    {
                        double v = decode[2 * c] + (samples.Get(x, y, c) * (decode[(2 * c) + 1] - decode[2 * c]) / max);
                        if (matte is not null && c < matte.Length && a > 0)
                        {
                            // Undo the premultiplication against the matte colour (11.6.5.3).
                            v = matte[c] + ((v - matte[c]) / a);
                        }

                        components[c] = v;
                    }

                    space.ToRgb(components, rgb);
                    color = rgb;
                }

                if (wide)
                {
                    row16[x] = new Rgba16(Unit16(color[0]), Unit16(color[1]), Unit16(color[2]), Unit16(a));
                }
                else
                {
                    row8[x] = new Rgba8(Unit8(color[0]), Unit8(color[1]), Unit8(color[2]), Unit8(a));
                }
            }
        }

        return (Raster?)narrow ?? deep!;
    }

    private static double[][] Table(ImageColorSpace space, double[] decode, int bits)
    {
        int count = 1 << bits;
        double max = count - 1;
        double[][] table = new double[count][];
        for (int v = 0; v < count; v++)
        {
            double[] rgb = new double[3];
            space.ToRgb([decode[0] + (v * (decode[1] - decode[0]) / max)], rgb);
            table[v] = rgb;
        }

        return table;
    }

    /// <summary>The image with its gray, RGB or CMYK components kept: Gray, GrayAlpha, Rgb, Rgba or Cmyk, at 8 or 16 bits.</summary>
    private static Raster Native(Samples samples, ImageColorSpace space, double[] decode, byte[]? alpha)
    {
        int width = samples.Width, height = samples.Height, n = space.Components;
        bool wide = samples.Bits == 16;
        double max = (1 << samples.Bits) - 1;
        double Component(int x, int y, int c) => Math.Clamp(decode[2 * c] + (samples.Get(x, y, c) * (decode[(2 * c) + 1] - decode[2 * c]) / max), 0, 1);
        byte A8(int x, int y) => alpha is null ? (byte)255 : alpha[(y * width) + x];
        switch (space.Family, alpha is not null, wide)
        {
            case (ColorFamily.Gray, false, false):
                return Fill<Gray8>(width, height, (x, y) => new Gray8(Unit8(Component(x, y, 0))));
            case (ColorFamily.Gray, false, true):
                return Fill<Gray16>(width, height, (x, y) => new Gray16(Unit16(Component(x, y, 0))));
            case (ColorFamily.Gray, true, false):
                return Fill<GrayAlpha8>(width, height, (x, y) => new GrayAlpha8(Unit8(Component(x, y, 0)), A8(x, y)));
            case (ColorFamily.Gray, true, true):
                return Fill<GrayAlpha16>(width, height, (x, y) => new GrayAlpha16(Unit16(Component(x, y, 0)), (ushort)(A8(x, y) * 257)));
            case (ColorFamily.Rgb, false, false):
                return Fill<Rgb8>(width, height, (x, y) => new Rgb8(Unit8(Component(x, y, 0)), Unit8(Component(x, y, 1)), Unit8(Component(x, y, 2))));
            case (ColorFamily.Rgb, false, true):
                return Fill<Rgb16>(width, height, (x, y) => new Rgb16(Unit16(Component(x, y, 0)), Unit16(Component(x, y, 1)), Unit16(Component(x, y, 2))));
            case (ColorFamily.Rgb, true, false):
                return Fill<Rgba8>(width, height, (x, y) => new Rgba8(Unit8(Component(x, y, 0)), Unit8(Component(x, y, 1)), Unit8(Component(x, y, 2)), A8(x, y)));
            case (ColorFamily.Rgb, true, true):
                return Fill<Rgba16>(width, height, (x, y) => new Rgba16(Unit16(Component(x, y, 0)), Unit16(Component(x, y, 1)), Unit16(Component(x, y, 2)), (ushort)(A8(x, y) * 257)));
            case (_, _, false):
                // CMYK keeps its inks; Egami has no CMYK with alpha, so a mask is not applied here.
                return Fill<Cmyk8>(width, height, (x, y) => new Cmyk8(Unit8(Component(x, y, 0)), Unit8(Component(x, y, 1)), Unit8(Component(x, y, 2)), Unit8(Component(x, y, 3))));
            default:
                return Fill<Cmyk16>(width, height, (x, y) => new Cmyk16(Unit16(Component(x, y, 0)), Unit16(Component(x, y, 1)), Unit16(Component(x, y, 2)), Unit16(Component(x, y, 3))));
        }
    }

    private static Raster<TPixel> Fill<TPixel>(int width, int height, Func<int, int, TPixel> pixel)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<TPixel> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = pixel(x, y);
            }
        }

        return raster;
    }

    /// <summary>A mask image as alpha the size of the image it masks, sampled at the nearest pixel.</summary>
    private static byte[] MaskAlpha(PdfImage mask, int width, int height, PdfImageOptions options, bool stencil)
    {
        PdfImageOptions plain = options with { ApplyMasks = false, ConvertColor = true };
        Raster raster = DecodeCore(mask, plain, 1);
        using Raster<Rgba8> rgba = raster as Raster<Rgba8> ?? raster.As<Rgba8>();
        if (!ReferenceEquals(rgba, raster))
        {
            raster.Dispose();
        }

        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = rgba.Row((int)((long)y * rgba.Height / height));
            for (int x = 0; x < width; x++)
            {
                Rgba8 p = row[(int)((long)x * rgba.Width / width)];

                // A stencil mask decodes to black where it paints; a soft mask's gray is its alpha.
                alpha[(y * width) + x] = stencil ? p.A : p.R;
            }
        }

        return alpha;
    }

    /// <summary>The image's samples after its filters and codec, and the colour space and alpha a JPX image brings itself.</summary>
    private static (Samples Samples, ImageColorSpace? Space, byte[]? Alpha) ReadSamples(PdfImage image, PdfDictionary d)
    {
        (byte[] data, PdfName? codec) = image.DecodeFilters();
        int width = (int)(d.GetInt("Width") ?? 0), height = (int)(d.GetInt("Height") ?? 0);
        int bits = image.IsStencilMask ? 1 : (int)(d.GetInt("BitsPerComponent") ?? 8);
        if (codec?.Value != "JPXDecode" && (width <= 0 || height <= 0 || (long)width * height > MaxPixels))
        {
            throw new InvalidDataException($"The image is {width} by {height} pixels.");
        }

        switch (codec?.Value)
        {
            case null:
                if (bits is not (1 or 2 or 4 or 8 or 16))
                {
                    throw new InvalidDataException($"The image has {bits} bits per component.");
                }

                int components = image.IsStencilMask ? 1 : ComponentCount(image, d);
                return (new Samples(width, height, components, bits, data), null, null);
            case "DCTDecode":
                return (FromRaster(Jpeg(data), invertBilevel: false), null, null);
            case "JBIG2Decode":
                Jbig2Globals? globals = d.Get<PdfDictionary>("DecodeParms")?.Get("JBIG2Globals")?.Resolve() is PdfStream g ? Jbig2Globals.Parse(g.GetDecodedBytes()) : null;
                Raster bilevel = Raster.Load(data, Jbig2Format.Info, options: new Jbig2ReaderOptions { Globals = globals });
                return (FromRaster(bilevel, invertBilevel: false, bilevelBits: true), null, null);
            case "CCITTFaxDecode":
                return (Ccitt(data, d, width, height), null, null);
            case "JPXDecode":
                return Jpx(data, d);
            default:
                throw new InvalidDataException($"The image filter {codec.Value} is not supported.");
        }
    }

    /// <summary>
    /// A JPEG's components as stored: gray, RGB (YCbCr converted) or CMYK inks, by its frame header's component count; data
    /// before the start-of-image marker is skipped, as pdf.js does.
    /// </summary>
    private static Raster Jpeg(byte[] data)
    {
        int start = data.AsSpan().IndexOf((byte[])[0xFF, 0xD8, 0xFF]);
        ReadOnlyMemory<byte> jpeg = start > 0 ? data.AsMemory(start) : data;
        return FrameComponents(jpeg.Span) switch
        {
            1 => Raster.Load<Gray8>(jpeg, JpegFormat.Info),
            4 => Raster.Load<Cmyk8>(jpeg, JpegFormat.Info),
            _ => Raster.Load<Rgb8>(jpeg, JpegFormat.Info),
        };
    }

    /// <summary>The component count in a JPEG's first frame header (SOF0 to SOF15, not DHT, JPG or DAC), or 0.</summary>
    private static int FrameComponents(ReadOnlySpan<byte> jpeg)
    {
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

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return at + 9 < jpeg.Length ? jpeg[at + 9] : 0;
            }

            at += 2 + length;
        }

        return 0;
    }

    private static int ComponentCount(PdfImage image, PdfDictionary d)
    {
        return ImageColorSpace.Read(d.Get("ColorSpace"), image.Resources, null).Components;
    }

    /// <summary>JPEG 2000: its own colour space and alpha, unless the PDF names a colour space the components fit.</summary>
    private static (Samples, ImageColorSpace?, byte[]?) Jpx(byte[] data, PdfDictionary d)
    {
        // With a colour space of its own the PDF wants the components as stored (an Indexed image's indices, a CMYK image's
        // inks); without one, the decoder's colour.
        int smaskInData = (int)(d.GetInt("SMaskInData") ?? 0);
        if (d.Get("ColorSpace") is not null)
        {
            Raster stored = Raster.Load(data, Jpeg2000Format.Info, options: new Jpeg2000ReaderOptions { Components = Jpeg2000Components.Raw });
            return (FromRaster(stored, invertBilevel: false) with { SourceBits = Math.Min(8, JpxPrecision(data)) }, null, null);
        }

        Raster raster = Raster.Load(data, Jpeg2000Format.Info);

        // Otherwise the decoder's own colour, as RGBA, with its alpha when the PDF asks for it (or always, without masks to say).
        using Raster<Rgba8> rgba = raster as Raster<Rgba8> ?? raster.As<Rgba8>();
        if (!ReferenceEquals(rgba, raster))
        {
            raster.Dispose();
        }

        byte[] samples = new byte[rgba.Width * rgba.Height * 3];
        byte[] alpha = new byte[rgba.Width * rgba.Height];
        bool hasAlpha = false;
        for (int y = 0; y < rgba.Height; y++)
        {
            Span<Rgba8> row = rgba.Row(y);
            for (int x = 0; x < rgba.Width; x++)
            {
                int i = (y * rgba.Width) + x;
                samples[3 * i] = row[x].R;
                samples[(3 * i) + 1] = row[x].G;
                samples[(3 * i) + 2] = row[x].B;
                alpha[i] = row[x].A;
                hasAlpha |= row[x].A != 255;
            }
        }

        return (new Samples(rgba.Width, rgba.Height, 3, 8, samples), new RgbOnly(), hasAlpha && smaskInData != 0 || hasAlpha && d.Get("SMask") is null ? alpha : null);
    }

    /// <summary>The first component's precision in a JPEG 2000 codestream's SIZ marker, or 8.</summary>
    private static int JpxPrecision(byte[] data)
    {
        int siz = data.AsSpan().IndexOf((byte[])[0xFF, 0x51]);
        return siz >= 0 && siz + 40 < data.Length ? (data[siz + 40] & 0x7F) + 1 : 8;
    }

    /// <summary>CCITT fax data wrapped in a one-strip TIFF for Egami's TIFF reader, the result as PDF samples (1 = white).</summary>
    private static Samples Ccitt(byte[] data, PdfDictionary d, int width, int height)
    {
        PdfDictionary parms = d.Get<PdfDictionary>("DecodeParms") ?? [];
        int k = (int)(parms.GetInt("K") ?? 0);
        int columns = (int)(parms.GetInt("Columns") ?? 1728);
        int rows = (int)(parms.GetInt("Rows") ?? height);
        bool blackIs1 = parms.GetBool("BlackIs1") == true;
        bool aligned = parms.GetBool("EncodedByteAlign") == true;
        rows = rows > 0 ? rows : height;
        byte[] tiff = TiffWrapper(data, columns, rows, k, aligned);
        Raster raster = Raster.Load(tiff, TiffFormat.Info);
        Samples samples = FromRaster(raster, invertBilevel: blackIs1, bilevelBits: true);
        return samples.Width == width ? samples : samples with { Width = Math.Min(width, samples.Width) };
    }

    private static byte[] TiffWrapper(byte[] data, int columns, int rows, int k, bool aligned)
    {
        // Little-endian TIFF: header, one IFD with 9 entries, then the strip.
        const int Entries = 9;
        int ifd = 8, stripAt = ifd + 2 + (Entries * 12) + 4;
        byte[] tiff = new byte[stripAt + data.Length];
        "II"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), (uint)ifd);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(ifd), Entries);
        int at = ifd + 2;
        void Entry(ushort tag, ushort type, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), value);
            at += 12;
        }

        Entry(256, 4, (uint)columns);
        Entry(257, 4, (uint)rows);
        Entry(258, 3, 1);
        Entry(259, 3, k < 0 ? 4u : 3u);
        Entry(262, 3, 0); // WhiteIsZero: the CCITT 1 bits are black.
        Entry(273, 4, (uint)stripAt);
        Entry(278, 4, (uint)rows);
        Entry(279, 4, (uint)data.Length);
        Entry((ushort)(k < 0 ? 293 : 292), 4, k < 0 ? 0u : (k > 0 ? 1u : 0u) | (aligned ? 4u : 0u));
        data.CopyTo(tiff, stripAt);
        return tiff;
    }

    /// <summary>
    /// A decoded raster as samples. Gray, RGB and CMYK keep their components; bilevel codec output becomes one bit a pixel
    /// with 1 for white, as the PDF filters give it (inverted for <c>/BlackIs1</c>).
    /// </summary>
    private static Samples FromRaster(Raster raster, bool invertBilevel, bool bilevelBits = false)
    {
        using (raster)
        {
            int width = raster.Width, height = raster.Height;
            if (bilevelBits)
            {
                using Raster<Gray8> gray = raster as Raster<Gray8> ?? raster.As<Gray8>();
                int stride = (width + 7) / 8;
                byte[] bits = new byte[stride * height];
                for (int y = 0; y < height; y++)
                {
                    Span<Gray8> row = gray.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        if ((row[x].L >= 128) ^ invertBilevel)
                        {
                            bits[(y * stride) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        }
                    }
                }

                return new Samples(width, height, 1, 1, bits);
            }

            switch (raster)
            {
                case Raster<Gray8> g:
                    return Pack(g, 1, (p, c) => p.L);
                case Raster<Rgb8> rgb:
                    return Pack(rgb, 3, (p, c) => c == 0 ? p.R : c == 1 ? p.G : p.B);
                case Raster<Cmyk8> cmyk:
                    return Pack(cmyk, 4, (p, c) => c == 0 ? p.C : c == 1 ? p.M : c == 2 ? p.Y : p.K);
                default:
                    using (Raster<Rgb8> converted = raster.As<Rgb8>())
                    {
                        return Pack(converted, 3, (p, c) => c == 0 ? p.R : c == 1 ? p.G : p.B);
                    }
            }
        }
    }

    private static Samples Pack<TPixel>(Raster<TPixel> raster, int components, Func<TPixel, int, byte> component)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        byte[] data = new byte[raster.Width * raster.Height * components];
        int i = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            Span<TPixel> row = raster.Row(y);
            for (int x = 0; x < raster.Width; x++)
            {
                for (int c = 0; c < components; c++)
                {
                    data[i++] = component(row[x], c);
                }
            }
        }

        return new Samples(raster.Width, raster.Height, components, 8, data);
    }

    private static double[]? Numbers(PdfArray? array)
    {
        return array is null ? null : [.. Enumerable.Range(0, array.Count).Select(i => array.GetNumber(i) ?? 0)];
    }

    private static byte Unit8(double v)
    {
        return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    private static ushort Unit16(double v)
    {
        return (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
    }

    /// <summary>Packed samples, rows padded to whole bytes as PDF stores them (8.9.3).</summary>
    private sealed record Samples(int Width, int Height, int Components, int Bits, byte[] Data)
    {
        /// <summary>The precision the samples had before they were scaled to <see cref="Bits"/>, as a codec's output is.</summary>
        public int SourceBits { get; init; } = Bits;

        private readonly int stride = ((Width * Components * Bits) + 7) / 8;

        public int Get(int x, int y, int c)
        {
            long bit = ((long)y * stride * 8) + ((((long)x * Components) + c) * Bits);
            long index = bit >> 3;
            if (index >= Data.Length)
            {
                return 0;
            }

            return Bits switch
            {
                8 => Data[index],
                16 => index + 1 < Data.Length ? (Data[index] << 8) | Data[index + 1] : Data[index] << 8,
                _ => (Data[index] >> (8 - Bits - (int)(bit & 7))) & ((1 << Bits) - 1),
            };
        }
    }

    /// <summary>The sRGB a JPX decoder gives, when the PDF names no colour space.</summary>
    private sealed class RgbOnly : ImageColorSpace
    {
        public override int Components => 3;

        public override ColorFamily Family => ColorFamily.Rgb;

        public override void ToRgb(ReadOnlySpan<double> c, Span<double> rgb)
        {
            rgb[0] = c[0];
            rgb[1] = c[1];
            rgb[2] = c[2];
        }
    }
}
