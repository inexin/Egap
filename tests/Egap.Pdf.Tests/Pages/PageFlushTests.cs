using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Page flush: Phase 3 task 16. Runs alone, as it measures the process's managed memory.</summary>
[Collection(nameof(MemoryMeasurement))]
public sealed class PageFlushTests(ITestOutputHelper output)
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
        string phase = "start";
        string peakInfo = "";
        long drawPeak = 0;
        long savePeak = 0;
        long compactPeak = 0;
        int samples = 0;
        using CancellationTokenSource stop = new();
        Thread sampler = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Live memory: a full collection first, so garbage the collector has not got to yet is not counted.
                string at = Volatile.Read(ref phase);
                long now = GC.GetTotalMemory(true);
                GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
                string gens = string.Join(" ", info.GenerationInfo.ToArray().Select((g, i) => $"g{i}={g.SizeAfterBytes / 1e6:F1}/{g.FragmentationAfterBytes / 1e6:F1}"));
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                long compacted = GC.GetTotalMemory(false);
                samples++;
                compactPeak = Math.Max(compactPeak, compacted);
                if (at == "save")
                {
                    savePeak = Math.Max(savePeak, now);
                }
                else
                {
                    drawPeak = Math.Max(drawPeak, now);
                }

                if (now > peak)
                {
                    peak = now;
                    peakInfo = $"phase {at}, compacted {compacted / 1e6:F1} MB, heap {info.HeapSizeBytes / 1e6:F1}, frag {info.FragmentedBytes / 1e6:F1}, gc#{info.Index} gen{info.Generation} compacted={info.Compacted} concurrent={info.Concurrent}, {gens}";
                }

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
                    Volatile.Write(ref phase, $"page {i}");
                }

                afterDrawing = GC.GetTotalMemory(true) - start;
                Volatile.Write(ref phase, "save");
                pdf.Save(file);
                Volatile.Write(ref phase, "after save");
            }

            stop.Cancel();
            sampler.Join();
            long size = new FileInfo(file).Length;
            output.WriteLine($"File {size:N0} bytes; managed memory after drawing {afterDrawing / 1e6:F1} MB, peak {(peak - start) / 1e6:F1} MB.");
            Assert.True(size > 60_000_000, $"The images should not compress away: {size:N0} bytes.");
            string config = string.Join(", ", GC.GetConfigurationVariables().Select(kv => $"{kv.Key}={kv.Value}"));
            Assert.Fail($"PROBE {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier} cpus {Environment.ProcessorCount} server {System.Runtime.GCSettings.IsServerGC} latency {System.Runtime.GCSettings.LatencyMode}; start {start / 1e6:F1} MB; peak {(peak - start) / 1e6:F1} MB ({peakInfo}); draw peak {(drawPeak - start) / 1e6:F1}, save peak {(savePeak - start) / 1e6:F1}, compacted peak {(compactPeak - start) / 1e6:F1}; after drawing {afterDrawing / 1e6:F1}; samples {samples}; GC config: {config}");
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

/// <summary>Tests that measure managed memory, run with no other test beside them.</summary>
[CollectionDefinition(DisableParallelization = true)]
public sealed class MemoryMeasurement;
