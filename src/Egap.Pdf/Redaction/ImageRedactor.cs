using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Redaction;

/// <summary>
/// Redacts the pixels of an image (Phase 5 design Q9): every pixel whose centre falls in an area takes that area's overlay
/// colour (black without one) and the image is encoded again, as JPEG when it was JPEG and losslessly otherwise. A stencil
/// mask's covered samples stop painting instead. The result is a new image XObject; the original stays for any other use.
/// </summary>
internal static class ImageRedactor
{
    /// <summary>The redacted image, or null when no pixel centre is covered.</summary>
    public static PdfStream? Redact(PdfDocument document, PdfImage image, PdfMatrix placement, IReadOnlyList<PdfRectangle> areas, IReadOnlyList<PdfRgbColor?> fills)
    {
        int width = image.Width, height = image.Height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        // An image bigger than a decoded stream may be cannot be decoded to redact its pixels; the caller removes it whole.
        if ((long)width * height * 4 > document.Options.MaxDecodedStreamBytes)
        {
            throw new PdfException($"The {width} by {height} image is too large to redact pixel by pixel.");
        }

        int?[] cover = new int?[width * height];
        bool any = false;
        for (int a = 0; a < areas.Count; a++)
        {
            if (!placement.TryInvert(out PdfMatrix inverse))
            {
                return null;
            }

            // Only the pixels inside the area's box in image space are tested.
            PdfRectangle box = inverse.Transform(areas[a]);
            int left = Math.Clamp((int)Math.Floor(box.Left * width), 0, width), right = Math.Clamp((int)Math.Ceiling(box.Right * width), 0, width);
            int top = Math.Clamp((int)Math.Floor((1 - box.Top) * height), 0, height), bottom = Math.Clamp((int)Math.Ceiling((1 - box.Bottom) * height), 0, height);
            for (int row = top; row < bottom; row++)
            {
                for (int column = left; column < right; column++)
                {
                    PdfPoint centre = placement.Transform(new PdfPoint((column + 0.5) / width, 1 - ((row + 0.5) / height)));
                    if (cover[(row * width) + column] is null && ContentRedactor.Contains(areas[a], new PdfRectangle(centre.X, centre.Y, centre.X, centre.Y)))
                    {
                        cover[(row * width) + column] = a;
                        any = true;
                    }
                }
            }
        }

        if (!any)
        {
            return null;
        }

        return image.IsStencilMask ? Stencil(image, cover) : Pixels(document, image, cover, fills);
    }

    private static PdfStream Pixels(PdfDocument document, PdfImage image, int?[] cover, IReadOnlyList<PdfRgbColor?> fills)
    {
        using Raster<Rgba8> raster = image.ToRaster<Rgba8>();
        int width = raster.Width;
        for (int i = 0; i < cover.Length; i++)
        {
            if (cover[i] is int area)
            {
                PdfRgbColor fill = fills[area] ?? new PdfRgbColor(0, 0, 0);
                raster.SetPixel(i % width, i / width, new Rgba8((byte)Math.Round(fill.Red * 255), (byte)Math.Round(fill.Green * 255), (byte)Math.Round(fill.Blue * 255), 255));
            }
        }

        bool jpeg = image.ImageCodec?.Value == "DCTDecode";
        PdfStream encoded = jpeg ? Images.ImageEncoder.EncodeJpeg(document, raster, 90) : Images.ImageEncoder.Encode(document, raster);
        if (image.Parameters.GetBool("Interpolate") == true)
        {
            encoded.Dictionary["Interpolate"] = PdfBoolean.True;
        }

        return encoded;
    }

    /// <summary>A stencil mask with its covered samples set so they paint nothing (8.9.6.2), stored with Flate.</summary>
    private static PdfStream Stencil(PdfImage image, int?[] cover)
    {
        byte[] samples = image.GetDecodedBytes();
        int width = image.Width, stride = (width + 7) / 8;

        // With /Decode [0 1] a sample of 0 paints; [1 0] swaps that.
        bool inverted = image.Parameters.Get<PdfArray>("Decode") is { Count: 2 } decode && decode.Get<PdfObject>(0) is PdfInteger { Value: 1 } or PdfReal { Value: 1 };
        for (int i = 0; i < cover.Length; i++)
        {
            if (cover[i] is null)
            {
                continue;
            }

            int at = ((i / width) * stride) + ((i % width) / 8);
            if (at >= samples.Length)
            {
                continue;
            }

            byte bit = (byte)(0x80 >> (i % width % 8));
            samples[at] = inverted ? (byte)(samples[at] & ~bit) : (byte)(samples[at] | bit);
        }

        PdfDictionary dictionary = new()
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(width),
            ["Height"] = new PdfInteger(image.Height),
            ["ImageMask"] = PdfBoolean.True,
            ["BitsPerComponent"] = new PdfInteger(1),
        };
        if (image.Parameters.Get("Decode") is PdfArray decodeArray)
        {
            dictionary["Decode"] = decodeArray.Clone();
        }

        PdfStream stream = new(dictionary);
        stream.SetDecodedBytes(samples);
        return stream;
    }
}
