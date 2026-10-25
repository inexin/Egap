using System.CommandLine;

using Egami.Ocr;

using Egap.Cli.Host;
using Egap.Pdf;
using Egap.Pdf.Ocr;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap ocr</c> and the <c>ocr</c> step: scanned pages made searchable through an OCR program the user names, such as
/// Tesseract, run for each page image and read back as hOCR or ALTO. Egap ships no engine; the output is PDF/A-2b unless
/// <c>--pdfa none</c>, as OCRmyPDF users expect.
/// </summary>
internal sealed class OcrEdit : Edit
{
    private readonly Option<string> _engine = new("--engine-command")
    {
        Description = "The OCR program and its arguments, run once per page: {image} is the page image (PNG), {output} the result file without extension, {langs} the languages, {dpi} the resolution, {page} the page from 0. It must write hOCR or ALTO, such as \"tesseract {image} {output} -l {langs} --dpi {dpi} hocr\".",
        HelpName = "command",
        Required = true,
    };

    private readonly Option<string?> _languages = new("--lang", "-l") { Description = "The languages to read, as the program names them, joined by + or commas, such as eng+nor.", HelpName = "languages" };
    private readonly Option<string?> _mode = new("--mode") { Description = "skip (the default) leaves pages with text alone; redo replaces an earlier invisible text layer; force reads every page.", HelpName = "skip|redo|force" };
    private readonly Option<string?> _pdfa = new("--pdfa") { Description = "The PDF/A level the output meets: 2b by default, or 1b, 2a, 2u, 3a, 3b, 3u, 4, 4f, or none to leave it as it is.", HelpName = "level" };
    private readonly Option<int> _pageJobs = new("--page-jobs") { Description = "How many pages of a file the program reads at once; 0 (the default) for one per processor.", HelpName = "count" };
    private readonly Option<double?> _dpi = new("--dpi") { Description = "The resolution pages are rendered at when they are not one scanned image; 300 by default.", HelpName = "dpi" };
    private readonly Option<double?> _confidence = new("--min-confidence") { Description = "Leave out words the program is less sure of, from 0 to 1.", HelpName = "fraction" };
    private readonly Option<bool> _deskew = new("--deskew") { Description = "Straighten the image the program reads; the page is not changed." };
    private readonly Option<bool> _rotate = new("--rotate-pages") { Description = "Turn pages the program finds sideways or upside down." };
    private readonly Option<int?> _timeout = new("--engine-timeout") { Description = "Seconds one run of the program may take; 300 by default.", HelpName = "seconds" };
    private readonly PageOption _pages = new("The pages to read");

    /// <inheritdoc/>
    public override string Name => "ocr";

    /// <inheritdoc/>
    public override string Description => "Make scanned pages searchable: run an OCR program (Tesseract or any that writes hOCR or ALTO) on each page and add its words as invisible text; PDF/A-2b by default.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_engine, _languages, _mode, _pdfa, _pageJobs, _dpi, _confidence, _deskew, _rotate, _timeout, _pages.Pages])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string engineCommand = result.GetValue(_engine) is { Length: > 0 } text
            ? text
            : throw new UsageException("--engine-command names the OCR program, such as \"tesseract {image} {output} -l {langs} --dpi {dpi} hocr\".");
        string[] languages = (result.GetValue(_languages) ?? string.Empty).Split(['+', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        PdfOcrMode mode = result.GetValue(_mode)?.ToLowerInvariant() switch
        {
            null or "skip" => PdfOcrMode.Skip,
            "redo" => PdfOcrMode.Redo,
            "force" => PdfOcrMode.Force,
            string other => throw new UsageException($"--mode '{other}' is not skip, redo or force."),
        };
        PdfAConformance? pdfa = result.GetValue(_pdfa)?.ToLowerInvariant() switch
        {
            null => PdfAConformance.PdfA2b,
            "none" => null,
            string level => ConvertCommand.Conformance(level),
        };
        int pageJobs = result.GetValue(_pageJobs) is int jobs and >= 0 ? jobs : throw new UsageException("--page-jobs must be 0 or more.");
        int timeout = result.GetValue(_timeout) ?? 300;
        if (timeout <= 0)
        {
            throw new UsageException("--engine-timeout must be more than 0 seconds.");
        }

        double dpi = result.GetValue(_dpi) ?? 300, confidence = result.GetValue(_confidence) ?? 0;
        if (dpi is < 72 or > 1200)
        {
            throw new UsageException("--dpi must be from 72 to 1200.");
        }

        if (confidence is < 0 or > 1)
        {
            throw new UsageException("--min-confidence must be from 0 to 1.");
        }

        PdfOcrOptions options = new()
        {
            Mode = mode,
            Languages = languages,
            MaxDegreeOfParallelism = pageJobs,
            Dpi = dpi,
            MinConfidence = confidence,
            Deskew = result.GetValue(_deskew),
            RotatePages = result.GetValue(_rotate),
            PdfA = pdfa,
        };
        CommandOcrEngine engine = new(engineCommand, new CommandOcrOptions { Languages = languages, Timeout = TimeSpan.FromSeconds(timeout) });
        PdfPageRange? range = _pages.Range(result);
        return (document, context) =>
        {
            PdfOcrOptions run = options with
            {
                Pages = range is null ? null : PageOption.Indices(document, range),
                AllowInvalidatingSignatures = context.AllowInvalidatingSignatures,
            };
            PdfOcrReport report = PdfOcr.MakeSearchableAsync(document, engine, run, session.Cancellation).GetAwaiter().GetResult();
            foreach (PdfOcrPageReport failed in report.Pages.Where(p => p.Status == PdfOcrPageStatus.Failed))
            {
                session.Info($"{context.File.Display}: page {failed.PageIndex + 1} failed: {failed.Message}");
            }

            int read = report.Pages.Count(p => p.Status == PdfOcrPageStatus.Recognized);
            if (read == 0 && report.Pages.FirstOrDefault(p => p.Status == PdfOcrPageStatus.Failed) is { } first)
            {
                throw new InvalidOperationException($"The OCR program failed on every page: {first.Message}");
            }

            if (report.PdfA is { Succeeded: false } failedPdfA)
            {
                List<PdfAIssue> unmet = [.. failedPdfA.Unmet];
                throw new InvalidOperationException($"It cannot meet {CheckCommands.Level(failedPdfA.Conformance)} (--pdfa none leaves the level alone): {string.Join("; ", unmet.Take(3).Select(i => $"{i.Clause} {i.Description}"))}{(unmet.Count > 3 ? $" and {unmet.Count - 3} more" : string.Empty)}.");
            }

            if (report.PdfA is not null)
            {
                context.Rewrite = true;
            }

            int skipped = report.Pages.Count(p => p.Status == PdfOcrPageStatus.Skipped);
            string level = report.PdfA is { } converted ? ", " + CheckCommands.Level(converted.Conformance) : string.Empty;
            session.Info($"{context.File.Display}: {report.Words} words on {read} pages{(skipped > 0 ? $", {skipped} skipped" : string.Empty)}{level}");
            return document;
        };
    }
}
