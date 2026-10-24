using System.Collections.Concurrent;
using System.Numerics;
using System.Text;

using Egami;
using Egami.Ocr;
using Egami.Pixels;

using Egap.Pdf.Ocr;

namespace Egap.Pdf.Tests.Ocr;

/// <summary>The OCR engine driver (hub step 5.7, Build Plan tasks 8 to 13), with a fake engine in place of a real one.</summary>
public sealed class PdfOcrTests
{
    /// <summary>A letter page as a 300 dpi scan.</summary>
    private const int ScanWidth = 2550, ScanHeight = 3300;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A word at a place given as fractions of the image (left, top, right, bottom), so it lands the same on any image of the page.</summary>
    private sealed record Word(string Text, float Left, float Top, float Right, float Bottom, double? Confidence = null, int Angle = 0);

    /// <summary>An engine that "reads" the words it is given and records what it was sent.</summary>
    private sealed class FakeEngine(IReadOnlyList<Word> words, OcrEngineInfo? info = null) : IOcrEngine
    {
        private int running;

        public OcrEngineInfo Info { get; } = info ?? new OcrEngineInfo("fake");

        public ConcurrentBag<OcrRequest> Requests { get; } = [];

        public int MostAtOnce { get; private set; }

        public Func<OcrRequest, Exception?>? Fail { get; init; }

        public int Rotation { get; init; }

        public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            int now = Interlocked.Increment(ref running);
            lock (Requests)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
            }

            try
            {
                Requests.Add(request);
                await Task.Delay(20, cancellationToken);
                if (Fail?.Invoke(request) is Exception e)
                {
                    throw e;
                }

                float w = request.Image.Width, h = request.Image.Height;
                List<OcrWord> found = [.. words.Select(word => new OcrWord(word.Text,
                    OcrQuad.FromBounds(word.Left * w, word.Top * h, (word.Right - word.Left) * w, (word.Bottom - word.Top) * h, word.Angle), word.Confidence, "en"))];
                return new OcrResult([new OcrLine(found)]) { Rotation = Rotation, ImageSize = new Size((int)w, (int)h) };
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }
    }

    private static readonly Word[] TwoWords = [new("Quarterly", 0.1f, 0.1f, 0.3f, 0.12f, 0.9), new("report", 0.32f, 0.1f, 0.45f, 0.12f, 0.7)];

    /// <summary>A PDF of scanned pages: each a 300 dpi image of the whole letter page, with dark bars where text would be.</summary>
    private static PdfDocument Scans(int pages = 1, int dpi = 300)
    {
        int width = ScanWidth * dpi / 300, height = ScanHeight * dpi / 300;
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height, new Rgb8(255, 255, 255));
        for (int y = height / 10; y < height * 12 / 100; y++)
        {
            raster.Row(y)[(width / 10)..(width * 45 / 100)].Fill(new Rgb8(20, 20, 20));
        }

        using MemoryStream png = new();
        raster.Save(png, "png");
        ReadOnlyMemory<byte> bytes = png.ToArray();
        return PdfDocument.FromImages(Enumerable.Repeat(bytes, pages), new PdfImagePageOptions { Dpi = dpi });
    }

    /// <summary>Adds visible Helvetica text to a page.</summary>
    private static void AddText(PdfPage page, string text, double x, double y)
    {
        PdfDictionary resources = page.Resources ?? [];
        PdfDictionary fonts = resources.Get<PdfDictionary>("Font") ?? [];
        fonts["Helv"] = page.Document.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        resources["Font"] = fonts;
        page.Dictionary["Resources"] = resources;
        PdfContent content = page.GetContent();
        foreach (PdfOperation op in PdfContent.Parse(Encoding.ASCII.GetBytes(FormattableString.Invariant($"BT /Helv 20 Tf {x} {y} Td ({text}) Tj ET"))))
        {
            content.Add(op);
        }

        page.SetContent(content);
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }

    private static string Words(PdfPage page) => string.Join(' ', page.GetText().Lines.SelectMany(l => l.Words).Select(w => w.Text));

    private static double Overlap(PdfRectangle a, PdfRectangle b)
    {
        PdfRectangle both = a.Intersect(b);
        return both.IsEmpty ? 0 : both.Width * both.Height / (b.Width * b.Height);
    }

    [Fact]
    public async Task AScanGetsTheEnginesWordsWhereItFoundThem()
    {
        using PdfDocument pdf = Scans();
        FakeEngine engine = new(TwoWords);

        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, engine, cancellationToken: Cancel);

        PdfOcrPageReport page = Assert.Single(report.Pages);
        Assert.Equal(PdfOcrPageStatus.Recognized, page.Status);
        Assert.Equal(2, page.Words);
        Assert.Equal(PdfOcrImageSource.PageImage, page.Source);
        Assert.Equal(300, page.Dpi, 1);
        Assert.Equal(0.8, page.MeanConfidence!.Value, 3);
        OcrRequest request = Assert.Single(engine.Requests);
        Assert.Equal((ScanWidth, ScanHeight), (request.Image.Width, request.Image.Height));
        Assert.Equal(300, request.Dpi, 1);

        using PdfDocument back = Reopen(pdf);
        Assert.Equal("Quarterly report", Words(back.Pages[0]));

        // Each word's text box covers the engine's box: Quarterly from 10 % to 30 % across, 10 % to 12 % down a letter page.
        PdfTextWord quarterly = back.Pages[0].GetText().Lines[0].Words[0];
        PdfRectangle expected = new(0.1 * 612, 792 * 0.88, 0.3 * 612, 792 * 0.9);
        Assert.True(Overlap(quarterly.Box, expected) >= 0.9, $"{quarterly.Box} against {expected}");
        Assert.True(Overlap(expected, quarterly.Box) >= 0.9, $"{quarterly.Box} against {expected}");
    }

    [Fact]
    public async Task SharedAndIndirectResourcesAreLeftAsTheyWere()
    {
        // Found by the corpus gate: resources held by reference, or a font dictionary shared with other pages.
        using PdfDocument pdf = Scans(2);
        PdfDictionary shared = pdf.Pages[0].Resources!.Clone();
        shared["Font"] = pdf.Objects.Add(new PdfDictionary());
        PdfReference resources = pdf.Objects.Add(shared);
        pdf.Pages[0].Dictionary["Resources"] = resources;
        pdf.Pages[1].Dictionary["Resources"] = resources;

        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords), new PdfOcrOptions { Pages = [0] }, Cancel);

        Assert.Equal(2, report.Words);
        Assert.Empty(((PdfDictionary)shared["Font"]!.Resolve()).Entries);
        using PdfDocument back = Reopen(pdf);
        Assert.Equal("Quarterly report", Words(back.Pages[0]));
        Assert.Equal("", Words(back.Pages[1]));
    }

    [Fact]
    public async Task LowResolutionScansAndMixedPagesAreRendered()
    {
        using PdfDocument fax = Scans(dpi: 100);
        FakeEngine engine = new(TwoWords);
        PdfOcrPageReport low = Assert.Single((await PdfOcr.MakeSearchableAsync(fax, engine, new PdfOcrOptions { Dpi = 200 }, Cancel)).Pages);
        Assert.Equal(PdfOcrImageSource.Rendered, low.Source);
        Assert.Equal(200, low.Dpi);
        Assert.Equal(1700, Assert.Single(engine.Requests).Image.Width);

        using PdfDocument mixed = Scans();
        AddText(mixed.Pages[0], "Stamped", 300, 100);
        PdfOcrPageReport forced = Assert.Single((await PdfOcr.MakeSearchableAsync(mixed, new FakeEngine(TwoWords), new PdfOcrOptions { Mode = PdfOcrMode.Force }, Cancel)).Pages);
        Assert.Equal(PdfOcrImageSource.Rendered, forced.Source);
        Assert.Equal(PdfOcrPageStatus.Recognized, forced.Status);
    }

    [Fact]
    public async Task SkipLeavesPagesWithTextAlone()
    {
        using PdfDocument pdf = Scans(3);
        AddText(pdf.Pages[0], "Born digital", 72, 700);
        PdfOcrLayer.AddLines(pdf.Pages[1], [new PdfOcrLine([new PdfOcrWord("Oldword", new PdfRectangle(72, 700, 200, 720))])]);

        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords), cancellationToken: Cancel);

        Assert.Equal([PdfOcrPageStatus.Skipped, PdfOcrPageStatus.Skipped, PdfOcrPageStatus.Recognized], report.Pages.Select(p => p.Status));
        Assert.Contains("text", report.Pages[0].Message, StringComparison.Ordinal);
        Assert.Contains("Redo", report.Pages[1].Message, StringComparison.Ordinal);
        Assert.Equal(2, report.Words);
    }

    [Fact]
    public async Task RedoReplacesAnEarlierLayerAndKeepsVisibleText()
    {
        using PdfDocument pdf = Scans(2);
        PdfOcrLayer.AddLines(pdf.Pages[0], [new PdfOcrLine([new PdfOcrWord("Oldword", new PdfRectangle(72, 700, 200, 720))])]);
        AddText(pdf.Pages[1], "Visible", 72, 100);

        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords), new PdfOcrOptions { Mode = PdfOcrMode.Redo }, Cancel);

        Assert.Equal(PdfOcrPageStatus.Recognized, report.Pages[0].Status);
        Assert.Equal(7, report.Pages[0].RemovedGlyphs);
        Assert.Equal(PdfOcrPageStatus.Skipped, report.Pages[1].Status);
        using PdfDocument back = Reopen(pdf);
        Assert.Equal("Quarterly report", Words(back.Pages[0]));
        Assert.Equal("Visible", Words(back.Pages[1]));
    }

    [Fact]
    public async Task ForceKeepsTextAndDoesNotAddItsWordsTwice()
    {
        using PdfDocument pdf = Scans();

        // "Visible" sits where the engine will find "Quarterly": 10 % across, 10 % to 12 % down.
        AddText(pdf.Pages[0], "Visible words over it", 0.1 * 612, (792 * 0.88) + 2);

        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords), new PdfOcrOptions { Mode = PdfOcrMode.Force }, Cancel);

        Assert.Equal(1, report.Words);
        using PdfDocument back = Reopen(pdf);
        string text = Words(back.Pages[0]);
        Assert.Contains("Visible", text, StringComparison.Ordinal);
        Assert.Contains("report", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotatedWordsGetARotatedTextMatrix()
    {
        using PdfDocument pdf = Scans();
        Word up = new("Upward", 0.05f, 0.4f, 0.07f, 0.7f, Angle: 90);

        await PdfOcr.MakeSearchableAsync(pdf, new FakeEngine([up]), cancellationToken: Cancel);

        using PdfDocument back = Reopen(pdf);
        PdfTextWord word = Assert.Single(back.Pages[0].GetText().Lines.SelectMany(l => l.Words));
        Assert.Equal("Upward", word.Text);
        PdfRectangle expected = new(0.05 * 612, 792 * 0.3, 0.07 * 612, 792 * 0.6);
        Assert.True(Overlap(word.Box, expected) >= 0.9 && Overlap(expected, word.Box) >= 0.9, $"{word.Box} against {expected}");
    }

    [Fact]
    public async Task LowConfidenceWordsAreDroppedAndRotationCanTurnThePage()
    {
        using PdfDocument pdf = Scans();

        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords) { Rotation = 90 },
            new PdfOcrOptions { MinConfidence = 0.8, RotatePages = true }, Cancel);

        Assert.Equal(1, report.Words);
        Assert.Equal(0.9, report.Pages[0].MeanConfidence!.Value, 3);
        Assert.Equal(90, report.Pages[0].Rotation);
        Assert.Equal(90, pdf.Pages[0].Rotate);
    }

    [Fact]
    public async Task LargeImagesAreScaledToTheEnginesLimitAndBoxesBack()
    {
        using PdfDocument pdf = Scans();
        FakeEngine engine = new(TwoWords, new OcrEngineInfo("small") { MaxImageDimension = 1000 });

        PdfOcrPageReport page = Assert.Single((await PdfOcr.MakeSearchableAsync(pdf, engine, cancellationToken: Cancel)).Pages);

        OcrRequest request = Assert.Single(engine.Requests);
        Assert.Equal(1000, Math.Max(request.Image.Width, request.Image.Height));
        Assert.True(page.Dpi < 100, $"{page.Dpi}");
        using PdfDocument back = Reopen(pdf);
        PdfTextWord quarterly = back.Pages[0].GetText().Lines[0].Words[0];
        Assert.True(Overlap(quarterly.Box, new PdfRectangle(0.1 * 612, 792 * 0.88, 0.3 * 612, 792 * 0.9)) >= 0.9);
    }

    [Fact]
    public async Task PagesRunInParallelOnlyWhenTheEngineAllowsIt()
    {
        using PdfDocument many = Scans(6);
        FakeEngine safe = new(TwoWords);
        List<int> progress = [];
        await PdfOcr.MakeSearchableAsync(many, safe, new PdfOcrOptions { MaxDegreeOfParallelism = 3, Progress = new SyncProgress(progress) }, Cancel);
        Assert.InRange(safe.MostAtOnce, 1, 3);
        Assert.Equal([1, 2, 3, 4, 5, 6], progress.Order());

        using PdfDocument more = Scans(4);
        FakeEngine single = new(TwoWords, new OcrEngineInfo("single") { IsThreadSafe = false });
        await PdfOcr.MakeSearchableAsync(more, single, new PdfOcrOptions { MaxDegreeOfParallelism = 4 }, Cancel);
        Assert.Equal(1, single.MostAtOnce);
    }

    [Fact]
    public async Task AFailedPageIsReportedUnlessStopOnError()
    {
        using PdfDocument pdf = Scans(3);
        FakeEngine engine = new(TwoWords) { Fail = r => r.PageIndex == 1 ? new OcrEngineException("The engine gave up.", 2) : null };

        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, engine, cancellationToken: Cancel);
        Assert.Equal([PdfOcrPageStatus.Recognized, PdfOcrPageStatus.Failed, PdfOcrPageStatus.Recognized], report.Pages.Select(p => p.Status));
        Assert.Equal("The engine gave up.", report.Pages[1].Message);

        using PdfDocument again = Scans(3);
        await Assert.ThrowsAsync<OcrEngineException>(() => PdfOcr.MakeSearchableAsync(again, engine, new PdfOcrOptions { StopOnError = true }, Cancel));
    }

    [Fact]
    public async Task PageRangesLimitsCancellationAndSignatures()
    {
        using PdfDocument pdf = Scans(3);
        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords), new PdfOcrOptions { Pages = [2, 0] }, Cancel);
        Assert.Equal([0, 2], report.Pages.Select(p => p.PageIndex));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords), new PdfOcrOptions { Pages = [3] }, Cancel));
        await Assert.ThrowsAsync<PdfLimitException>(() => PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords), new PdfOcrOptions { MaxPages = 2 }, Cancel));

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfOcr.MakeSearchableAsync(Scans(2), new FakeEngine(TwoWords), cancellationToken: cancelled.Token));

        using PdfDocument signed = Scans();
        PdfDictionary signature = new()
        {
            ["Type"] = PdfName.Get("Sig"),
            ["Filter"] = PdfName.Get("Adobe.PPKLite"),
            ["SubFilter"] = PdfName.Get("adbe.pkcs7.detached"),
            ["ByteRange"] = new PdfArray { new PdfInteger(0), new PdfInteger(10), new PdfInteger(20), new PdfInteger(10) },
            ["Contents"] = PdfString.FromBytes(new byte[16], hex: true),
        };
        PdfDictionary field = new() { ["FT"] = PdfName.Get("Sig"), ["T"] = PdfString.FromText("Signature1"), ["V"] = signed.Objects.Add(signature) };
        signed.Catalog["AcroForm"] = new PdfDictionary { ["Fields"] = new PdfArray { signed.Objects.Add(field) }, ["SigFlags"] = new PdfInteger(3) };
        using PdfDocument reopened = Reopen(signed);
        await Assert.ThrowsAsync<PdfException>(() => PdfOcr.MakeSearchableAsync(reopened, new FakeEngine(TwoWords), cancellationToken: Cancel));
        PdfOcrReport allowed = await PdfOcr.MakeSearchableAsync(reopened, new FakeEngine(TwoWords), new PdfOcrOptions { AllowInvalidatingSignatures = true }, Cancel);
        Assert.Equal(2, allowed.Words);
    }

    [Fact]
    public async Task PdfAIsMadeInTheSamePassWhenAskedFor()
    {
        using PdfDocument pdf = Scans();

        PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, new FakeEngine(TwoWords), new PdfOcrOptions { PdfA = PdfAConformance.PdfA2b }, Cancel);

        Assert.NotNull(report.PdfA);
        Assert.True(report.PdfA.Succeeded, string.Join("; ", report.PdfA.Unmet.Select(i => i.ToString())));
        using PdfDocument back = Reopen(pdf);
        Assert.True(PdfAConverter.Check(back, PdfAConformance.PdfA2b).Succeeded);
        Assert.Equal("Quarterly report", Words(back.Pages[0]));
    }

    [Fact]
    public async Task DeskewTurnsTheEnginesCopyAndMapsWordsBack()
    {
        // A scan whose text lines run 3 degrees off level.
        int width = 1275, height = 1650;
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height, new Rgb8(255, 255, 255));
        double slope = Math.Tan(3 * Math.PI / 180);
        for (int line = 0; line < 30; line++)
        {
            int top = 200 + (line * 40);
            for (int x = 150; x < 1100; x++)
            {
                int y = top + (int)(x * slope);
                for (int t = 0; t < 12 && y + t < height; t++)
                {
                    raster.Row(y + t)[x] = new Rgb8(10, 10, 10);
                }
            }
        }

        using MemoryStream png = new();
        raster.Save(png, "png");
        using PdfDocument pdf = PdfDocument.FromImages([(ReadOnlyMemory<byte>)png.ToArray()], new PdfImagePageOptions { Dpi = 150 });

        // The engine names a word at the centre of what it sees: the turn is about the centre, so it lands at the page's.
        FakeEngine engine = new([new Word("Centre", 0.45f, 0.49f, 0.55f, 0.51f)]);
        await PdfOcr.MakeSearchableAsync(pdf, engine, new PdfOcrOptions { Deskew = true }, Cancel);

        OcrRequest request = Assert.Single(engine.Requests);
        Assert.Equal((width, height), (request.Image.Width, request.Image.Height));
        using PdfDocument back = Reopen(pdf);
        PdfTextWord word = Assert.Single(back.Pages[0].GetText().Lines.SelectMany(l => l.Words));
        PdfRectangle page = back.Pages[0].MediaBox;
        Vector2 centre = new((float)((word.Box.Left + word.Box.Right) / 2), (float)((word.Box.Bottom + word.Box.Top) / 2));
        Assert.Equal(page.Width / 2, centre.X, 0.5);
        Assert.Equal(page.Height / 2, centre.Y, 0.5);
    }

    /// <summary>A progress that reports on the calling thread, so the test sees every report before the run returns.</summary>
    private sealed class SyncProgress(List<int> seen) : IProgress<int>
    {
        public void Report(int value)
        {
            lock (seen)
            {
                seen.Add(value);
            }
        }
    }
}
