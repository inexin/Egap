using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Display lists, tiles and threads (Phase 4 task 18).</summary>
public sealed class RenderParallelTests
{
    private static readonly Typeface Noto = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));

    /// <summary>A saved and reopened document of 16 pages with text, shapes, a gradient and an image, so reads are lazy.</summary>
    private static byte[] Document()
    {
        using PdfDocument pdf = PdfDocument.Create();
        using Raster<Rgba8> image = Raster.Create(8, 8, new Rgba8(0, 128, 0, 255));
        for (int i = 0; i < 16; i++)
        {
            PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 200, 120));
            using PdfCanvas canvas = new(page);
            canvas.FillRect(new RectF(10, 10, 60, 40), new SolidPaint(new RgbaF32(i / 16f, 0, 1 - (i / 16f), 1)));
            canvas.FillRect(new RectF(80, 10, 100, 40), new LinearGradient(new Vector2(80, 0), new Vector2(180, 0), new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 0, 1, 1)));
            canvas.DrawText($"Page {i + 1}", new Vector2(10, 90), new Font(Noto, 24), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
            canvas.DrawImage(image, new RectF(150, 70, 40, 40));
        }

        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void PagesRenderTheSameInParallelAsOneByOne()
    {
        using PdfDocument pdf = PdfDocument.Open(Document());
        byte[][] sequential = [.. pdf.Pages.Select(p => Bytes(p.Render()))];

        using PdfDocument fresh = PdfDocument.Open(Document());
        byte[][] parallel = new byte[16][];
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => parallel[i] = Bytes(fresh.Pages[i].Render()));

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(sequential[i], parallel[i]);
        }
    }

    [Fact]
    public void APictureReplaysAsTheRenderAtAnyScale()
    {
        using PdfDocument pdf = PdfDocument.Open(Document());
        PdfPage page = pdf.Pages[3];
        Picture picture = page.ToPicture();
        using Raster<Rgba8> replayed = picture.ToRaster<Rgba8>(2);
        using Raster<Rgba8> direct = page.Render(new PdfRenderOptions { Dpi = 144 });

        Assert.Equal((direct.Width, direct.Height), (replayed.Width, replayed.Height));
        Assert.True(RenderTextTests.Differing(direct, replayed) < 0.002);

        // The picture replays on other threads too.
        Raster<Rgba8>[] copies = new Raster<Rgba8>[4];
        Parallel.For(0, 4, i => copies[i] = picture.ToRaster<Rgba8>(1));
        byte[] first = Bytes(copies[0]);
        Assert.All(copies, c => Assert.Equal(first, Bytes(c)));
        Array.ForEach(copies, c => c.Dispose());
    }

    [Fact]
    public void TilesPutTogetherMakeTheWholePage()
    {
        using PdfDocument pdf = PdfDocument.Open(Document());
        PdfPage page = pdf.Pages[0];
        using Raster<Rgba8> whole = page.Render(new PdfRenderOptions { Dpi = 144 });
        for (int ty = 0; ty < whole.Height; ty += 100)
        {
            for (int tx = 0; tx < whole.Width; tx += 128)
            {
                using Raster<Rgba8> tile = page.Render(new PdfRenderOptions { Dpi = 144, Region = new RectF(tx, ty, 128, 100) });
                for (int y = 0; y < tile.Height && ty + y < whole.Height; y += 9)
                {
                    for (int x = 0; x < tile.Width && tx + x < whole.Width; x += 9)
                    {
                        Assert.Equal(whole.GetPixel(tx + x, ty + y), tile.GetPixel(x, y));
                    }
                }
            }
        }
    }

    private static byte[] Bytes(Raster<Rgba8> raster)
    {
        byte[] bytes = new byte[raster.Width * raster.Height * 4];
        raster.CopyPixelsTo(bytes);
        return bytes;
    }
}
