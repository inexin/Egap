using System.Numerics;

using Egami;
using Egami.Colors;
using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Canvas;

/// <summary>Images on the PDF canvas: Phase 3 task 10.</summary>
public sealed class PdfCanvasImageTests
{
    [Fact]
    public void WritesSixteenBitImagesWithSixteenBits()
    {
        using Raster<Rgba16> image = Raster.Create<Rgba16>(3, 2, new Rgba16(1000, 2000, 3000, 40000));
        PdfStream stream = Single(c => c.DrawImage(image, Vector2.Zero));

        Assert.Equal(16, stream.Dictionary.GetInt("BitsPerComponent"));
        Assert.Equal(3 * 2 * 3 * 2, stream.GetDecodedBytes().Length);
        PdfStream mask = stream.Dictionary.Get<PdfStream>("SMask")!;
        Assert.Equal(16, mask.Dictionary.GetInt("BitsPerComponent"));
        Assert.Equal([0x9C, 0x40], mask.GetDecodedBytes()[..2]);
    }

    [Fact]
    public void KeepsAnRgbProfileAsICCBased()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(2, 2, new Rgba8(128, 128, 128, 255));
        image.Metadata.IccProfile = ColorProfile.DisplayP3.ToIcc();
        PdfStream stream = Single(c => c.DrawImage(image, Vector2.Zero));

        PdfArray space = stream.Dictionary.Get<PdfArray>("ColorSpace")!;
        Assert.Equal("ICCBased", space.GetName(0)!.Value);
        Assert.Equal(3, space.Get<PdfStream>(1)!.Dictionary.GetInt("N"));

        // Gray pixels keep their three components, since the profile describes three.
        Assert.Equal(2 * 2 * 3, stream.GetDecodedBytes().Length);
    }

    [Fact]
    public void WritesGrayPixelsAsDeviceGray()
    {
        using Raster<Gray8> image = Raster.Create<Gray8>(4, 1, new Gray8(77));
        PdfStream stream = Single(c => c.DrawImage(image, Vector2.Zero));

        Assert.Equal("DeviceGray", stream.Dictionary.GetName("ColorSpace")!.Value);
        Assert.Equal([77, 77, 77, 77], stream.GetDecodedBytes());
        Assert.Null(stream.Dictionary.Get("SMask"));
    }

    [Fact]
    public void EmbedsAJpegAsItIsStored()
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(16, 8, new Rgb8(200, 100, 50));
        using MemoryStream jpeg = new();
        raster.Save(jpeg, "jpg");
        EncodedImage image = EncodedImage.Load(jpeg.ToArray());

        PdfStream stream = Single(c =>
        {
            c.DrawImage(image, new RectF(0, 0, 16, 8));
            c.DrawImage(image, new RectF(0, 0, 8, 8), new RectF(20, 20, 40, 40));
        });

        Assert.Equal("DCTDecode", stream.Dictionary.GetName("Filter")!.Value);
        Assert.Equal(jpeg.ToArray(), stream.GetEncodedBytes());
        Assert.Equal((16, 8), (stream.Dictionary.GetInt("Width"), stream.Dictionary.GetInt("Height")));
    }

    [Fact]
    public void DecodesImagesItCannotEmbedAsTheyAre()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4, new Rgba8(10, 20, 30, 128));
        using MemoryStream png = new();
        raster.Save(png, "png");

        PdfStream stream = Single(c => c.DrawImage(EncodedImage.Load(png.ToArray()), new RectF(0, 0, 4, 4)));

        Assert.Equal("FlateDecode", stream.Dictionary.GetName("Filter")!.Value);
        Assert.NotNull(stream.Dictionary.Get("SMask"));
    }

    [Fact]
    public void DrawsLatticesAndAtlasesFromOneImage()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(9, 9, new Rgba8(0, 0, 255, 255));
        PdfStream stream = Single(c =>
        {
            c.DrawImageNinePatch(image, new Rect(3, 3, 3, 3), new RectF(0, 0, 60, 30));
            c.DrawAtlas(image, [SpriteTransform.FromRotationScale(2, 30, new Vector2(50, 50)), SpriteTransform.FromRotationScale(1, 0, new Vector2(80, 10))], [new RectF(0, 0, 3, 3), new RectF(3, 3, 6, 6)]);
        });

        Assert.Equal(9, stream.Dictionary.GetInt("Width"));
    }

    /// <summary>Draws on a new page and returns the one image XObject the page uses.</summary>
    private static PdfStream Single(Action<Egami.Drawing.Canvas> draw)
    {
        PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 100, 100));
        using (PdfCanvas canvas = new(page))
        {
            draw(canvas);
            Assert.Empty(canvas.Warnings);
        }

        return (PdfStream)Assert.Single(page.Resources!.Get<PdfDictionary>("XObject")!.Entries).Value.Resolve();
    }
}
