using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Egap.Pdf.Ocr;

/// <summary>Reads OCR XML safely: no DTDs, no external entities, a size limit.</summary>
internal static class OcrXml
{
    /// <summary>The most characters an OCR file is read from.</summary>
    private const int MaxCharacters = 64 << 20;

    public static XDocument Load(Stream stream)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            MaxCharactersFromEntities = 1 << 20,
        };
        try
        {
            using XmlReader reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new PdfException($"The OCR file is not well-formed XML: {e.Message}", e);
        }
    }
}

/// <summary>
/// hOCR (the hOCR 1.2 specification, as Tesseract, Kraken and OCRmyPDF write it): XHTML whose elements carry their class
/// (<c>ocr_page</c>, <c>ocr_line</c>, <c>ocrx_word</c> and the other line kinds) and a <c>title</c> with properties such as
/// <c>bbox x0 y0 x1 y1</c> in the page image's pixels, from its top left. The first page is read.
/// </summary>
internal static partial class HocrReader
{
    private static readonly HashSet<string> LineClasses = ["ocr_line", "ocrx_line", "ocr_header", "ocr_footer", "ocr_caption", "ocr_textfloat"];

    public static List<PdfOcrLine> Read(Stream stream, PdfRectangle pageBox)
    {
        XDocument document = OcrXml.Load(stream);
        XElement? page = document.Descendants().FirstOrDefault(e => HasClass(e, "ocr_page"));
        if (page is null || Box(page) is not (double, double, double, double) pixels || pixels.Item3 <= pixels.Item1 || pixels.Item4 <= pixels.Item2)
        {
            throw new PdfException("The hOCR file has no ocr_page with a bbox.");
        }

        Map map = new(pixels.Item1, pixels.Item2, pixels.Item3, pixels.Item4, pageBox);
        List<PdfOcrLine> lines = [];
        foreach (XElement line in page.Descendants().Where(e => Classes(e).Any(LineClasses.Contains)))
        {
            List<PdfOcrWord> words = [];
            foreach (XElement word in line.Descendants().Where(e => HasClass(e, "ocrx_word")))
            {
                string text = word.Value.Trim();
                if (text.Length > 0 && Box(word) is (double x0, double y0, double x1, double y1))
                {
                    words.Add(new PdfOcrWord(text, map.ToPage(x0, y0, x1, y1), Language(word)));
                }
            }

            if (words.Count > 0)
            {
                lines.Add(new PdfOcrLine(words));
            }
        }

        return lines;
    }

    private static string? Language(XElement element)
    {
        for (XElement? at = element; at is not null; at = at.Parent)
        {
            if (((string?)at.Attribute("lang") ?? (string?)at.Attribute(XNamespace.Xml + "lang")) is string language && language.Length > 0)
            {
                return language;
            }
        }

        return null;
    }

    private static string[] Classes(XElement element)
    {
        return ((string?)element.Attribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool HasClass(XElement element, string name)
    {
        return Classes(element).Contains(name);
    }

    /// <summary>The <c>bbox</c> property of an element's title.</summary>
    private static (double, double, double, double)? Box(XElement element)
    {
        Match match = BboxPattern().Match((string?)element.Attribute("title") ?? "");
        if (!match.Success)
        {
            return null;
        }

        double[] values = [.. Enumerable.Range(1, 4).Select(i => double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture))];
        return (values[0], values[1], values[2], values[3]);
    }

    [GeneratedRegex(@"(?:^|;)\s*bbox\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)\s+(-?\d+(?:\.\d+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex BboxPattern();
}

/// <summary>
/// ALTO (v2 to v4, the Library of Congress schema, as ABBYY and digitisation workflows write it): <c>Page</c> with its
/// <c>WIDTH</c> and <c>HEIGHT</c>, <c>TextLine</c> elements of <c>String</c> elements with <c>CONTENT</c>, <c>HPOS</c>,
/// <c>VPOS</c>, <c>WIDTH</c> and <c>HEIGHT</c> in the file's measurement unit, from the top left. The first page is read.
/// </summary>
internal static class AltoReader
{
    public static List<PdfOcrLine> Read(Stream stream, PdfRectangle pageBox)
    {
        XDocument document = OcrXml.Load(stream);
        XElement? page = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Page");
        if (page is null || Number(page, "WIDTH") is not double width || Number(page, "HEIGHT") is not double height || width <= 0 || height <= 0)
        {
            throw new PdfException("The ALTO file has no Page with a WIDTH and HEIGHT.");
        }

        Map map = new(0, 0, width, height, pageBox);
        List<PdfOcrLine> lines = [];
        foreach (XElement line in page.Descendants().Where(e => e.Name.LocalName == "TextLine"))
        {
            List<PdfOcrWord> words = [];
            foreach (XElement word in line.Elements().Where(e => e.Name.LocalName == "String"))
            {
                string text = ((string?)word.Attribute("CONTENT") ?? "").Trim();
                if (text.Length > 0 && Number(word, "HPOS") is double x && Number(word, "VPOS") is double y && Number(word, "WIDTH") is double w && Number(word, "HEIGHT") is double h)
                {
                    words.Add(new PdfOcrWord(text, map.ToPage(x, y, x + w, y + h), (string?)word.Attribute("LANG") ?? (string?)line.Attribute("LANG")));
                }
            }

            if (words.Count > 0)
            {
                lines.Add(new PdfOcrLine(words));
            }
        }

        return lines;
    }

    private static double? Number(XElement element, string name)
    {
        return double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    }
}

/// <summary>Maps an OCR file's page coordinates (y down from the top left) onto a PDF page box (y up).</summary>
internal sealed class Map(double left, double top, double right, double bottom, PdfRectangle page)
{
    public PdfRectangle ToPage(double x0, double y0, double x1, double y1)
    {
        double sx = page.Width / (right - left), sy = page.Height / (bottom - top);
        double l = page.Left + ((Math.Min(x0, x1) - left) * sx);
        double r = page.Left + ((Math.Max(x0, x1) - left) * sx);
        double t = page.Top - ((Math.Min(y0, y1) - top) * sy);
        double b = page.Top - ((Math.Max(y0, y1) - top) * sy);
        return new PdfRectangle(l, b, r, t);
    }
}
