namespace Egap.Pdf.Text;

/// <summary>
/// Collects a page's glyphs for text extraction (Phase 3 design Q10) and builds them into words and lines: content order by
/// default, a line breaking where the baseline or direction changes or the text runs back, a word breaking at a space glyph
/// or a gap wider than a fraction of the font's space. <c>/ActualText</c> spans become one glyph each.
/// </summary>
internal sealed class TextCollector : PdfContentWalker
{
    /// <summary>The ascent and descent of a font that gives none, in thousandths of the em.</summary>
    private const double DefaultAscent = 800;
    private const double DefaultDescent = -200;

    private readonly List<Item> items = [];
    private readonly Stack<List<Item>?> spans = new();
    private readonly Stack<string> actualTexts = new();
    private int unmapped;
    private (PdfName? Name, PdfDictionary? Resources, PdfObject? Value) fontResource;

    /// <summary>What replacing text needs of each glyph beyond what extraction gives; kept only when asked for.</summary>
    private Dictionary<PdfTextGlyph, GlyphDetails>? details;

    /// <summary>Walks a page and builds its text.</summary>
    public static PdfTextPage Read(PdfPage page, PdfTextOptions options)
    {
        return Read(page, options, null);
    }

    /// <summary>Walks a page and builds its text, with each glyph's matrix, rendering mode and font resource on the side.</summary>
    public static PdfTextPage ReadWithDetails(PdfPage page, PdfTextOptions options, out Dictionary<PdfTextGlyph, GlyphDetails> details)
    {
        details = new(ReferenceEqualityComparer.Instance);
        return Read(page, options, details);
    }

    private static PdfTextPage Read(PdfPage page, PdfTextOptions options, Dictionary<PdfTextGlyph, GlyphDetails>? details)
    {
        TextCollector collector = new() { details = details };
        collector.Walk(page);
        List<PdfWarning> warnings = [];
        if (collector.unmapped > 0)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.TextWithoutUnicode, $"{collector.unmapped} glyphs have no Unicode text: their fonts have no ToUnicode map or glyph names that say; they were given private-use characters."));
        }

        List<PdfTextLine> lines = options.Order == PdfTextOrder.Position ? ByPosition(collector.items, options) : ByContent(collector.items, options);
        return new PdfTextPage([.. collector.items.Select(i => i.Glyph)], lines, warnings);
    }

    /// <summary>The current font as the resources hold it, looked up again only when the font or the resources change.</summary>
    private PdfObject? FontResource()
    {
        if (!ReferenceEquals(fontResource.Name, State.FontName) || !ReferenceEquals(fontResource.Resources, Resources))
        {
            fontResource = (State.FontName, Resources, State.FontName is { } name ? Resources?.Get<PdfDictionary>("Font")?[name] : null);
        }

        return fontResource.Value;
    }

    protected override void OnGlyph(PdfGlyph glyph)
    {
        PdfFont? font = State.Font is PdfDictionary dictionary ? PdfFont.FromDictionary(dictionary) : null;
        string? text = font?.ToUnicode(glyph.Code);
        if (text is null)
        {
            unmapped++;
            text = ((char)(0xE000 + (glyph.Code % 0x1900))).ToString();
        }
        else
        {
            text = Compatible(text);
        }

        // The glyph's advance by the font's ascent and descent, in text space units of one em, mapped to the page.
        PdfMatrix m = glyph.Matrix;
        double ascent = font is { Ascent: not 0 } f1 ? f1.Ascent : DefaultAscent;
        double descent = font is { Descent: not 0 } f2 ? f2.Descent : DefaultDescent;
        double advance = glyph.Width / 1000;
        PdfPoint origin = m.Transform(new PdfPoint(0, 0));
        PdfPoint end = m.Transform(new PdfPoint(advance, 0));
        PdfRectangle box = m.Transform(new PdfRectangle(0, Math.Min(descent, 0) / 1000, Math.Max(advance, 0), Math.Max(ascent, 0) / 1000));
        double size = Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));
        (double dx, double dy) = Unit(m.A, m.B);
        double space = font is not null && !font.IsComposite && font.GetWidth(32) is > 0 and var w ? w : 250;
        Item item = new(new PdfTextGlyph(text, box, font, size, State.FillColor, glyph.Code) { Origin = origin, End = end }, dx, dy, size, space / 1000 * size);
        details?.Add(item.Glyph, new GlyphDetails(m, State.TextRenderMode, FontResource()));
        if (spans.Count > 0 && spans.Peek() is List<Item> span)
        {
            span.Add(item);
        }
        else
        {
            items.Add(item);
        }
    }

    protected override bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure)
    {
        // A Type 3 glyph's procedure draws the glyph; its own text operators, if any, are not the page's text.
        return false;
    }

    protected override void OnBeginMarkedContent(PdfMarkedContent markedContent)
    {
        // A span with ActualText inside another counts as part of the outer one.
        bool outer = spans.Count == 0 || spans.All(s => s is null);
        spans.Push(markedContent.ActualText is not null && outer ? [] : null);
        if (markedContent.ActualText is string actual && outer)
        {
            actualTexts.Push(actual);
        }
    }

    protected override void OnEndMarkedContent(PdfMarkedContent markedContent)
    {
        if (spans.Count == 0)
        {
            return;
        }

        List<Item>? span = spans.Pop();
        if (span is null)
        {
            return;
        }

        string text = actualTexts.Count > 0 ? actualTexts.Pop() : string.Empty;
        if (span.Count == 0)
        {
            return;
        }

        // The span becomes one glyph with its actual text, over the boxes of the glyphs it replaces.
        Item first = span[0], last = span[^1];
        PdfRectangle box = PdfTextWord.Union(span.Select(i => i.Glyph.Box));
        PdfTextGlyph glyph = first.Glyph with { Text = text, Box = box, Code = -1, End = last.Glyph.End };
        items.Add(first with { Glyph = glyph });
    }

    /// <summary>
    /// Ligatures and Arabic presentation forms as the letters they join (NFKC on those blocks only), as pdf.js and PDFium
    /// give them: a search for "office" finds it whether the producer used the ffi ligature or not.
    /// </summary>
    private static string Compatible(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '\uFB00' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF')
            {
                return string.Concat(text.Select(ch => ch is >= '\uFB00' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF' ? ch.ToString().Normalize(System.Text.NormalizationForm.FormKC) : ch.ToString()));
            }
        }

        return text;
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        return length > 0 ? (x / length, y / length) : (1, 0);
    }

    /// <summary>Lines in content order: a new line where the baseline moves, the direction turns or the text runs back.</summary>
    private static List<PdfTextLine> ByContent(List<Item> items, PdfTextOptions options)
    {
        List<PdfTextLine> lines = [];
        List<Item> line = [];
        foreach (Item item in items)
        {
            if (line.Count > 0 && !Continues(line[^1], item))
            {
                AddLine(lines, line, options);
                line = [];
            }

            line.Add(item);
        }

        AddLine(lines, line, options);
        return lines;
    }

    /// <summary>Lines by position: glyphs grouped by baseline top to bottom, each group left to right.</summary>
    private static List<PdfTextLine> ByPosition(List<Item> items, PdfTextOptions options)
    {
        List<Item> sorted = [.. items.Where(i => !string.IsNullOrEmpty(i.Glyph.Text)).OrderByDescending(i => i.Glyph.Origin.Y).ThenBy(i => i.Glyph.Origin.X)];
        List<PdfTextLine> lines = [];
        List<Item> line = [];
        double baseline = double.NaN, tolerance = 0;
        foreach (Item item in sorted)
        {
            if (line.Count > 0 && Math.Abs(item.Glyph.Origin.Y - baseline) > tolerance)
            {
                AddLine(lines, [.. line.OrderBy(i => i.Glyph.Origin.X)], options);
                line = [];
            }

            if (line.Count == 0)
            {
                baseline = item.Glyph.Origin.Y;
                tolerance = Math.Max(item.Size * 0.4, 0.5);
            }

            line.Add(item);
        }

        AddLine(lines, [.. line.OrderBy(i => i.Glyph.Origin.X)], options);
        return lines;
    }

    /// <summary>Whether a glyph carries on the line the previous glyph is on.</summary>
    private static bool Continues(Item previous, Item next)
    {
        if ((previous.Dx * next.Dx) + (previous.Dy * next.Dy) < 0.99)
        {
            return false;
        }

        // Offsets from the previous glyph's end, across and along the direction of writing.
        double ox = next.Glyph.Origin.X - previous.Glyph.End.X, oy = next.Glyph.Origin.Y - previous.Glyph.End.Y;
        double across = Math.Abs((ox * -previous.Dy) + (oy * previous.Dx));
        double along = (ox * previous.Dx) + (oy * previous.Dy);
        double size = Math.Max(Math.Max(previous.Size, next.Size), 0.001);
        return across <= size * 0.5 && along >= -size * 1.5;
    }

    /// <summary>Splits a line's glyphs into words at spaces and wide gaps and adds the line when it has any.</summary>
    private static void AddLine(List<PdfTextLine> lines, List<Item> line, PdfTextOptions options)
    {
        line = Logical(line);
        List<PdfTextWord> words = [];
        List<PdfTextGlyph> word = [];
        Item? previous = null;
        foreach (Item item in line)
        {
            if (item.Glyph.IsSpace || string.IsNullOrEmpty(item.Glyph.Text))
            {
                if (word.Count > 0)
                {
                    words.Add(new PdfTextWord(word));
                    word = [];
                }

                previous = item.Glyph.IsSpace ? null : previous;
                continue;
            }

            if (previous is Item p && word.Count > 0)
            {
                double gap = ((item.Glyph.Origin.X - p.Glyph.End.X) * p.Dx) + ((item.Glyph.Origin.Y - p.Glyph.End.Y) * p.Dy);
                if (gap > options.WordGap * Math.Max(p.SpaceWidth, p.Size * 0.1))
                {
                    words.Add(new PdfTextWord(word));
                    word = [];
                }
            }

            word.Add(item.Glyph);
            previous = item;
        }

        if (word.Count > 0)
        {
            words.Add(new PdfTextWord(word));
        }

        if (words.Count > 0)
        {
            lines.Add(new PdfTextLine(words));
        }
    }

    /// <summary>
    /// A line written right to left (more Hebrew or Arabic letters than others) in the logical order text is read in, as
    /// pdf.js and PDFium give it: content shows such lines left to right, so the runs are turned around, and the glyphs within
    /// each right-to-left run; runs of Latin letters and digits keep their own order.
    /// </summary>
    private static List<Item> Logical(List<Item> line)
    {
        int rtl = 0, ltr = 0;
        foreach (Item item in line)
        {
            foreach (char c in item.Glyph.Text)
            {
                rtl += IsRtl(c) ? 1 : 0;
                ltr += char.IsLetter(c) && !IsRtl(c) ? 1 : 0;
            }
        }

        if (rtl == 0 || rtl < ltr)
        {
            return line;
        }

        // Runs: right-to-left glyphs and neutrals together, left-to-right letters and digits apart.
        List<List<Item>> runs = [];
        bool? current = null;
        foreach (Item item in line)
        {
            bool strongLtr = item.Glyph.Text.Any(c => char.IsAsciiLetterOrDigit(c) || (char.IsLetterOrDigit(c) && !IsRtl(c) && c < 0x0590));
            if (current != strongLtr || runs.Count == 0)
            {
                runs.Add([]);
                current = strongLtr;
            }

            runs[^1].Add(item);
        }

        List<Item> logical = [];
        for (int r = runs.Count - 1; r >= 0; r--)
        {
            bool isLtr = runs[r].Any(i => i.Glyph.Text.Any(char.IsAsciiLetterOrDigit));
            logical.AddRange(isLtr ? runs[r] : Enumerable.Reverse(runs[r]));
        }

        return logical;
    }

    private static bool IsRtl(char c)
    {
        return c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';
    }

    /// <summary>A glyph with what layout needs: its direction of writing, size and space width in user space.</summary>
    private sealed record Item(PdfTextGlyph Glyph, double Dx, double Dy, double Size, double SpaceWidth);
}

/// <summary>A glyph's text rendering matrix (text space, font size included, to default user space), its rendering mode (<c>Tr</c>) and its font as the resources hold it.</summary>
internal readonly record struct GlyphDetails(PdfMatrix Matrix, int RenderMode, PdfObject? FontResource);
