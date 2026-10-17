using Egami.Formats.Ccitt;

using Egap.Pdf.Images;

namespace Egap.Pdf.Optimizing;

/// <summary>
/// Bilevel images as CCITT Group 4 (Phase 7 design Q4, Build Plan task 4): one-component images whose samples are only black
/// and white, 1-bit images and stencil masks among them, are written with <c>/CCITTFaxDecode</c> through Egami's encoder when
/// that is at least 5% smaller than what they hold. G4 is lossless, so it runs in every preset, and a bilevel image is never
/// resampled (design Q3).
/// </summary>
/// <remarks>
/// The filter gives back exactly the samples coded, so the image keeps its colour space, <c>/Decode</c> array,
/// <c>/ImageMask</c> and masks: pixels whose sample is 0 are coded as CCITT black (BlackIs1 false, PDF's default). An 8-bit
/// image of 0 and 255 becomes a 1-bit image with the same meaning; an 8-bit DeviceRGB image of only black and white becomes
/// 1-bit DeviceGray. A 1-bit Indexed image keeps its palette: G4 codes its indices. Images already in CCITT or JBIG2, inline images, and
/// images with a colour key mask (whose ranges are in the old bit depth) are left alone.
/// </remarks>
internal static class BilevelEncoder
{
    /// <summary>
    /// Whether an image goes to this encoder: bilevel by the survey, or any 1-bit image of one component (an Indexed image
    /// with a two-colour palette among them), whose samples G4 codes as they are.
    /// </summary>
    public static bool Applies(SurveyedImage surveyed)
    {
        PdfImage image = surveyed.Image;
        return surveyed.Kind == SurveyedImageKind.Bilevel || (image.BitsPerComponent == 1 && !image.IsStencilMask && Space(image) is { Components: 1 });
    }

    /// <summary>Writes one bilevel image as G4; the change, or null when it was left as it was.</summary>
    public static ImageChange? Optimize(SurveyedImage surveyed, double minSaving)
    {
        PdfImage image = surveyed.Image;
        if (image.Stream is not PdfStream stream || !Applies(surveyed) || surveyed.ColorKeyMask is not null
            || image.ImageCodec?.Value is "CCITTFaxDecode" or "JBIG2Decode" or "JPXDecode" || image.Width <= 0 || image.Height <= 0)
        {
            return null;
        }

        // One component as stored, or 8-bit DeviceRGB that is only black and white, which becomes DeviceGray.
        ImageColorSpace? space = image.IsStencilMask ? null : Space(image);
        bool toGray = space is { Family: ColorFamily.Rgb, Components: 3 } && stream.Dictionary.Get("ColorSpace")?.Resolve() is PdfName { Value: "DeviceRGB" }
            && stream.Dictionary.Get("Decode") is null;
        if (!image.IsStencilMask && space?.Components != 1 && !toGray)
        {
            return null;
        }

        byte[]? rows = PackedRows(image, toGray ? 3 : 1);
        if (rows is null)
        {
            return null;
        }

        byte[] coded;
        try
        {
            coded = CcittFax.Encode(rows, new CcittFaxParameters { K = -1, Columns = image.Width, Rows = image.Height });
        }
        catch (ArgumentException)
        {
            return null;
        }

        long before = stream.EncodedLength;
        if (coded.Length > before * (1 - minSaving))
        {
            return null;
        }

        ImageChange change = new(stream, image.Width, image.Height, image.Width, image.Height, before, coded.Length,
            surveyed.EffectiveResolution, surveyed.EffectiveResolution, "CCITT G4");
        PdfDictionary d = stream.Dictionary;
        foreach (string key in new[] { "Filter", "DecodeParms", "Length" })
        {
            d.Remove(key);
        }

        d["BitsPerComponent"] = new PdfInteger(1);
        if (toGray)
        {
            d["ColorSpace"] = PdfName.Get("DeviceGray");
        }

        d["Filter"] = PdfName.Get("CCITTFaxDecode");
        d["DecodeParms"] = new PdfDictionary
        {
            ["K"] = new PdfInteger(-1),
            ["Columns"] = new PdfInteger(image.Width),
            ["Rows"] = new PdfInteger(image.Height),
        };
        stream.SetEncodedBytes(coded);
        return change;
    }

    /// <summary>
    /// The image's samples packed one bit a pixel, each row padded to whole bytes, 1 where the sample is at its maximum: the
    /// stored 1-bit rows as they are, or 8-bit samples of 0 and 255 thresholded (for three components, all three equal).
    /// Null when a sample is neither.
    /// </summary>
    private static byte[]? PackedRows(PdfImage image, int components)
    {
        int width = image.Width, height = image.Height, rowBytes = (width + 7) / 8;
        try
        {
            if (image.BitsPerComponent == 1 && components == 1 && image.ImageCodec is null)
            {
                byte[] data = image.GetDecodedBytes();
                if (data.Length < rowBytes * height)
                {
                    return null;
                }

                byte[] rows = data[..(rowBytes * height)];
                if ((width & 7) != 0)
                {
                    byte keep = (byte)(0xFF << (8 - (width & 7)));
                    for (int at = rowBytes - 1; at < rows.Length; at += rowBytes)
                    {
                        rows[at] &= keep;
                    }
                }

                return rows;
            }

            ImageDecoder.StoredSamples? stored = image.BitsPerComponent == 8 ? ImageDecoder.ReadStored(image) : null;
            if (stored is null || stored.Components != components || stored.Width != width || stored.Height != height)
            {
                return null;
            }

            byte[] packed = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = ((y * width) + x) * components;
                    byte v = stored.Data[i];
                    if (v is not (0 or 255) || (components == 3 && (stored.Data[i + 1] != v || stored.Data[i + 2] != v)))
                    {
                        return null;
                    }

                    if (v == 255)
                    {
                        packed[(y * rowBytes) + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }
                }
            }

            return packed;
        }
        catch (PdfException)
        {
            return null;
        }
    }

    private static ImageColorSpace? Space(PdfImage image)
    {
        try
        {
            return image.IsStencilMask ? null : ImageColorSpace.Read(image.Parameters.Get("ColorSpace"), image.Resources, null);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }
    }
}
