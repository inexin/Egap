using System.CommandLine;
using System.Globalization;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap ocr-layer</c> and the <c>ocr-layer</c> step: an invisible text layer from an outside OCR engine's hOCR or ALTO
/// files, one per page, so scans become searchable. Egap does no OCR itself.
/// </summary>
internal sealed class OcrLayerEdit : Edit
{
    private readonly Option<string?> _hocr = new("--hocr") { Description = "hOCR files, one per page, named by a template such as scan-{page}.hocr ({page} from 1).", HelpName = "template" };
    private readonly Option<string?> _alto = new("--alto") { Description = "ALTO XML files, one per page, named as --hocr.", HelpName = "template" };
    private readonly PageOption _pages = new("The pages to add text to");

    /// <inheritdoc/>
    public override string Name => "ocr-layer";

    /// <inheritdoc/>
    public override string Description => "Add an invisible, searchable text layer from hOCR or ALTO files (from Tesseract, ABBYY and the like), one per page.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        command.Options.Add(_hocr);
        command.Options.Add(_alto);
        command.Options.Add(_pages.Pages);
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string? hocr = result.GetValue(_hocr), alto = result.GetValue(_alto);
        if ((hocr is null) == (alto is null))
        {
            throw new UsageException("Give --hocr or --alto.");
        }

        string pattern = hocr ?? alto!;
        PdfPageRange? range = _pages.Range(result);
        return (document, context) =>
        {
            IReadOnlyList<int> pages = PageOption.Indices(document, range);
            if (pages.Count > 1 && !pattern.Contains("{page", StringComparison.Ordinal))
            {
                throw new UsageException($"'{pattern}' names one file, but {pages.Count} pages take a text layer; put {{page}} in it, such as scan-{{page}}.hocr.");
            }

            int words = 0, layered = 0;
            foreach (int index in pages)
            {
                string path = session.ResolvePath(Fill(pattern, index + 1));
                if (!File.Exists(path))
                {
                    session.Info($"{context.File.Display}: no {Path.GetFileName(path)}, page {index + 1} left as it is");
                    continue;
                }

                using FileStream stream = File.OpenRead(path);
                words += hocr is not null ? PdfOcrLayer.AddHocr(document.Pages[index], stream) : PdfOcrLayer.AddAlto(document.Pages[index], stream);
                layered++;
            }

            if (layered == 0)
            {
                throw new InvalidOperationException($"No {(hocr is not null ? "hOCR" : "ALTO")} file matches '{pattern}'.");
            }

            session.Info($"{context.File.Display}: {words} words on {layered} pages");
            return document;
        };
    }

    /// <summary>The pattern with <c>{page}</c>, or <c>{page:000}</c>, filled in.</summary>
    private static string Fill(string pattern, int page)
    {
        int start = pattern.IndexOf("{page", StringComparison.Ordinal);
        if (start < 0)
        {
            return pattern;
        }

        int end = pattern.IndexOf('}', start);
        string format = end > start + 5 && pattern[start + 5] == ':' ? pattern[(start + 6)..end] : string.Empty;
        return pattern[..start] + page.ToString(format, CultureInfo.InvariantCulture) + pattern[(end + 1)..];
    }
}

/// <summary>
/// <c>egap clean</c> and the <c>clean</c> step: tidy scans and prints: grayscale, blank pages removed, scanned pages
/// straightened, text turned into outlines.
/// </summary>
internal sealed class CleanEdit : Edit
{
    private readonly Option<bool> _gray = new("--grayscale") { Description = "Turn every color to gray." };
    private readonly Option<bool> _blank = new("--remove-blank-pages") { Description = "Remove pages with next to no ink." };
    private readonly Option<double?> _ink = new("--blank-ink") { Description = "The most ink, as a fraction of the page, that still counts as blank, such as 0.002.", HelpName = "fraction" };
    private readonly Option<bool> _deskew = new("--deskew") { Description = "Straighten scanned pages that are a little crooked." };
    private readonly Option<double?> _maxAngle = new("--max-angle") { Description = "The largest tilt --deskew corrects, in degrees.", HelpName = "degrees" };
    private readonly Option<bool> _outlines = new("--text-to-outlines") { Description = "Turn text into outlines, so it prints the same everywhere and needs no fonts (it can no longer be searched)." };

    /// <inheritdoc/>
    public override string Name => "clean";

    /// <inheritdoc/>
    public override string Description => "Tidy scans and prints: --grayscale, --remove-blank-pages, --deskew and --text-to-outlines, in that order.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_gray, _blank, _ink, _deskew, _maxAngle, _outlines])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        bool gray = result.GetValue(_gray), blank = result.GetValue(_blank), deskew = result.GetValue(_deskew), outlines = result.GetValue(_outlines);
        if (!gray && !blank && !deskew && !outlines)
        {
            throw new UsageException("Nothing to do: give --grayscale, --remove-blank-pages, --deskew or --text-to-outlines.");
        }

        BlankPageOptions blankOptions = result.GetValue(_ink) is double ink ? BlankPageOptions.Default with { MaxInkFraction = ink } : BlankPageOptions.Default;
        DeskewOptions deskewOptions = result.GetValue(_maxAngle) is double angle ? DeskewOptions.Default with { MaxAngle = angle } : DeskewOptions.Default;
        return (document, context) =>
        {
            if (gray)
            {
                session.Report(context.File.Display, document.ConvertToGray(session.Cancellation));
            }

            if (blank)
            {
                IReadOnlyList<int> removed = document.RemoveBlankPages(blankOptions, session.Cancellation);
                session.Info($"{context.File.Display}: removed {removed.Count} blank pages");
            }

            if (deskew)
            {
                IReadOnlyList<int> straightened = document.DeskewScannedPages(deskewOptions, session.Cancellation);
                session.Info($"{context.File.Display}: straightened {straightened.Count} pages");
            }

            if (outlines)
            {
                int glyphs = 0;
                foreach (PdfPage page in document.Pages)
                {
                    glyphs += page.ConvertTextToOutlines();
                }

                session.Info($"{context.File.Display}: {glyphs} glyphs turned into outlines");
            }

            return document;
        };
    }
}
