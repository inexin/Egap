using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.MemoryTests;

/// <summary>
/// Page flush's memory target (Phase 3 task 16). The test measures the whole process's managed memory, so it lives in a test
/// project of its own: in Egap.Pdf.Tests other tests were still running beside it and allocating, which on slower machines
/// pushed the measured peak over the target.
/// </summary>
public sealed class PageFlushMemoryTests(ITestOutputHelper output)
{
    private static readonly Typeface Noto = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));

    /// <summary>
    /// The design's memory target: 1,000 pages of text with an image each, flushed as they are drawn, generate and save in under
    /// 100 MB of managed memory, sampled as the live heap every 50 ms. Each image is 200 by 150 noisy pixels, about 80 KB written, so without flush the images alone
    /// would hold about 80 MB.
    /// </summary>
    [Fact]
    public void GeneratesAThousandPagesUnderTheMemoryTarget()
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egap-flush-{Guid.NewGuid():N}.pdf");
        long peak = 0;
        using CancellationTokenSource stop = new();
        Thread sampler = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Live memory: a full collection first, so garbage the collector has not got to yet is not counted.
                peak = Math.Max(peak, GC.GetTotalMemory(true));
                Thread.Sleep(50);
            }
        });
        GC.Collect();
        long start = GC.GetTotalMemory(true);
        sampler.Start();
        long afterDrawing;
        try
        {
            using (PdfDocument pdf = PdfDocument.Create())
            {
                Font font = new(Noto, 11);
                SolidPaint ink = new(new RgbaF32(0, 0, 0, 1));
                for (int i = 0; i < 1000; i++)
                {
                    PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 595, 842));
                    using (Raster<Rgba8> image = Photo(200, 150, i))
                    using (PdfCanvas canvas = new(page))
                    {
                        canvas.DrawImage(image, new RectF(72, 72, 300, 225));
                        for (int line = 0; line < 30; line++)
                        {
                            canvas.DrawText($"Page {i + 1}, line {line + 1}: the quick brown fox jumps over the lazy dog.", new Vector2(72, 340 + (line * 15)), font, ink);
                        }
                    }

                    page.Flush();
                }

                afterDrawing = GC.GetTotalMemory(true) - start;
                pdf.Save(file);
            }

            stop.Cancel();
            sampler.Join();
            long size = new FileInfo(file).Length;
            output.WriteLine($"File {size:N0} bytes; managed memory after drawing {afterDrawing / 1e6:F1} MB, peak {(peak - start) / 1e6:F1} MB.");
            Assert.True(size > 60_000_000, $"The images should not compress away: {size:N0} bytes.");
            Assert.True(peak - start < 100_000_000, $"Peak managed memory {(peak - start) / 1e6:F1} MB.");
            using PdfDocument reopened = PdfDocument.Open(file);
            Assert.Equal(1000, reopened.Pages.Count);
        }
        finally
        {
            stop.Cancel();
            File.Delete(file);
        }
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
