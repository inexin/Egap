using System.Diagnostics;
using System.Numerics;

using Egami.Ocr;

namespace Egap.Pdf.Ocr;

/// <summary>
/// Makes scanned PDFs searchable through any OCR engine (an <see cref="IOcrEngine"/> from Egami.Ocr): each page's image goes
/// to the engine, and the words that come back are written over the page as invisible text, placed and scaled so selection
/// follows the scan. Egap ships no engine; plug in <see cref="CommandOcrEngine"/> for a program such as Tesseract, or your
/// own class around an OS or cloud service.
/// </summary>
/// <remarks>
/// The document is changed in place and saved by the caller. A page that is one scanned image is read as its own pixels;
/// other pages are rendered. Pages go to the engine several at a time when it is thread-safe. A signed document is refused
/// unless <see cref="PdfOcrOptions.AllowInvalidatingSignatures"/>.
/// </remarks>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("scan.pdf");
/// CommandOcrEngine tesseract = new("tesseract {image} {output} -l {langs} --dpi {dpi} hocr", new CommandOcrOptions { Output = OcrOutputFormat.Hocr });
/// PdfOcrReport report = await PdfOcr.MakeSearchableAsync(pdf, tesseract, new PdfOcrOptions { PdfA = PdfAConformance.PdfA2b });
/// pdf.Save("searchable.pdf");
/// </code>
/// </example>
public static class PdfOcr
{
    /// <summary>The share of an OCR word's box that existing text must cover for the word to count as already on the page.</summary>
    private const double Duplicate = 0.5;

    /// <summary>Recognizes the text of a document's pages and adds it as an invisible layer.</summary>
    /// <param name="document">The document, changed in place.</param>
    /// <param name="engine">The OCR engine.</param>
    /// <param name="options">Which pages, how and with what limits; <see cref="PdfOcrOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Cancels the run; pages already done keep their text.</param>
    /// <returns>What happened to each page.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A page in <see cref="PdfOcrOptions.Pages"/> is not in the document.</exception>
    /// <exception cref="PdfLimitException">More pages than <see cref="PdfOcrOptions.MaxPages"/>.</exception>
    /// <exception cref="PdfException">The document is signed and <see cref="PdfOcrOptions.AllowInvalidatingSignatures"/> is not set.</exception>
    public static async Task<PdfOcrReport> MakeSearchableAsync(PdfDocument document, IOcrEngine engine, PdfOcrOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(engine);
        options ??= PdfOcrOptions.Default;
        List<int> pages = Pages(document, options);
        if (!options.AllowInvalidatingSignatures && document.Signatures.Count > 0)
        {
            throw new PdfException("The document is signed, and adding a text layer would break its signatures; set AllowInvalidatingSignatures to go ahead.");
        }

        document.SignaturesMayBreak |= options.AllowInvalidatingSignatures;
        OcrEngineInfo info = engine.Info;
        int degree = !info.IsThreadSafe ? 1 : options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Environment.ProcessorCount;
        OcrEngineLimits limits = new(info.MaxImageDimension);
        PdfOcrPageReport[] reports = new PdfOcrPageReport[pages.Count];

        // The document is read and written by one page at a time; only the engine's work runs side by side. One page more
        // than the engine takes is in flight, so the next page's image is ready when an engine call ends.
        using SemaphoreSlim gate = new(1, 1);
        using SemaphoreSlim engines = new(degree, degree);
        int done = 0;
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, pages.Count), new ParallelOptions { MaxDegreeOfParallelism = degree + 1, CancellationToken = cancellationToken }, async (i, token) =>
            {
                reports[i] = await Page(document, pages[i], engine, limits, options, gate, engines, token).ConfigureAwait(false);
                options.Progress?.Report(Interlocked.Increment(ref done));
            }).ConfigureAwait(false);
        }
        finally
        {
            document.ResetRenderCache();
        }

        PdfOcrReport report = new(reports);
        if (options.PdfA is PdfAConformance level && level != PdfAConformance.None)
        {
            report = report with { PdfA = PdfAConverter.Convert(document, level) };
        }

        return report;
    }

    private static List<int> Pages(PdfDocument document, PdfOcrOptions options)
    {
        int count = document.Pages.Count;
        List<int> pages = options.Pages is { } chosen ? [.. chosen.Resolve(count).Distinct().Order()] : [.. Enumerable.Range(0, count)];
        if (pages.Count > 0 && (pages[0] < 0 || pages[^1] >= count))
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"The document has {count} pages; page indices run from 0 to {count - 1}.");
        }

        if (pages.Count > options.MaxPages)
        {
            throw new PdfLimitException(nameof(PdfOcrOptions.MaxPages), $"{pages.Count} pages is more than the {options.MaxPages} one run looks at.");
        }

        return pages;
    }

    /// <summary>One page: decided, read by the engine, written (design Q4 to Q6).</summary>
    private static async Task<PdfOcrPageReport> Page(PdfDocument document, int index, IOcrEngine engine, OcrEngineLimits limits, PdfOcrOptions options,
        SemaphoreSlim gate, SemaphoreSlim engines, CancellationToken cancellationToken)
    {
        OcrImage? image;
        int removed = 0;
        string? skip = null;
        IReadOnlyList<PdfRectangle> existing = [];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PdfPage page = document.Pages[index];
            PageTextCounter text = PageTextCounter.Of(page);
            switch (options.Mode)
            {
                case PdfOcrMode.Skip when text.Visible > 0:
                    skip = "The page has text.";
                    break;
                case PdfOcrMode.Skip when text.Invisible > 0:
                    skip = "The page has an invisible text layer; Redo replaces it.";
                    break;
                case PdfOcrMode.Redo when text.Invisible > 0:
                    removed = InvisibleTextRemover.Remove(page);
                    document.ResetRenderCache();
                    break;
                case PdfOcrMode.Redo when text.Visible > 0:
                    skip = "The page has text and no invisible layer to redo.";
                    break;
            }

            if (skip is not null)
            {
                return new PdfOcrPageReport(index, PdfOcrPageStatus.Skipped) { Message = skip };
            }

            bool hasText = text.Visible > 0 || (options.Mode == PdfOcrMode.Force && text.Invisible > 0);
            if (hasText)
            {
                existing = [.. page.GetText().Lines.SelectMany(l => l.Words).Select(w => w.Box)];
            }

            image = OcrImage.For(page, hasText, limits, options, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        using (image)
        {
            OcrRequest request = new(image.Image) { Dpi = image.Dpi, Languages = options.Languages, PageIndex = index };
            await engines.WaitAsync(cancellationToken).ConfigureAwait(false);
            Stopwatch watch = Stopwatch.StartNew();
            OcrResult result;
            try
            {
                result = await engine.RecognizeAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!options.StopOnError && e is not OperationCanceledException)
            {
                return new PdfOcrPageReport(index, PdfOcrPageStatus.Failed) { Message = e.Message, EngineTime = watch.Elapsed, Source = image.Source, Dpi = image.Dpi, RemovedGlyphs = removed };
            }
            finally
            {
                engines.Release();
            }

            TimeSpan engineTime = watch.Elapsed;
            OcrResult kept = existing.Count > 0 ? WithoutExisting(result, image.ImageToPage, existing) : result;
            List<double> confidences = [.. kept.Words.Where(w => w.Confidence is not null && w.Confidence >= options.MinConfidence).Select(w => w.Confidence!.Value)];
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                PdfPage page = document.Pages[index];
                Matrix3x2 m = image.ImageToPage;
                int words = PdfOcrLayer.AddResult(page, kept, new PdfMatrix(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32), options.MinConfidence);
                int rotation = ((result.Rotation % 360) + 360) % 360;
                if (options.RotatePages && rotation is 90 or 180 or 270)
                {
                    page.Rotate = (page.Rotate + rotation) % 360;
                }

                return new PdfOcrPageReport(index, PdfOcrPageStatus.Recognized)
                {
                    Words = words,
                    MeanConfidence = confidences.Count > 0 ? confidences.Average() : null,
                    EngineTime = engineTime,
                    Source = image.Source,
                    Dpi = image.Dpi,
                    Rotation = rotation,
                    RemovedGlyphs = removed,
                };
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>The engine's words without those the page already has as text, so Force and Redo do not add a word twice.</summary>
    private static OcrResult WithoutExisting(OcrResult result, Matrix3x2 imageToPage, IReadOnlyList<PdfRectangle> existing)
    {
        List<OcrLine> lines = [];
        foreach (OcrLine line in result.Lines)
        {
            List<OcrWord> words = [.. line.Words.Where(word => !Covered(word.Box.Transform(imageToPage), existing))];
            if (words.Count > 0)
            {
                lines.Add(line with { Words = words });
            }
        }

        return result with { Lines = lines };
    }

    private static bool Covered(OcrQuad quad, IReadOnlyList<PdfRectangle> existing)
    {
        Vector2[] corners = [quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft];
        PdfRectangle box = new(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
        double area = box.Width * box.Height;
        if (area <= 0)
        {
            return false;
        }

        double covered = 0;
        foreach (PdfRectangle text in existing)
        {
            if (box.Intersect(text) is { IsEmpty: false } overlap)
            {
                covered += overlap.Width * overlap.Height;
            }
        }

        return covered >= area * Duplicate;
    }
}
