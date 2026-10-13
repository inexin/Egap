using System.Text;

using Egami;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Images;

/// <summary>Image replacement: Phase 3 task 23.</summary>
public sealed class ImageReplaceTests
{
    [Fact]
    public void ReplacesAnImageEveryPageSharesAndSavesOnlyTheNewStream()
    {
        byte[] original = TwoPagesSharingAnImage();
        using PdfDocument pdf = PdfDocument.Open(original);
        using Raster<Rgb8> replacement = Raster.Create<Rgb8>(3, 2, new Rgb8(10, 20, 30));
        pdf.Pages[0].GetImages()[0].Image.Replace(replacement);

        foreach (PdfPage page in pdf.Pages)
        {
            PdfImage image = Assert.Single(page.GetImages()).Image;
            Assert.Equal((3, 2), (image.Width, image.Height));
            using Raster<Rgba8> pixels = image.ToRaster<Rgba8>();
            Assert.Equal(new Rgba8(10, 20, 30, 255), pixels.GetPixel(2, 1));
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        byte[] bytes = saved.ToArray();
        Assert.Equal(original, bytes[..original.Length]);
        string update = Encoding.Latin1.GetString(bytes, original.Length, bytes.Length - original.Length);
        // The update holds the image stream and the cross-reference stream, nothing of the pages.
        Assert.Equal(["Image", "XRef"], update.Split(" obj").Skip(1).Select(o => o.Contains("/XRef", StringComparison.Ordinal) ? "XRef" : o.Contains("/Image", StringComparison.Ordinal) ? "Image" : "other"));
        Assert.Contains("/Width 3", update, StringComparison.Ordinal);
    }

    [Fact]
    public void StoresJpegBytesAsTheyAre()
    {
        using PdfDocument pdf = PdfDocument.Open(TwoPagesSharingAnImage());
        using Raster<Rgb8> source = Raster.Create<Rgb8>(16, 8, new Rgb8(200, 40, 60));
        using MemoryStream jpeg = new();
        source.Save(jpeg, JpegFormat.Info);
        PdfImage image = pdf.Pages[1].GetImages()[0].Image;
        image.ReplaceWithJpeg(jpeg.ToArray());

        Assert.Equal("DCTDecode", image.ImageCodec!.Value);
        Assert.Equal(jpeg.ToArray(), image.GetRawBytes());
        Assert.Equal((16, 8, "DeviceRGB"), (image.Width, image.Height, ((PdfName)image.ColorSpace!).Value));
        Assert.Throws<ArgumentException>(() => image.ReplaceWithJpeg("not a jpeg"u8));
    }

    [Fact]
    public void EncodesJpegWithAlphaAsASoftMask()
    {
        using PdfDocument pdf = PdfDocument.Open(TwoPagesSharingAnImage());
        using Raster<Rgba8> source = Raster.Create<Rgba8>(8, 8, new Rgba8(0, 0, 255, 128));
        PdfImage image = pdf.Pages[0].GetImages()[0].Image;
        image.Replace(source, PdfImageEncoding.Jpeg(90));

        Assert.Equal("DCTDecode", image.ImageCodec!.Value);
        using Raster<Rgba8> pixels = image.ToRaster<Rgba8>();
        Rgba8 p = pixels.GetPixel(4, 4);
        Assert.Equal(128, p.A);
        Assert.InRange(p.B, 240, 255);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImageEncoding.Jpeg(0));
    }

    [Fact]
    public void RefusesToReplaceInlineImages()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.SetContent(PdfContent.Parse("BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080 EI"u8));
        PdfImage inline = Assert.Single(page.GetImages()).Image;
        using Raster<Gray8> replacement = Raster.Create<Gray8>(1, 1);

        Assert.Throws<InvalidOperationException>(() => inline.Replace(replacement));
    }

    private static byte[] TwoPagesSharingAnImage()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(1),
            ["Height"] = new PdfInteger(1),
            ["BitsPerComponent"] = new PdfInteger(8),
            ["ColorSpace"] = PdfName.Get("DeviceGray"),
            ["Interpolate"] = PdfBoolean.Get(true),
        });
        image.SetDecodedBytes([128]);
        PdfReference reference = pdf.Objects.Add(image);
        for (int i = 0; i < 2; i++)
        {
            PdfPage page = pdf.Pages.AddBlank();
            page.Resources = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im"] = reference } };
            page.SetContent(PdfContent.Parse("q 100 0 0 100 0 0 cm /Im Do Q"u8));
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        return saved.ToArray();
    }
}
