using System.Text;

using Egami;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Images;

/// <summary>Image listing and decoding: Phase 3 task 22.</summary>
public sealed class ImageDecodeTests
{
    [Fact]
    public void DecodesRgbSamples()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfImage image = Image(pdf, 2, 1, 8, PdfName.Get("DeviceRGB"), [255, 0, 0, 0, 128, 255]);

        using Raster<Rgba8> raster = image.ToRaster<Rgba8>();
        Assert.Equal([new Rgba8(255, 0, 0, 255), new Rgba8(0, 128, 255, 255)], Pixels(raster));
    }

    [Fact]
    public void AppliesTheDecodeArrayToOneBitGray()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfImage image = Image(pdf, 4, 1, 1, PdfName.Get("DeviceGray"), [0b1010_0000], decode: [1, 0]);

        using Raster<Rgba8> raster = image.ToRaster<Rgba8>();
        Assert.Equal([0, 255, 0, 255], Pixels(raster).Select(p => (int)p.R));
    }

    [Fact]
    public void LooksUpIndexedColours()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfArray space = [PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), new PdfInteger(1), PdfString.FromBytes([10, 20, 30, 200, 210, 220])];
        PdfImage image = Image(pdf, 3, 1, 2, space, [0b0001_0000]);

        using Raster<Rgba8> raster = image.ToRaster<Rgba8>();
        Assert.Equal([new Rgba8(10, 20, 30, 255), new Rgba8(200, 210, 220, 255), new Rgba8(10, 20, 30, 255)], Pixels(raster));
    }

    [Fact]
    public void ConvertsSeparationAndDeviceNThroughTheirTintTransforms()
    {
        using PdfDocument pdf = PdfDocument.Create();

        // A spot colour that is half-strength cyan at full tint, and a two-colorant DeviceN computed by a PostScript function.
        PdfDictionary tint = new() { ["FunctionType"] = new PdfInteger(2), ["Domain"] = Numbers(0, 1), ["C0"] = Numbers(0, 0, 0, 0), ["C1"] = Numbers(0.5, 0, 0, 0), ["N"] = new PdfInteger(1) };
        PdfArray separation = [PdfName.Get("Separation"), PdfName.Get("Spot"), PdfName.Get("DeviceCMYK"), tint];
        PdfImage spot = Image(pdf, 1, 1, 8, separation, [255]);
        using (Raster<Rgba8> raster = spot.ToRaster<Rgba8>())
        {
            // Half cyan on coated paper, through FOGRA39.
            Assert.Equal(new Rgba8(129, 208, 245, 255), Pixels(raster)[0]);
        }

        PdfStream program = new(new PdfDictionary { ["FunctionType"] = new PdfInteger(4), ["Domain"] = Numbers(0, 1, 0, 1), ["Range"] = Numbers(0, 1, 0, 1, 0, 1) });
        program.SetDecodedBytes("{ exch dup 0.5 gt { pop 1 } { pop 0 } ifelse exch 0 }"u8);
        PdfArray deviceN = [PdfName.Get("DeviceN"), new PdfArray { PdfName.Get("A"), PdfName.Get("B") }, PdfName.Get("DeviceRGB"), pdf.Objects.Add(program)];
        PdfImage twoInks = Image(pdf, 2, 1, 8, deviceN, [255, 51, 0, 102]);
        using (Raster<Rgba8> raster = twoInks.ToRaster<Rgba8>())
        {
            Assert.Equal([new Rgba8(255, 51, 0, 255), new Rgba8(0, 102, 0, 255)], Pixels(raster));
        }
    }

    [Fact]
    public void AppliesSoftMasksColourKeysAndStencils()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfImage soft = Image(pdf, 2, 1, 8, PdfName.Get("DeviceGray"), [255, 255]);
        PdfStream mask = Stream(pdf, 2, 1, 8, PdfName.Get("DeviceGray"), [0, 200]);
        soft.Stream!.Dictionary["SMask"] = pdf.Objects.Add(mask);
        using (Raster<Rgba8> raster = soft.ToRaster<Rgba8>())
        {
            Assert.Equal([0, 200], Pixels(raster).Select(p => (int)p.A));
        }

        using (Raster<Rgba8> raster = soft.ToRaster<Rgba8>(new PdfImageOptions { ApplyMasks = false }))
        {
            Assert.Equal([255, 255], Pixels(raster).Select(p => (int)p.A));
        }

        PdfImage keyed = Image(pdf, 3, 1, 8, PdfName.Get("DeviceGray"), [10, 100, 250]);
        keyed.Stream!.Dictionary["Mask"] = Numbers(90, 110);
        using (Raster<Rgba8> raster = keyed.ToRaster<Rgba8>())
        {
            Assert.Equal([255, 0, 255], Pixels(raster).Select(p => (int)p.A));
        }

        PdfStream stencilStream = new(new PdfDictionary { ["Type"] = PdfName.Get("XObject"), ["Subtype"] = PdfName.Get("Image"), ["Width"] = new PdfInteger(2), ["Height"] = new PdfInteger(1), ["ImageMask"] = PdfBoolean.Get(true) });
        stencilStream.SetDecodedBytes([0b0100_0000]);
        PdfImage stencil = new(stencilStream, null);
        Assert.True(stencil.IsStencilMask);
        using (Raster<Rgba8> raster = stencil.ToRaster<Rgba8>())
        {
            Assert.Equal([255, 0], Pixels(raster).Select(p => (int)p.A));
        }
    }

    [Fact]
    public void KeepsSixteenBitsAndCmykWhenAsked()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfImage deep = Image(pdf, 1, 1, 16, PdfName.Get("DeviceRGB"), [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC]);
        using (Raster raster = deep.ToRaster())
        {
            Rgba16 pixel = Assert.IsType<Raster<Rgba16>>(raster).GetPixel(0, 0);
            Assert.Equal((0x1234, 0x5678, 0x9ABC), (pixel.R, pixel.G, pixel.B));
        }

        PdfImage cmyk = Image(pdf, 1, 1, 8, PdfName.Get("DeviceCMYK"), [0, 255, 255, 0]);
        using (Raster raster = cmyk.ToRaster(new PdfImageOptions { ConvertColor = false }))
        {
            Cmyk8 ink = Assert.IsType<Raster<Cmyk8>>(raster).GetPixel(0, 0);
            Assert.Equal((0, 255, 255, 0), (ink.C, ink.M, ink.Y, ink.K));
        }

        using (Raster<Rgba8> raster = cmyk.ToRaster<Rgba8>())
        {
            // Magenta and yellow print a press red (FOGRA39's), not sRGB's.
            Assert.Equal(new Rgba8(228, 9, 22, 255), Pixels(raster)[0]);
        }
    }

    [Fact]
    public void DecodesJpegAndKeepsItsBytes()
    {
        using PdfDocument pdf = PdfDocument.Create();
        using Raster<Rgb8> source = Raster.Create<Rgb8>(16, 8, new Rgb8(200, 40, 60));
        using MemoryStream jpeg = new();
        source.Save(jpeg, JpegFormat.Info);
        PdfStream stream = new(new PdfDictionary { ["Type"] = PdfName.Get("XObject"), ["Subtype"] = PdfName.Get("Image"), ["Width"] = new PdfInteger(16), ["Height"] = new PdfInteger(8), ["BitsPerComponent"] = new PdfInteger(8), ["ColorSpace"] = PdfName.Get("DeviceRGB"), ["Filter"] = PdfName.Get("DCTDecode") });
        stream.SetEncodedBytes(jpeg.ToArray());
        PdfImage image = new(stream, null);

        Assert.Equal("DCTDecode", image.ImageCodec!.Value);
        Assert.Equal(jpeg.ToArray(), image.GetRawBytes());
        using Raster<Rgba8> raster = image.ToRaster<Rgba8>();
        Rgba8 p = raster.GetPixel(8, 4);
        Assert.InRange(Math.Abs(p.R - 200) + Math.Abs(p.G - 40) + Math.Abs(p.B - 60), 0, 12);
    }

    [Fact]
    public void GivesACmykJpegsStoredSamplesToTheColourSpace()
    {
        // Egami writes no ink as Adobe does, inverted (255); a PDF reader takes that as full ink unless a Decode array inverts it.
        using PdfDocument pdf = PdfDocument.Create();
        using Raster<Cmyk8> source = Raster.Create<Cmyk8>(8, 8, new Cmyk8(0, 0, 0, 0));
        using MemoryStream jpeg = new();
        source.Save(jpeg, JpegFormat.Info);
        foreach ((PdfArray? decode, int expected) in new (PdfArray?, int)[] { (null, 0), (Numbers(1, 0, 1, 0, 1, 0, 1, 0), 255) })
        {
            PdfStream stream = new(new PdfDictionary { ["Type"] = PdfName.Get("XObject"), ["Subtype"] = PdfName.Get("Image"), ["Width"] = new PdfInteger(8), ["Height"] = new PdfInteger(8), ["BitsPerComponent"] = new PdfInteger(8), ["ColorSpace"] = PdfName.Get("DeviceCMYK"), ["Filter"] = PdfName.Get("DCTDecode") });
            if (decode is not null)
            {
                stream.Dictionary["Decode"] = decode;
            }

            stream.SetEncodedBytes(jpeg.ToArray());
            using Raster<Rgba8> raster = new PdfImage(stream, null).ToRaster<Rgba8>();
            Assert.InRange(raster.GetPixel(4, 4).G, expected - 30, expected + 30);
        }
    }

    [Fact]
    public void ListsImagesWithTheirPlacementInlineOnesIncluded()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 200, 200));
        PdfStream xObject = Stream(pdf, 1, 1, 8, PdfName.Get("DeviceGray"), [128]);
        page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im1"] = pdf.Objects.Add(xObject) } };
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes("q 100 0 0 50 10 20 cm /Im1 Do Q q 20 0 0 20 0 0 cm BI /W 2 /H 1 /CS /G /BPC 8 /F /AHx ID 00FF> EI Q")));

        IReadOnlyList<PdfPlacedImage> images = page.GetImages();
        Assert.Equal(2, images.Count);
        Assert.Equal(("Im1", new PdfMatrix(100, 0, 0, 50, 10, 20)), (images[0].Name, images[0].Matrix));
        Assert.True(images[1].Image.IsInline);
        Assert.Equal("ASCIIHexDecode", Assert.Single(images[1].Image.Filters).Value);
        using Raster<Rgba8> raster = images[1].Image.ToRaster<Rgba8>();
        Assert.Equal([0, 255], Pixels(raster).Select(p => (int)p.R));
    }

    [Fact]
    public void ListsImagesInsideTilingPatternsOncePerUse()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 200, 200));
        PdfStream tile = new(new PdfDictionary
        {
            ["PatternType"] = new PdfInteger(1),
            ["PaintType"] = new PdfInteger(1),
            ["TilingType"] = new PdfInteger(1),
            ["BBox"] = Numbers(0, 0, 10, 10),
            ["XStep"] = new PdfInteger(10),
            ["YStep"] = new PdfInteger(10),
            ["Matrix"] = Numbers(1, 0, 0, 1, 5, 0),
            ["Resources"] = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Dot"] = pdf.Objects.Add(Stream(pdf, 1, 1, 8, PdfName.Get("DeviceGray"), [0])) } },
        });
        tile.SetDecodedBytes("q 10 0 0 10 0 0 cm /Dot Do Q"u8);
        page.Resources = new PdfDictionary { ["Pattern"] = new PdfDictionary { ["P1"] = pdf.Objects.Add(tile) } };

        // Two fills with the pattern: one use, one listing.
        page.SetContent(PdfContent.Parse("/Pattern cs /P1 scn 0 0 100 100 re f 100 100 50 50 re f"u8));

        PdfPlacedImage image = Assert.Single(page.GetImages());
        Assert.Equal(("Dot", new PdfMatrix(10, 0, 0, 10, 5, 0)), (image.Name, image.Matrix));
    }

    private static PdfImage Image(PdfDocument pdf, int width, int height, int bits, PdfObject space, byte[] data, double[]? decode = null)
    {
        PdfStream stream = Stream(pdf, width, height, bits, space, data);
        if (decode is not null)
        {
            stream.Dictionary["Decode"] = Numbers(decode);
        }

        pdf.Objects.Add(stream);
        return new PdfImage(stream, null);
    }

    private static PdfStream Stream(PdfDocument pdf, int width, int height, int bits, PdfObject space, byte[] data)
    {
        _ = pdf;
        PdfStream stream = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(width),
            ["Height"] = new PdfInteger(height),
            ["BitsPerComponent"] = new PdfInteger(bits),
            ["ColorSpace"] = space,
        });
        stream.SetDecodedBytes(data);
        return stream;
    }

    private static PdfArray Numbers(params double[] values)
    {
        return [.. values.Select(v => (PdfObject)new PdfReal(v))];
    }

    private static Rgba8[] Pixels(Raster<Rgba8> raster)
    {
        Rgba8[] pixels = new Rgba8[raster.Width * raster.Height];
        raster.CopyPixelsTo(pixels);
        return pixels;
    }
}
