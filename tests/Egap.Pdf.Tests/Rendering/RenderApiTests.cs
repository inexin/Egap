using System.Text;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>The render API (Phase 4 task 3): resolution, fitting, boxes, rotation, user unit, region, background, pixel types and any canvas.</summary>
public sealed class RenderApiTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);
    private static readonly Rgba8 Red = new(255, 0, 0, 255);

    /// <summary>A 200 by 100 point page with a red 20 point square at its bottom left corner and a blue one at its top left.</summary>
    private static PdfPage Page(PdfDocument pdf, Action<PdfPage>? setup = null)
    {
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 200, 100));
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes("1 0 0 rg 0 0 20 20 re f 0 0 1 rg 0 80 20 20 re f")));
        setup?.Invoke(page);
        return page;
    }

    [Fact]
    public void RendersAtSeventyTwoDpiByDefault()
    {
        using PdfDocument pdf = PdfDocument.Create();
        using Raster<Rgba8> raster = Page(pdf).Render();

        Assert.Equal((200, 100), (raster.Width, raster.Height));
        Assert.Equal(Red, raster.GetPixel(10, 90));
        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(10, 10));
        Assert.Equal(White, raster.GetPixel(100, 50));
    }

    [Fact]
    public void ScalesWithDpiAndFitsInsideABox()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf);
        using Raster<Rgba8> big = page.Render(new PdfRenderOptions { Dpi = 144 });
        using Raster<Rgba8> odd = page.Render(new PdfRenderOptions { Dpi = 100 });
        using Raster<Rgba8> thumb = page.Render(PdfRenderOptions.FitTo(64, 64));

        Assert.Equal((400, 200), (big.Width, big.Height));
        Assert.Equal(Red, big.GetPixel(20, 180));
        Assert.Equal((278, 139), (odd.Width, odd.Height));
        Assert.Equal((64, 32), (thumb.Width, thumb.Height));
    }

    [Fact]
    public void TurnsByRotateAndTheExtraRotation()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, p => p.Rotate = 90);
        using Raster<Rgba8> ninety = page.Render();
        using Raster<Rgba8> half = page.Render(new PdfRenderOptions { Rotation = 90 });
        using Raster<Rgba8> full = page.Render(new PdfRenderOptions { Rotation = 270 });

        // Turned a quarter clockwise, the bottom left corner goes to the top left.
        Assert.Equal((100, 200), (ninety.Width, ninety.Height));
        Assert.Equal(Red, ninety.GetPixel(10, 10));
        Assert.Equal((200, 100), (half.Width, half.Height));
        Assert.Equal(Red, half.GetPixel(190, 10));
        Assert.Equal(Red, full.GetPixel(10, 90));
        Assert.Equal((100.0, 200.0), (page.DisplayWidth, page.DisplayHeight));
    }

    [Fact]
    public void HonoursTheBoxAndTheUserUnit()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, p => p.CropBox = new PdfRectangle(10, 10, 110, 60));
        using Raster<Rgba8> crop = page.Render();
        using Raster<Rgba8> media = page.Render(new PdfRenderOptions { Box = PdfPageBox.MediaBox });
        page.UserUnit = 2;
        using Raster<Rgba8> units = page.Render();

        Assert.Equal((100, 50), (crop.Width, crop.Height));
        Assert.Equal(Red, crop.GetPixel(5, 45));
        Assert.Equal(White, crop.GetPixel(15, 45));
        Assert.Equal((200, 100), (media.Width, media.Height));
        Assert.Equal((200, 100), (units.Width, units.Height));
        Assert.Equal(Red, units.GetPixel(10, 90));
    }

    [Fact]
    public void RendersARegionAsATile()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf);
        using Raster<Rgba8> whole = page.Render(new PdfRenderOptions { Dpi = 144 });
        using Raster<Rgba8> tile = page.Render(new PdfRenderOptions { Dpi = 144, Region = new RectF(0, 150, 64, 50) });

        Assert.Equal((64, 50), (tile.Width, tile.Height));
        for (int y = 0; y < tile.Height; y += 7)
        {
            for (int x = 0; x < tile.Width; x += 7)
            {
                Assert.Equal(whole.GetPixel(x, 150 + y), tile.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void BackgroundAndPixelTypes()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf);
        using Raster<Rgba8> clear = page.Render(new PdfRenderOptions { Background = new RgbaF32(0, 0, 0, 0) });
        using Raster<Gray8> gray = page.Render<Gray8>();
        using Raster<RgbaF32> floats = page.Render<RgbaF32>();

        Assert.Equal(0, clear.GetPixel(100, 50).A);
        Assert.Equal(Red, clear.GetPixel(10, 90));
        Assert.Equal(255, gray.GetPixel(100, 50).L);
        Assert.Equal(1f, floats.GetPixel(10, 90).R);
    }

    [Fact]
    public void DrawsOntoAnyCanvas()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf);
        PictureRecorder recorder = new(new RectF(0, 0, (float)page.DisplayWidth, (float)page.DisplayHeight));
        page.RenderTo(recorder);
        Picture picture = recorder.Finish();
        using Raster<Rgba8> replayed = picture.ToRaster<Rgba8>(2);
        using Raster<Rgba8> direct = page.Render(new PdfRenderOptions { Dpi = 144 });

        Assert.Equal((direct.Width, direct.Height), (replayed.Width, replayed.Height));
        Assert.Equal(direct.GetPixel(20, 180), replayed.GetPixel(20, 180));
        Assert.Equal(direct.GetPixel(200, 100), replayed.GetPixel(200, 100));
    }

    [Fact]
    public void RejectsBadOptions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRenderOptions { Dpi = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRenderOptions { Rotation = 45 });
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfRenderOptions.FitTo(0, 10));
        using PdfDocument pdf = PdfDocument.Create();
        Assert.Throws<PdfLimitException>(() => Page(pdf).Render(new PdfRenderOptions { Dpi = 1_000_000 }));
    }
}
