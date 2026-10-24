using Egami;
using Egami.Drawing;
using Egami.Ocr;
using Egami.Pixels;

using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Model;

/// <summary>Scanned pages read through an OCR engine on the async load (hub step 5.7, Build Plan task 9, design Q2).</summary>
public sealed class OcrModelTests
{
    /// <summary>Reads two lines at fixed places of whatever image it gets, or fails when told to.</summary>
    private sealed class FakeEngine(bool fail = false) : IOcrEngine
    {
        public OcrEngineInfo Info { get; } = new("fake");

        public int Calls { get; private set; }

        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (fail)
            {
                throw new OcrEngineException("No engine here.");
            }

            float w = request.Image.Width, h = request.Image.Height;
            OcrLine Line(float top, params string[] words) => new([.. words.Select((word, i) =>
                new OcrWord(word, OcrQuad.FromBounds((0.1f + (i * 0.15f)) * w, top * h, 0.13f * w, 0.02f * h), 0.9))]);
            return Task.FromResult(new OcrResult([Line(0.1f, "Scanned", "minutes"), Line(0.15f, "of", "the", "meeting")]));
        }
    }

    private static byte[] Scan()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 595, 842));
        using (PdfCanvas canvas = new(page))
        {
            using Raster<Rgba8> scan = Raster.Create<Rgba8>(60, 80, new Rgba8(240, 240, 230, 255));
            canvas.DrawImage(scan, new RectF(0, 0, 595, 842));
        }

        using MemoryStream saved = new();
        created.Save(saved);
        return saved.ToArray();
    }

    [Fact]
    public async Task LoadPdfAsyncTurnsAScanIntoText()
    {
        byte[] bytes = Scan();
        FakeEngine engine = new();
        List<PdfToDocumentWarning> warnings = [];

        Document model = await Document.LoadPdfAsync(new MemoryStream(bytes), new PdfToDocumentOptions { OcrEngine = engine, OnWarning = warnings.Add }, TestContext.Current.CancellationToken);

        Assert.Equal(1, engine.Calls);
        Assert.Empty(warnings);
        Assert.Empty(model.Descendants<Documents.Picture>());
        string text = model.GetText();
        Assert.Contains("Scanned minutes", text, StringComparison.Ordinal);
        Assert.Contains("of the meeting", text, StringComparison.Ordinal);

        // The PDF itself is not changed, and the sync load still keeps the picture.
        using PdfDocument pdf = PdfDocument.Open(bytes);
        Assert.Equal("", pdf.Pages[0].ExtractText().Trim());
        Document sync = Document.LoadPdf(new MemoryStream(bytes), new PdfToDocumentOptions { OcrEngine = engine });
        Assert.Single(sync.Descendants<Documents.Picture>());
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task AFailedEngineKeepsThePictureWithWarnings()
    {
        List<PdfToDocumentWarning> warnings = [];

        Document model = await Document.LoadPdfAsync(new MemoryStream(Scan()), new PdfToDocumentOptions { OcrEngine = new FakeEngine(fail: true), OnWarning = warnings.Add },
            TestContext.Current.CancellationToken);

        Assert.Equal([PdfToDocumentWarningKind.OcrFailed, PdfToDocumentWarningKind.ScannedPage], warnings.Select(w => w.Kind));
        Assert.Contains("No engine here.", warnings[0].Message, StringComparison.Ordinal);
        Assert.Single(model.Descendants<Documents.Picture>());
    }
}
