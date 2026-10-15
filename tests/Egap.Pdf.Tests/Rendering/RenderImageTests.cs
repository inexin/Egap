using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Images (Phase 4 task 6): XObjects and inline images, stencil masks, alpha, sampling and reduced decoding.</summary>
public sealed class RenderImageTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void ImageXObjectsFillTheUnitSquareRightWayUp()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 40, 40));
        using Raster<Rgba8> quarters = Quarters();
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawImage(quarters, new RectF(0, 0, 40, 40));
        }

        using Raster<Rgba8> raster = page.Render();

        // Top left red, top right green, bottom left blue, bottom right white, as the raster was.
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(5, 5));
        Assert.Equal(new Rgba8(0, 255, 0, 255), raster.GetPixel(35, 5));
        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(5, 35));
        Assert.Equal(White, raster.GetPixel(35, 35));
    }

    [Fact]
    public void UpscaledImagesKeepHardEdgesUnlessTheyInterpolate()
    {
        using Raster<Rgba8> hard = RenderCoreTests.Render("q 20 0 0 20 0 0 cm BI /W 2 /H 1 /CS /G /BPC 8 ID \u0000\u00ff EI Q");
        using Raster<Rgba8> smooth = RenderCoreTests.Render("q 20 0 0 20 0 0 cm BI /W 2 /H 1 /CS /G /BPC 8 /I true ID \u0000\u00ff EI Q");

        Assert.Equal(new Rgba8(0, 0, 0, 255), hard.GetPixel(9, 10));
        Assert.Equal(White, hard.GetPixel(10, 10));
        Assert.InRange(smooth.GetPixel(9, 10).R, 60, 200);
    }

    [Fact]
    public void StencilMasksPaintTheFillColour()
    {
        // Rows from the top: 0 1 then 1 0; a stencil paints where the sample is 0.
        using Raster<Rgba8> raster = RenderCoreTests.Render("1 0 0 rg q 20 0 0 20 0 0 cm BI /W 2 /H 2 /IM true ID @\u0080 EI Q");
        using Raster<Rgba8> inverted = RenderCoreTests.Render("0 0 1 rg q 20 0 0 20 0 0 cm BI /W 2 /H 2 /IM true /D [1 0] ID @\u0080 EI Q");

        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(5, 5));
        Assert.Equal(White, raster.GetPixel(15, 5));
        Assert.Equal(White, raster.GetPixel(5, 15));
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(15, 15));
        Assert.Equal(new Rgba8(0, 0, 255, 255), inverted.GetPixel(15, 5));
    }

    [Fact]
    public void ImagesTakeConstantAlphaAndTheirOwnMasks()
    {
        PdfDictionary resources = new() { ["ExtGState"] = new PdfDictionary { ["A"] = new PdfDictionary { ["ca"] = new PdfReal(0.5) } } };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/A gs q 20 0 0 20 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0000 EI Q", resources: resources);

        Assert.InRange(raster.GetPixel(10, 10).R, 120, 135);
    }

    [Fact]
    public void BrokenImagesDrawNothing()
    {
        using Raster<Rgba8> raster = RenderCoreTests.Render("q 20 0 0 20 0 0 cm BI /W 4 /H 4 /CS /Nonsense /BPC 8 ID \u0000 EI Q 0 0 5 5 re f");

        Assert.Equal(White, raster.GetPixel(10, 5));
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(2, 17));
    }

    [Fact]
    public void JpegsDrawnSmallDecodeReducedAndAreCached()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 400, 400));
        using Raster<Rgba8> big = Raster.Create(800, 800, new Rgba8(200, 30, 30, 255));
        using (PdfCanvas canvas = new(page, new PdfCanvasOptions()))
        {
            canvas.DrawImage(big, new RectF(0, 0, 400, 400));
        }

        PdfImage jpeg = page.GetImages().Select(i => i.Image).First();
        PdfStream image = jpeg.Stream!;
        jpeg.ReplaceWithJpeg(Jpeg(big));

        using Raster<Rgba8> thumb = page.Render(PdfRenderOptions.FitTo(50, 50));
        Rgba8 middle = thumb.GetPixel(25, 25);
        Assert.InRange(middle.R, 190, 210);
        Assert.InRange(middle.G, 20, 40);

        // 800 pixels drawn 50 wide decode at an eighth; the full-size render decodes again at full size.
        Raster cached = pdf.RenderCache.Images.GetOrDecode(image, 8, long.MaxValue, () => throw new InvalidOperationException("Not cached."));
        Assert.Equal(100, cached.Width);
        using Raster<Rgba8> full = page.Render(new PdfRenderOptions { Dpi = 144 });
        Assert.Equal(800, pdf.RenderCache.Images.GetOrDecode(image, 1, long.MaxValue, () => throw new InvalidOperationException("Not cached.")).Width);
    }

    private static byte[] Jpeg(Raster<Rgba8> raster)
    {
        using Raster<Rgb8> rgb = raster.As<Rgb8>();
        using MemoryStream stream = new();
        rgb.Save(stream, Egami.Formats.Jpeg.JpegFormat.Info);
        return stream.ToArray();
    }

    private static Raster<Rgba8> Quarters()
    {
        Raster<Rgba8> raster = Raster.Create(4, 4, White);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                raster.SetPixel(x, y, new Rgba8(255, 0, 0, 255));
                raster.SetPixel(x + 2, y, new Rgba8(0, 255, 0, 255));
                raster.SetPixel(x, y + 2, new Rgba8(0, 0, 255, 255));
            }
        }

        return raster;
    }
}
