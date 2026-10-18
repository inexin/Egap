using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Page flush: Phase 3 task 16. The memory target is tested in Egap.Pdf.MemoryTests, in a process of its own.</summary>
public sealed class PageFlushTests
{
    private static readonly Typeface Noto = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));

    [Fact]
    public void AFlushedPageSavesAndReadsAsBefore()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 200, 200));
        using Raster<Rgba8> image = Photo(64, 48, 1);
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawImage(image, new RectF(10, 10, 128, 96));
            canvas.DrawText("Flushed", new Vector2(10, 150), new Font(Noto, 20), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        }

        byte[] content = ((PdfStream)page.Dictionary["Contents"]!.Resolve()).GetDecodedBytes();
        PdfStream xObject = (PdfStream)Assert.Single(page.Resources!.Get<PdfDictionary>("XObject")!.Entries).Value.Resolve();
        byte[] samples = xObject.GetDecodedBytes();
        page.Flush();

        // Read back from the scratch file, before and after a save.
        Assert.Equal(samples, xObject.GetDecodedBytes());
        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        PdfPage again = reopened.Pages[0];
        Assert.Equal(content, ((PdfStream)again.Dictionary["Contents"]!.Resolve()).GetDecodedBytes());
        Assert.Equal(samples, ((PdfStream)Assert.Single(again.Resources!.Get<PdfDictionary>("XObject")!.Entries).Value.Resolve()).GetDecodedBytes());
        Assert.NotNull(again.Resources!.Get<PdfDictionary>("Font"));
    }

    /// <summary>A gradient with noise on it, different for every seed, so it does not compress much.</summary>
    private static Raster<Rgba8> Photo(int width, int height, int seed)
    {
        Random random = new(seed);
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)((x * 255 / width) ^ random.Next(32)), (byte)((y * 255 / height) ^ random.Next(32)), (byte)random.Next(256), 255);
            }
        }

        return raster;
    }
}
