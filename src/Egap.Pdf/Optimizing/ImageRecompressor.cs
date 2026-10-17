using System.IO.Compression;

using Egami;
using Egami.Formats.Jpeg;
using Egami.Operations;
using Egami.Pixels;

using Egap.Pdf.Filters;
using Egap.Pdf.Images;

namespace Egap.Pdf.Optimizing;

/// <summary>
/// The optimizer's image pass (Phase 7 design 7.1, Q3 and Q4, Build Plan task 3): an image drawn above 1.5 times the target
/// resolution at its largest placement is downsampled with Egami's bicubic (Catmull-Rom) filter in its stored, encoded
/// components, with its soft mask resampled the same way; then it is written as JPEG when it is a photo and as Flate (level 9,
/// PNG predictors) otherwise; bilevel images go to <see cref="BilevelEncoder"/> as CCITT G4. The result is kept only when it is
/// at least 5% smaller than the image and soft mask it replaces.
/// </summary>
/// <remarks>
/// The image keeps its object, colour space, <c>/Decode</c> array and every other entry, so pages, forms and annotations that
/// draw it are untouched: only the samples, size, filters and soft mask change. Images whose samples cannot be resampled as
/// they are stored are left alone: inline images, Indexed, Lab, Separation and DeviceN images, colour-key masked images,
/// and JPEG 2000 images that carry their own colour
/// space. Existing JPEGs are re-encoded here only when downsampled; the rest is task 5.
/// </remarks>
internal static class ImageRecompressor
{
    /// <summary>Optimizes one surveyed image; the change made, or null when it was left as it was.</summary>
    public static ImageChange? Optimize(PdfDocument document, SurveyedImage surveyed, ImageSettings settings)
    {
        if (BilevelEncoder.Applies(surveyed))
        {
            return BilevelEncoder.Optimize(surveyed, settings.MinSaving);
        }

        PdfImage image = surveyed.Image;
        if (image.Stream is not PdfStream stream || image.IsStencilMask || surveyed.Kind == SurveyedImageKind.Unknown
            || surveyed.ColorKeyMask is not null || image.Width <= 0 || image.Height <= 0)
        {
            return null;
        }

        string? codec = image.ImageCodec?.Value;
        if (codec is "JBIG2Decode" or "CCITTFaxDecode")
        {
            return null;
        }

        ImageColorSpace space;
        try
        {
            space = ImageColorSpace.Read(stream.Dictionary.Get("ColorSpace"), image.Resources, null);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }

        if (space.Family is not (ColorFamily.Gray or ColorFamily.Rgb or ColorFamily.Cmyk) || space.Components is not (1 or 3 or 4))
        {
            return null;
        }

        (int width, int height) = TargetSize(surveyed, settings);
        bool resample = width < image.Width || height < image.Height;
        if (codec == "DCTDecode" && !resample)
        {
            return JpegOptimizer.Optimize(surveyed, settings, () => Encode(document, surveyed, settings, space, width, height, jpeg: true));
        }

        // A JPEG stays a JPEG when it is downsampled; otherwise only lossy re-encoding of a Flate or uncompressed photo is left.
        bool jpeg = settings.Lossy && (surveyed.Kind == SurveyedImageKind.Photo || codec == "DCTDecode");
        if (!settings.Lossy || (!resample && (!jpeg || codec is not null)))
        {
            return null;
        }

        return Encode(document, surveyed, settings, space, width, height, jpeg);
    }

    /// <summary>
    /// Decodes the stored samples, resamples them to the size given when it is smaller, and writes them as JPEG or Flate in
    /// place when the result is small enough. A JPEG is written at no higher a quality than it had.
    /// </summary>
    private static ImageChange? Encode(PdfDocument document, SurveyedImage surveyed, ImageSettings settings, ImageColorSpace space, int width, int height, bool jpeg)
    {
        PdfImage image = surveyed.Image;
        PdfStream stream = image.Stream!;
        bool resample = width < image.Width || height < image.Height;
        int quality = settings.JpegQuality;
        try
        {
            if (image.ImageCodec?.Value == "DCTDecode" && JpegOptimizer.Quality(image.DecodeFilters().Data) is int had)
            {
                quality = Math.Min(quality, had);
            }
        }
        catch (PdfException)
        {
            return null;
        }

        ImageDecoder.StoredSamples? stored;
        try
        {
            stored = ImageDecoder.ReadStored(image);
        }
        catch (PdfException)
        {
            return null;
        }

        if (stored is null || stored.Components != space.Components)
        {
            return null;
        }

        // A CMYK JPEG would need its inks inverted under a Decode array: with one already given, Flate keeps the meaning.
        bool hasDecode = stream.Dictionary.Get("Decode") is not null;
        if (jpeg && space.Components == 4 && hasDecode)
        {
            jpeg = false;
        }

        PdfStream? oldMask = surveyed.SoftMask;
        long before = stream.EncodedLength + (oldMask?.EncodedLength ?? 0);
        using Raster raster = ToRaster(stored);
        using Raster? resized = resample ? raster.Derive(ops => ops.Resize(width, height, ResampleFilter.Bicubic)) : null;
        Raster sized = resized ?? raster;
        PdfStream encoded = jpeg ? Jpeg(sized, quality) : Flate(sized);
        PdfStream? mask = null;
        if (oldMask is not null && resample)
        {
            mask = ResampleMask(oldMask, image, width, height);
            if (mask is null)
            {
                return null;
            }
        }

        long after = encoded.EncodedLength + (mask?.EncodedLength ?? oldMask?.EncodedLength ?? 0);
        if (after > before * (1 - settings.MinSaving))
        {
            return null;
        }

        ImageChange change = new(stream, image.Width, image.Height, sized.Width, sized.Height, before, after,
            surveyed.EffectiveResolution, surveyed.EffectiveResolution is double dpi ? dpi * sized.Width / image.Width : null,
            jpeg ? "JPEG" : "Flate");
        Apply(document, stream, encoded, mask);
        return change;
    }

    /// <summary>The size an image is downsampled to: unchanged unless above the threshold times the target resolution.</summary>
    internal static (int Width, int Height) TargetSize(SurveyedImage surveyed, ImageSettings settings)
    {
        int width = surveyed.Image.Width, height = surveyed.Image.Height;
        if (settings.TargetResolution is not double target || surveyed.EffectiveResolution is not double effective
            || !double.IsFinite(effective) || effective <= target * settings.Threshold)
        {
            return (width, height);
        }

        int w = Math.Clamp((int)Math.Round(width * target / surveyed.ResolutionX), 1, width);
        int h = Math.Clamp((int)Math.Round(height * target / surveyed.ResolutionY), 1, height);
        return (w, h);
    }

    private static Raster ToRaster(ImageDecoder.StoredSamples stored)
    {
        int w = stored.Width, h = stored.Height;
        byte[] data = stored.Data;
        switch (stored.Components)
        {
            case 1:
                Raster<Gray8> gray = Raster.Create<Gray8>(w, h);
                for (int y = 0; y < h; y++)
                {
                    Span<Gray8> row = gray.Row(y);
                    for (int x = 0; x < w; x++)
                    {
                        row[x] = new Gray8(data[(y * w) + x]);
                    }
                }

                return gray;
            case 3:
                Raster<Rgb8> rgb = Raster.Create<Rgb8>(w, h);
                for (int y = 0; y < h; y++)
                {
                    Span<Rgb8> row = rgb.Row(y);
                    for (int x = 0, i = y * w * 3; x < w; x++, i += 3)
                    {
                        row[x] = new Rgb8(data[i], data[i + 1], data[i + 2]);
                    }
                }

                return rgb;
            default:
                Raster<Cmyk8> cmyk = Raster.Create<Cmyk8>(w, h);
                for (int y = 0; y < h; y++)
                {
                    Span<Cmyk8> row = cmyk.Row(y);
                    for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
                    {
                        row[x] = new Cmyk8(data[i], data[i + 1], data[i + 2], data[i + 3]);
                    }
                }

                return cmyk;
        }
    }

    private static byte[] Samples(Raster raster)
    {
        int w = raster.Width, h = raster.Height;
        switch (raster)
        {
            case Raster<Gray8> gray:
                {
                    byte[] data = new byte[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        Span<Gray8> row = gray.Row(y);
                        for (int x = 0; x < w; x++)
                        {
                            data[(y * w) + x] = row[x].L;
                        }
                    }

                    return data;
                }

            case Raster<Rgb8> rgb:
                {
                    byte[] data = new byte[w * h * 3];
                    for (int y = 0; y < h; y++)
                    {
                        Span<Rgb8> row = rgb.Row(y);
                        for (int x = 0, i = y * w * 3; x < w; x++, i += 3)
                        {
                            (data[i], data[i + 1], data[i + 2]) = (row[x].R, row[x].G, row[x].B);
                        }
                    }

                    return data;
                }

            case Raster<Cmyk8> cmyk:
                {
                    byte[] data = new byte[w * h * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Span<Cmyk8> row = cmyk.Row(y);
                        for (int x = 0, i = y * w * 4; x < w; x++, i += 4)
                        {
                            (data[i], data[i + 1], data[i + 2], data[i + 3]) = (row[x].C, row[x].M, row[x].Y, row[x].K);
                        }
                    }

                    return data;
                }

            default:
                throw new InvalidOperationException($"No samples for {raster.GetType().Name}.");
        }
    }

    private static int Components(Raster raster) => raster switch
    {
        Raster<Gray8> => 1,
        Raster<Rgb8> => 3,
        _ => 4,
    };

    /// <summary>Samples as an image stream: Flate at level 9 with the PNG Up predictor on 8-bit samples.</summary>
    internal static PdfStream Flate(Raster raster)
    {
        return FlateStream(Samples(raster), raster.Width, raster.Height, Components(raster));
    }

    internal static PdfStream FlateStream(byte[] samples, int width, int height, int components)
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
            ["Width"] = new PdfInteger(width),
            ["Height"] = new PdfInteger(height),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["Filter"] = PdfName.Get("FlateDecode"),
            ["DecodeParms"] = new PdfDictionary
            {
                ["Predictor"] = new PdfInteger(12),
                ["Colors"] = new PdfInteger(components),
                ["BitsPerComponent"] = new PdfInteger(8),
                ["Columns"] = new PdfInteger(width),
            },
        });
        stream.SetEncodedBytes(FlateFilter.Encode(predicted, CompressionLevel.SmallestSize));
        return stream;
    }

    /// <summary>Samples as a baseline JPEG at a quality; CMYK written by Egami with an Adobe marker and inverted inks.</summary>
    private static PdfStream Jpeg(Raster raster, int quality)
    {
        using MemoryStream jpeg = new();
        raster.Save(jpeg, JpegFormat.Info, new JpegWriterOptions { Quality = quality, OptimizeHuffman = true });
        PdfStream stream = ImageEncoder.JpegStream(jpeg.ToArray());
        stream.Dictionary.Remove("ColorSpace");
        stream.Dictionary.Remove("Type");
        stream.Dictionary.Remove("Subtype");
        return stream;
    }

    /// <summary>The soft mask resampled by the image's own factors, as Flate; null when it cannot be read.</summary>
    private static PdfStream? ResampleMask(PdfStream mask, PdfImage image, int width, int height)
    {
        PdfImage soft = new(mask, image.Resources);
        if (soft.Width <= 0 || soft.Height <= 0)
        {
            return null;
        }

        ImageDecoder.StoredSamples? stored;
        try
        {
            stored = ImageDecoder.ReadStored(soft);
        }
        catch (PdfException)
        {
            return null;
        }

        if (stored is not { Components: 1 })
        {
            return null;
        }

        int w = Math.Clamp((int)Math.Round((double)soft.Width * width / image.Width), 1, soft.Width);
        int h = Math.Clamp((int)Math.Round((double)soft.Height * height / image.Height), 1, soft.Height);
        using Raster raster = ToRaster(stored);
        using Raster sized = raster.Derive(ops => ops.Resize(w, h, ResampleFilter.Bicubic));
        PdfStream encoded = Flate(sized);
        foreach (KeyValuePair<PdfName, PdfObject> entry in mask.Dictionary.Entries)
        {
            if (entry.Key.Value is not ("Width" or "Height" or "BitsPerComponent" or "Filter" or "DecodeParms" or "Length"))
            {
                encoded.Dictionary[entry.Key] = entry.Value switch
                {
                    PdfDictionary dictionary => dictionary.Clone(),
                    PdfArray array => array.Clone(),
                    _ => entry.Value,
                };
            }
        }

        return encoded;
    }

    /// <summary>Writes the new samples into the image's own stream, keeping its object and every other entry.</summary>
    private static void Apply(PdfDocument document, PdfStream stream, PdfStream encoded, PdfStream? mask)
    {
        PdfDictionary d = stream.Dictionary;
        foreach (string key in new[] { "Filter", "DecodeParms", "SMaskInData", "Length" })
        {
            d.Remove(key);
        }

        foreach (PdfName key in encoded.Dictionary.Entries.Select(e => e.Key).ToList())
        {
            PdfObject value = encoded.Dictionary[key]!;
            encoded.Dictionary.Remove(key);
            d[key] = value;
        }

        stream.SetEncodedBytes(encoded.GetEncodedBytes());
        if (mask is not null)
        {
            mask.Dictionary["Type"] = PdfName.Get("XObject");
            mask.Dictionary["Subtype"] = PdfName.Get("Image");
            mask.Dictionary["ColorSpace"] = PdfName.Get("DeviceGray");
            d["SMask"] = document.Objects.Add(mask);
        }
    }
}

/// <summary>What the image pass aims for; a preset fills these in (design Q2).</summary>
internal sealed record ImageSettings
{
    /// <summary>The resolution images are downsampled to, in samples per inch; null keeps every image's size.</summary>
    public double? TargetResolution { get; init; }

    /// <summary>Images are downsampled only above this many times the target (design Q3).</summary>
    public double Threshold { get; init; } = 1.5;

    /// <summary>The quality photos are written at as JPEG.</summary>
    public int JpegQuality { get; init; } = 75;

    /// <summary>Whether lossy changes are allowed at all: false for the Lossless preset.</summary>
    public bool Lossy { get; init; } = true;

    /// <summary>A result must be at least this much smaller than what it replaces, or the original is kept.</summary>
    public double MinSaving { get; init; } = 0.05;
}

/// <summary>One image the pass changed, for the optimize report.</summary>
internal sealed record ImageChange(PdfStream Stream, int OldWidth, int OldHeight, int NewWidth, int NewHeight, long OldBytes, long NewBytes,
    double? OldResolution, double? NewResolution, string Encoding);
