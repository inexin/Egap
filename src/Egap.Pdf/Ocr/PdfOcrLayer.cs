using System.Globalization;
using System.Numerics;
using System.Text;

using Egami.Ocr;

namespace Egap.Pdf;

/// <summary>A recognised word: its text and its box on the page, in PDF user space (Phase 5 design Q16).</summary>
/// <param name="Text">The word's text.</param>
/// <param name="Box">Where the word is on the page, in points from the bottom left.</param>
/// <param name="Language">The word's language when the OCR output gives one, such as <c>eng</c> or <c>en</c>.</param>
public sealed record PdfOcrWord(string Text, PdfRectangle Box, string? Language = null);

/// <summary>A recognised line of words, in reading order.</summary>
/// <param name="Words">The words.</param>
public sealed record PdfOcrLine(IReadOnlyList<PdfOcrWord> Words);

/// <summary>
/// An invisible text layer over a scanned page (Phase 5 design Q16): the words OCR found, drawn in render mode 3 (neither
/// filled nor stroked) in a glyphless font Egap builds, each word scaled to its box, so the page can be searched, selected
/// and read aloud while it looks exactly as the scan. Words come from hOCR (Tesseract, Kraken, OCRmyPDF) or ALTO (ABBYY,
/// libraries' digitisation) files, or from any OCR engine as word boxes. The page image is not touched.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("scan.pdf");
/// using (FileStream hocr = File.OpenRead("page1.hocr"))
/// {
///     PdfOcrLayer.AddHocr(pdf.Pages[0], hocr);
/// }
///
/// PdfAConverter.Convert(pdf, PdfAConformance.PdfA2b);
/// pdf.Save("searchable.pdf");
/// </code>
/// </example>
public static class PdfOcrLayer
{
    /// <summary>The glyphless font's advance for every code, in thousandths of the font size.</summary>
    private const int Advance = 500;

    /// <summary>Adds the words of an hOCR file's first page (its <c>ocr_page</c> box mapped onto the page's crop box).</summary>
    public static int AddHocr(PdfPage page, Stream hocr)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(hocr);
        return AddLines(page, Ocr.HocrReader.Read(hocr, page.CropBox));
    }

    /// <summary>Adds the words of an ALTO file's first page (its page size mapped onto the page's crop box).</summary>
    public static int AddAlto(PdfPage page, Stream alto)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(alto);
        return AddLines(page, Ocr.AltoReader.Read(alto, page.CropBox));
    }

    /// <summary>Reads the lines of an hOCR file's first page, mapped onto <paramref name="pageBox"/>.</summary>
    public static IReadOnlyList<PdfOcrLine> ReadHocr(Stream hocr, PdfRectangle pageBox)
    {
        ArgumentNullException.ThrowIfNull(hocr);
        return Ocr.HocrReader.Read(hocr, pageBox);
    }

    /// <summary>Reads the lines of an ALTO file's first page, mapped onto <paramref name="pageBox"/>.</summary>
    public static IReadOnlyList<PdfOcrLine> ReadAlto(Stream alto, PdfRectangle pageBox)
    {
        ArgumentNullException.ThrowIfNull(alto);
        return Ocr.AltoReader.Read(alto, pageBox);
    }

    /// <summary>
    /// Adds lines of words as invisible text over the page's content: each line one text object, each word placed at its
    /// box's bottom left, sized to the box's height and scaled across its width. Returns how many words were added.
    /// </summary>
    public static int AddLines(PdfPage page, IEnumerable<PdfOcrLine> lines)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(lines);
        return Write(page, lines.Select(line => line.Words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Box.Width > 0 && w.Box.Height > 0)
            .Select(w => new PlacedWord(w.Text.Trim(), new Vector2((float)w.Box.Left, (float)w.Box.Bottom), new Vector2((float)w.Box.Width, 0), new Vector2(0, (float)w.Box.Height), w.Language))
            .ToList()));
    }

    /// <summary>
    /// Adds an OCR engine's words as invisible text over the page's content. Each word's quad is mapped from the image's
    /// pixels to the page's user space by <paramref name="imageToPage"/>; a rotated or skewed word gets a text matrix along
    /// its own baseline and is scaled across its width, so selection follows the scan. Words less sure than
    /// <paramref name="minConfidence"/> are left out; a word's language goes on a <c>/Span</c> with <c>/Lang</c> when the
    /// document is tagged. Returns how many words were added.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="result">The words, in pixels of the image the engine read.</param>
    /// <param name="imageToPage">The image's pixels (origin at the top left) to the page's user space.</param>
    /// <param name="minConfidence">The lowest confidence kept, from 0 (all words, the default) to 1; words with no confidence are kept.</param>
    public static int AddResult(PdfPage page, OcrResult result, PdfMatrix imageToPage, double minConfidence = 0)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(result);
        Matrix3x2 matrix = new((float)imageToPage.A, (float)imageToPage.B, (float)imageToPage.C, (float)imageToPage.D, (float)imageToPage.E, (float)imageToPage.F);
        bool tagged = page.Document.Structure.Exists;
        List<List<PlacedWord>> lines = [];
        foreach (OcrLine line in result.Lines)
        {
            List<PlacedWord> words = [];
            foreach (OcrWord word in line.Words)
            {
                if (string.IsNullOrWhiteSpace(word.Text) || word.Confidence < minConfidence)
                {
                    continue;
                }

                OcrQuad quad = word.Box.Transform(matrix);
                Vector2 along = quad.BottomRight - quad.BottomLeft, up = quad.TopLeft - quad.BottomLeft;
                if (along.Length() > 0 && up.Length() > 0 && float.IsFinite(along.X + along.Y + up.X + up.Y + quad.BottomLeft.X + quad.BottomLeft.Y))
                {
                    words.Add(new PlacedWord(word.Text.Trim(), quad.BottomLeft, along, up, tagged && IsLanguageTag(word.Language) ? word.Language : null));
                }
            }

            lines.Add(words);
        }

        return Write(page, lines);
    }

    /// <summary>Writes the words, one text object per line, after the page's content. Returns how many were written.</summary>
    private static int Write(PdfPage page, IEnumerable<IReadOnlyList<PlacedWord>> lines)
    {
        PdfDocument document = page.Document;
        PdfReference font = Ocr.GlyphlessFont.For(document);
        PdfDictionary resources = page.Resources ?? [];
        PdfDictionary fonts = resources.Get<PdfDictionary>("Font") ?? [];
        string name = "GlyphLess";
        for (int i = 1; fonts.Get(name) is PdfObject existing && !(existing is PdfReference r && r.ObjectNumber == font.ObjectNumber); i++)
        {
            name = "GlyphLess" + i.ToString(CultureInfo.InvariantCulture);
        }

        StringBuilder text = new();
        int count = 0;
        foreach (IReadOnlyList<PlacedWord> words in lines)
        {
            if (words.Count == 0)
            {
                continue;
            }

            text.Append("BT 3 Tr\n");
            for (int i = 0; i < words.Count; i++)
            {
                PlacedWord word = words[i];
                string shown = i + 1 < words.Count ? word.Text + " " : word.Text;
                float width = word.Along.Length(), size = word.Up.Length();
                double natural = word.Text.Length * Advance / 1000.0 * size;
                double scale = natural > 0 ? width / natural * 100 : 100;
                Vector2 x = word.Along / width, y = word.Up / size;
                if (word.Language is { Length: > 0 } language)
                {
                    text.Append("/Span <</Lang (").Append(language).Append(")>> BDC ");
                }

                text.Append(CultureInfo.InvariantCulture, $"/{name} {Number(size)} Tf {Number(scale)} Tz {Number(x.X)} {Number(x.Y)} {Number(y.X)} {Number(y.Y)} {Number(word.Origin.X)} {Number(word.Origin.Y)} Tm <");
                foreach (char c in shown)
                {
                    text.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                }

                text.Append("> Tj");
                text.Append(word.Language is { Length: > 0 } ? " EMC\n" : "\n");
                count++;
            }

            text.Append("ET\n");
        }

        if (count > 0)
        {
            fonts[name] = font;
            resources["Font"] = fonts;
            page.Dictionary["Resources"] = resources;

            // The layer goes after the page's content, wrapped so its state does not leak into anything drawn later.
            PdfContent content = page.GetContent();
            PdfContent layer = PdfContent.Parse(Encoding.ASCII.GetBytes("q\n" + text + "Q\n"));
            content.Insert(0, new PdfOperation("q"));
            content.Add(new PdfOperation("Q"));
            foreach (PdfOperation operation in layer)
            {
                content.Add(operation);
            }

            page.SetContent(content);
        }

        return count;
    }

    private static string Number(double value)
    {
        return Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a language name is a plain tag (letters, digits and hyphens) that can go in a <c>/Lang</c> string as it is.</summary>
    private static bool IsLanguageTag(string? language)
    {
        return language is { Length: > 0 and <= 35 } && language.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    /// <summary>A word in user space: its baseline start, the vectors along its baseline and up its height, and its language.</summary>
    private readonly record struct PlacedWord(string Text, Vector2 Origin, Vector2 Along, Vector2 Up, string? Language);
}
