using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Pages made from images, and pages rendered to image files.</summary>
public sealed class ImagePagesTests
{
    [Fact]
    public void AJpegPageIsTheImageAtItsResolution()
    {
        byte[] jpeg = Jpeg(300, 150, dpi: 150);

        using PdfDocument pdf = PdfDocument.FromImages([(ReadOnlyMemory<byte>)jpeg]);

        PdfRectangle box = Assert.Single(pdf.Pages).MediaBox;
        Assert.Equal(144, box.Right - box.Left, 3);
        Assert.Equal(72, box.Top - box.Bottom, 3);
        PdfStream image = ImageOf(pdf.Pages[0]);
        Assert.Equal("DCTDecode", image.Dictionary.GetName("Filter")!.Value);
        Assert.Equal(jpeg, image.GetEncodedBytes());
    }

    [Fact]
    public void ImagesFitAPageSizeWithinTheMargin()
    {
        byte[] wide = Png(400, 200);
        byte[] tall = Png(100, 300);

        using PdfDocument pdf = PdfDocument.FromImages([(ReadOnlyMemory<byte>)wide, tall], new PdfImagePageOptions { PageSize = PdfPageSize.A4, Margin = Length.Mm(10) });

        Assert.Equal(2, pdf.Pages.Count);
        Assert.True(pdf.Pages[0].MediaBox.Right > pdf.Pages[0].MediaBox.Top, "the wide image turns its page");
        Assert.True(pdf.Pages[1].MediaBox.Right < pdf.Pages[1].MediaBox.Top);
        using Raster<Rgba8> shown = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        Assert.Equal(255, shown.GetPixel(2, 2).R);                                            // the margin stays white
        Assert.Equal(new Rgba8(20, 120, 220, 255), shown.GetPixel(shown.Width / 2, shown.Height / 2));
    }

    [Fact]
    public void ImagesWithoutAResolutionUseTheFallback()
    {
        using PdfDocument pdf = PdfDocument.FromImages([(ReadOnlyMemory<byte>)Png(96, 192)], new PdfImagePageOptions { Dpi = 96 });

        Assert.Equal(72, pdf.Pages[0].MediaBox.Right, 3);
        Assert.Equal(144, pdf.Pages[0].MediaBox.Top, 3);
    }

    [Fact]
    public void RefusesNoImages()
    {
        Assert.Throws<ArgumentException>(() => PdfDocument.FromImages(Array.Empty<ReadOnlyMemory<byte>>()));
    }

    [Fact]
    public void RendersPagesToNumberedFiles()
    {
        using PdfDocument pdf = PdfDocument.FromImages([(ReadOnlyMemory<byte>)Png(40, 40), Png(40, 40), Png(40, 40)]);
        string folder = Path.Combine(Path.GetTempPath(), "egap-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<string> files = pdf.RenderPagesToFiles(Path.Combine(folder, "p{page:00}.png"), new PdfRenderOptions { Dpi = 72 }, PdfPageRange.Parse("1,z"), TestContext.Current.CancellationToken);
            Assert.Equal([Path.Combine(folder, "p01.png"), Path.Combine(folder, "p03.png")], files);
            using Raster first = Raster.Load(files[0]);
            Assert.Equal((30, 30), (first.Width, first.Height));

            IReadOnlyList<string> plain = pdf.RenderPagesToFiles(Path.Combine(folder, "page.jpg"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(folder, "page-2.jpg"), plain[1]);
            Assert.True(File.Exists(plain[2]));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static PdfStream ImageOf(PdfPage page)
    {
        return (PdfStream)Assert.Single(page.Resources!.Get<PdfDictionary>("XObject")!.Entries).Value.Resolve();
    }

    private static byte[] Png(int width, int height)
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height, new Rgb8(20, 120, 220));
        using MemoryStream png = new();
        raster.Save(png, "png");
        return png.ToArray();
    }

    private static byte[] Jpeg(int width, int height, int dpi)
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height, new Rgb8(200, 100, 50));
        using MemoryStream stream = new();
        raster.Save(stream, "jpg");
        byte[] jpeg = stream.ToArray();

        // Set the JFIF density, which sits at a fixed place in the APP0 segment the writer puts first.
        Assert.True(jpeg is [0xFF, 0xD8, 0xFF, 0xE0, _, _, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, ..]);
        jpeg[13] = 1;
        jpeg[14] = (byte)(dpi >> 8);
        jpeg[15] = (byte)dpi;
        jpeg[16] = (byte)(dpi >> 8);
        jpeg[17] = (byte)dpi;
        return jpeg;
    }
}
