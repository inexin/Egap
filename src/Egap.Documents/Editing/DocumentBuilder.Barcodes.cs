using Egami;
using Egami.Barcodes;
using Egami.Drawing;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egap.Documents;

/// <summary>Barcodes.</summary>
public sealed partial class DocumentBuilder
{
    /// <summary>The resolution of the raster stand-in a barcode picture keeps for Word, in pixels per inch.</summary>
    private const double BarcodeFallbackResolution = 300;

    /// <summary>
    /// Writes a barcode as a picture <paramref name="width"/> by <paramref name="height"/> points: an SVG of the symbol, which
    /// layout and PDF export draw as vectors, with a PNG stand-in at 300 pixels per inch that DOCX keeps for Word.
    /// </summary>
    /// <param name="barcode">The symbol, made with Egami.Barcodes (<c>Barcode.Qr</c>, <c>Barcode.Ean13</c> and the others).</param>
    /// <param name="width">The width in points.</param>
    /// <param name="height">The height in points.</param>
    /// <param name="options">How the symbol is drawn (quiet zone, bar width reduction, the font of a linear symbol's text); null for the defaults.</param>
    /// <returns>The picture, with the symbol's text as its alternative text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="barcode"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    public Picture InsertBarcode(Barcode barcode, double width, double height, BarcodeDrawOptions? options = null)
    {
        return Put(BarcodePicture(Document, barcode, width, height, options));
    }

    /// <summary>A barcode picture of <paramref name="document"/>, not yet in a paragraph: the SVG and its PNG stand-in.</summary>
    internal static Picture BarcodePicture(Document document, Barcode barcode, double width, double height, BarcodeDrawOptions? options)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        if (!(width > 0 && height > 0 && double.IsFinite(width) && double.IsFinite(height)))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The size must be positive.");
        }

        float w = (float)width, h = (float)height;
        SolidPaint black = new(new RgbaF32(0, 0, 0, 1));
        using MemoryStream svg = new();
        using (Egami.Svg.SvgCanvas canvas = new(svg, w, h))
        {
            canvas.DrawBarcode(barcode, new RectF(0, 0, w, h), black, options);
        }

        float scale = (float)(BarcodeFallbackResolution / 72);
        using Raster<Rgba8> raster = Raster.Create(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale)), new Rgba8(255, 255, 255, 255));
        using (RasterCanvas<Rgba8> canvas = new(raster))
        {
            canvas.Scale(scale, scale);
            canvas.DrawBarcode(barcode, new RectF(0, 0, w, h), black, options);
        }

        using MemoryStream png = new();
        raster.Save(png, PngFormat.Info);
        return new Picture(document, new ImageData(svg.ToArray(), "image/svg+xml"), width, height)
        {
            Fallback = new ImageData(png.ToArray(), "image/png"),
            Description = barcode.HumanReadableText,
        };
    }
}
