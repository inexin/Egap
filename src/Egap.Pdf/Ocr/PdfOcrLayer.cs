using System.Globalization;
using System.Text;

using Egami.Fonts;

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
        PdfDocument document = page.Document;
        PdfReference font = Ocr.GlyphlessFont.For(document);
        PdfDictionary resources = page.Resources ?? [];
        PdfDictionary fonts = resources.Get<PdfDictionary>("Font") ?? [];
        string name = "GlyphLess";
        for (int i = 1; fonts.Get(name) is PdfObject existing && !(existing is PdfReference r && r.ObjectNumber == font.ObjectNumber); i++)
        {
            name = "GlyphLess" + i.ToString(CultureInfo.InvariantCulture);
        }

        fonts[name] = font;
        resources["Font"] = fonts;
        page.Dictionary["Resources"] = resources;

        StringBuilder text = new();
        int count = 0;
        foreach (PdfOcrLine line in lines)
        {
            List<PdfOcrWord> words = [.. line.Words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Box.Width > 0 && w.Box.Height > 0)];
            if (words.Count == 0)
            {
                continue;
            }

            text.Append("BT 3 Tr\n");
            for (int i = 0; i < words.Count; i++)
            {
                PdfOcrWord word = words[i];
                string shown = i + 1 < words.Count ? word.Text.Trim() + " " : word.Text.Trim();
                int letters = word.Text.Trim().Length;
                double size = word.Box.Height;
                double natural = letters * Advance / 1000.0 * size;
                double scale = natural > 0 ? word.Box.Width / natural * 100 : 100;
                text.Append(CultureInfo.InvariantCulture, $"/{name} {Number(size)} Tf {Number(scale)} Tz 1 0 0 1 {Number(word.Box.Left)} {Number(word.Box.Bottom)} Tm <");
                foreach (char c in shown)
                {
                    text.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                }

                text.Append("> Tj\n");
                count++;
            }

            text.Append("ET\n");
        }

        if (count > 0)
        {
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
}
