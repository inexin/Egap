using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// Reads one page for layout analysis (Build Plan task 2) in a single walk of its content: glyphs as Phase 3's text extraction
/// reads them (Unicode from the font, ligatures and Arabic presentation forms as their letters, <c>/ActualText</c> spans as
/// one glyph) built into words and lines by the same rules, plus painted paths, images and shadings, each with its marked
/// content, then the page's visible annotations and the outline entries that point to it. Everything is turned into the
/// displayed page's space (<see cref="PageBox"/>). Glyphs whose box lies wholly outside the crop box are left out.
/// </summary>
internal sealed class PageReader : PdfContentWalker
{
    /// <summary>The ascent and descent of a font that gives none, in thousandths of the em (as Phase 3 assumes).</summary>
    private const double DefaultAscent = 800;
    private const double DefaultDescent = -200;

    private readonly PdfMatrix toDisplayed;
    private readonly PageBox bounds;
    private readonly Dictionary<PdfDictionary, PdfFont> fonts = new(ReferenceEqualityComparer.Instance);
    private readonly List<PageGlyph> glyphs = [];
    private readonly List<PagePath> paths = [];
    private readonly List<PageImage> images = [];
    private readonly List<PageShading> shadings = [];
    private readonly Stack<List<PageGlyph>?> spans = new();
    private readonly Stack<string> actualTexts = new();
    private Dictionary<object, int>? operations;
    private int operation = -1;
    private int unmapped;

    private PageReader(PdfMatrix toDisplayed, PageBox bounds)
    {
        this.toDisplayed = toDisplayed;
        this.bounds = bounds;
    }

    /// <summary>Reads page <paramref name="index"/> of <paramref name="document"/>; <paramref name="outline"/> from <see cref="ReadOutline"/>.</summary>
    public static PageContent Read(PdfDocument document, int index, IReadOnlyList<OutlineEntry>? outline = null, bool operations = false)
    {
        PdfPage page = document.Pages[index];
        (PdfMatrix toDisplayed, double width, double height, int turns) = Displayed(page);
        PageReader reader = new(toDisplayed, new PageBox(0, 0, width, height));
        reader.operations = operations ? new(ReferenceEqualityComparer.Instance) : null;
        reader.Walk(page);
        List<PdfWarning> warnings = [.. reader.Warnings];
        if (reader.unmapped > 0)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.TextWithoutUnicode,
                $"{reader.unmapped} glyphs have no Unicode text: their fonts have no ToUnicode map or glyph names that say; they were given private-use characters."));
        }

        return new PageContent
        {
            Index = index,
            Width = width,
            Height = height,
            QuarterTurns = turns,
            ToDisplayed = toDisplayed,
            Glyphs = reader.glyphs,
            Lines = Lines(reader.glyphs),
            Paths = reader.paths,
            Images = reader.images,
            Shadings = reader.shadings,
            Annotations = Annotations(document, page, toDisplayed),
            Outline = [.. (outline ?? []).Where(e => e.Page == index)],
            Warnings = warnings,
            Operations = reader.operations ?? (IReadOnlyDictionary<object, int>)new Dictionary<object, int>(),
        };
    }

    protected override void OnOperation(PdfOperation operation)
    {
        // The page's own operations are counted; those of the forms and patterns it draws belong to the operation drawing them.
        if (Depth == 0)
        {
            this.operation++;
        }
    }

    /// <summary>
    /// The matrix from a page's user space to its displayed space (points, y down from the top left of the crop box as a viewer
    /// shows it), with the displayed width and height and the page's rotation in quarter turns.
    /// </summary>
    public static (PdfMatrix ToDisplayed, double Width, double Height, int QuarterTurns) Displayed(PdfPage page)
    {
        PdfRectangle crop = page.CropBox.Normalize();
        double l = crop.Left, b = crop.Bottom, r = crop.Right, t = crop.Top;
        int turns = ((page.Rotate % 360) + 360) % 360 / 90;
        return turns switch
        {
            1 => (new PdfMatrix(0, 1, 1, 0, -b, -l), crop.Height, crop.Width, 1),
            2 => (new PdfMatrix(-1, 0, 0, 1, r, -b), crop.Width, crop.Height, 2),
            3 => (new PdfMatrix(0, -1, -1, 0, t, r), crop.Height, crop.Width, 3),
            _ => (new PdfMatrix(1, 0, 0, -1, -l, t), crop.Width, crop.Height, 0),
        };
    }

    /// <summary>The document's outline, depth first, each entry with its depth and its destination page and displayed top.</summary>
    public static List<OutlineEntry> ReadOutline(PdfDocument document)
    {
        List<OutlineEntry> entries = [];
        Visit(document.Outline.Children, 0);
        return entries;

        void Visit(IReadOnlyList<PdfOutlineItem> items, int level)
        {
            foreach (PdfOutlineItem item in items)
            {
                PdfDestination? destination = item.Destination ?? (item.Action as PdfGoToAction)?.ResolveDestination(document);
                (int? page, double? top) = Target(document, destination);
                entries.Add(new(item.Title, level, page, top));
                if (level < 64)
                {
                    Visit(item.Children, level + 1);
                }
            }
        }
    }

    protected override void OnGlyph(PdfGlyph glyph)
    {
        PdfFont? font = State.Font is PdfDictionary dictionary ? Font(dictionary) : null;
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

        // The glyph's advance by the font's ascent and descent, in text space units of one em, mapped to the displayed page.
        PdfMatrix m = glyph.Matrix * toDisplayed;
        double ascent = font is { Ascent: not 0 } f1 ? f1.Ascent : DefaultAscent;
        double descent = font is { Descent: not 0 } f2 ? f2.Descent : DefaultDescent;
        double advance = glyph.Width / 1000;
        PdfPoint origin = m.Transform(new PdfPoint(0, 0));
        PdfPoint end = m.Transform(new PdfPoint(advance, 0));
        double low = Math.Min(descent, 0) / 1000, high = Math.Max(ascent, 0) / 1000, right = Math.Max(advance, 0);
        PageBox box = PageBox.Around(m.Transform(new PdfPoint(0, low)), m.Transform(new PdfPoint(right, low)),
            m.Transform(new PdfPoint(0, high)), m.Transform(new PdfPoint(right, high)));
        double size = Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));
        (double dx, double dy) = Unit(m.A, m.B);
        double space = font is not null && !font.IsComposite && font.GetWidth(32) is > 0 and var w ? w : 250;
        (int mcid, string? artifact) = Marked();
        PageGlyph item = new(text, box, origin, end, dx, dy, font, size, State.FillColor, State.TextRenderMode, mcid, artifact, space / 1000 * size);
        operations?.Add(item, operation);
        // A glyph inside an /ActualText span belongs to it, however deep other marked content nests within the span.
        if (spans.Count > 0 && spans.FirstOrDefault(s => s is not null) is List<PageGlyph> span)
        {
            span.Add(item);
        }
        else if (Visible(box))
        {
            glyphs.Add(item);
        }
    }

    protected override bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure)
    {
        // A Type 3 glyph's procedure draws the glyph; its paths are not figures and its text operators are not the page's text.
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
        if (spans.Count == 0 || spans.Pop() is not List<PageGlyph> span)
        {
            return;
        }

        string text = actualTexts.Count > 0 ? actualTexts.Pop() : string.Empty;
        if (span.Count == 0)
        {
            return;
        }

        // The span becomes one glyph with its actual text, over the boxes of the glyphs it replaces.
        PageBox box = PageBox.Around(span.Select(g => g.Box));
        if (Visible(box))
        {
            PageGlyph whole = span[0] with { Text = text, Box = box, End = span[^1].End };
            operations?.TryAdd(whole, operations.GetValueOrDefault(span[0], operation));
            glyphs.Add(whole);
        }
    }

    protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
    {
        bool stroked = (paint & PdfPathPaint.Stroke) != 0, filled = (paint & (PdfPathPaint.Fill | PdfPathPaint.EvenOddFill)) != 0;
        if (!stroked && !filled || path.IsEmpty)
        {
            return;
        }

        PdfMatrix m = State.Matrix * toDisplayed;
        List<PdfPathSegment> segments = new(path.Segments.Count);
        PageBox box = PageBox.Empty;
        foreach (PdfPathSegment segment in path.Segments)
        {
            PdfPathSegment mapped = segment with
            {
                Point = m.Transform(segment.Point),
                Control1 = m.Transform(segment.Control1),
                Control2 = m.Transform(segment.Control2),
            };
            segments.Add(mapped);
            if (segment.Verb != PdfPathVerb.Close)
            {
                box = box.Union(PageBox.Around(mapped.Point));
            }

            if (segment.Verb == PdfPathVerb.CurveTo)
            {
                box = box.Union(PageBox.Around(mapped.Control1, mapped.Control2));
            }
        }

        double scale = Math.Sqrt(Math.Abs((m.A * m.D) - (m.B * m.C)));
        (int mcid, string? artifact) = Marked();
        PagePath item = new(segments, box, stroked, filled, State.LineWidth * scale, State.StrokeColor, State.FillColor, mcid, artifact);
        operations?.Add(item, operation);
        paths.Add(item);
    }

    protected override void OnImage(PdfStream? image, PdfInlineImage? inlineImage, PdfName? name)
    {
        PdfMatrix m = State.Matrix * toDisplayed;

        // Image space has (0, 0) at the stored image's bottom left and (0, 1) at its top left.
        PdfPoint[] corners = [m.Transform(new PdfPoint(0, 1)), m.Transform(new PdfPoint(1, 1)), m.Transform(new PdfPoint(1, 0)), m.Transform(new PdfPoint(0, 0))];
        (int mcid, string? artifact) = Marked();
        PageImage item = new(corners, PageBox.Around(corners), image, inlineImage, name?.Value, m, mcid, artifact);
        operations?.Add(item, operation);
        images.Add(item);
    }

    protected override void OnShading(PdfObject? shading, PdfName name)
    {
        PageBox clip = bounds;
        foreach (PdfClipPath c in State.Clips)
        {
            if (c.Path.GetBounds(c.Matrix * toDisplayed) is PdfRectangle r)
            {
                clip = clip.Intersect(new PageBox(r.Left, r.Bottom, r.Right, r.Top));
            }
        }

        (int mcid, string? artifact) = Marked();
        shadings.Add(new(clip, mcid, artifact));
    }

    /// <summary>
    /// The innermost MCID around the current content (-1 for none) and, inside an artifact, its <c>/Subtype</c> or <c>/Type</c>
    /// (empty when it gives neither); null outside artifacts.
    /// </summary>
    private (int Mcid, string? Artifact) Marked()
    {
        int mcid = -1;
        string? artifact = null;
        foreach (PdfMarkedContent marked in MarkedContent)
        {
            mcid = marked.Mcid ?? mcid;
            if (marked.Tag.Value == "Artifact")
            {
                artifact = marked.Properties?.GetName("Subtype")?.Value ?? marked.Properties?.GetName("Type")?.Value ?? artifact ?? "";
            }
        }

        return (mcid, artifact);
    }

    private bool Visible(PageBox box)
    {
        return box.Overlap(bounds) > 0 || (box.Area == 0 && bounds.Contains(box.CenterX, box.CenterY));
    }

    private PdfFont Font(PdfDictionary dictionary)
    {
        if (!fonts.TryGetValue(dictionary, out PdfFont? font))
        {
            font = PdfFont.FromDictionary(dictionary);
            fonts[dictionary] = font;
        }

        return font;
    }

    /// <summary>
    /// Ligatures and Arabic presentation forms as the letters they join (NFKC on those blocks only), as Phase 3 gives them.
    /// </summary>
    private static string Compatible(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '\uFB00' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF')
            {
                return string.Concat(text.Select(ch => ch is >= '\uFB00' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF'
                    ? ch.ToString().Normalize(System.Text.NormalizationForm.FormKC) : ch.ToString()));
            }
        }

        return text;
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        return length > 0 ? (x / length, y / length) : (1, 0);
    }

    /// <summary>Lines in content order: a new line where the baseline moves, the direction turns or the text runs back (Phase 3 Q10).</summary>
    internal static List<PageLine> Lines(IEnumerable<PageGlyph> glyphs)
    {
        List<PageLine> lines = [];
        List<PageGlyph> line = [];
        foreach (PageGlyph glyph in glyphs)
        {
            if (line.Count > 0 && !Continues(line[^1], glyph))
            {
                AddLine(lines, line);
                line = [];
            }

            line.Add(glyph);
        }

        AddLine(lines, line);
        return lines;
    }

    /// <summary>Whether a glyph carries on the line the previous glyph is on.</summary>
    private static bool Continues(PageGlyph previous, PageGlyph next)
    {
        if ((previous.Dx * next.Dx) + (previous.Dy * next.Dy) < 0.99)
        {
            return false;
        }

        // Offsets from the previous glyph's end, across and along the direction of writing.
        double ox = next.Origin.X - previous.End.X, oy = next.Origin.Y - previous.End.Y;
        double across = Math.Abs((ox * -previous.Dy) + (oy * previous.Dx));
        double along = (ox * previous.Dx) + (oy * previous.Dy);
        double size = Math.Max(Math.Max(previous.Size, next.Size), 0.001);
        return across <= size * 0.5 && along >= -size * 1.5;
    }

    /// <summary>Splits a line's glyphs into words at spaces and gaps wider than the font's space, and adds the line when it has any.</summary>
    private static void AddLine(List<PageLine> lines, List<PageGlyph> line)
    {
        List<PageWord> words = [];
        List<PageGlyph> word = [];
        PageGlyph? previous = null;
        foreach (PageGlyph glyph in Logical(line))
        {
            if (glyph.IsSpace || glyph.Text.Length == 0)
            {
                if (word.Count > 0)
                {
                    words.Add(new PageWord(word));
                    word = [];
                }

                previous = glyph.IsSpace ? null : previous;
                continue;
            }

            if (previous is not null && word.Count > 0)
            {
                double gap = ((glyph.Origin.X - previous.End.X) * previous.Dx) + ((glyph.Origin.Y - previous.End.Y) * previous.Dy);
                if (gap > PdfTextOptions.Default.WordGap * Math.Max(previous.SpaceWidth, previous.Size * 0.1))
                {
                    words.Add(new PageWord(word));
                    word = [];
                }
            }

            word.Add(glyph);
            previous = glyph;
        }

        if (word.Count > 0)
        {
            words.Add(new PageWord(word));
        }

        if (words.Count > 0)
        {
            lines.Add(new PageLine(words));
        }
    }

    /// <summary>
    /// A line written right to left in the logical order text is read in, as Phase 3 gives it: the runs turned around, and the
    /// glyphs within each right-to-left run; runs of Latin letters and digits keep their own order.
    /// </summary>
    private static List<PageGlyph> Logical(List<PageGlyph> line)
    {
        int rtl = 0, ltr = 0;
        foreach (PageGlyph glyph in line)
        {
            foreach (char c in glyph.Text)
            {
                rtl += IsRtl(c) ? 1 : 0;
                ltr += char.IsLetter(c) && !IsRtl(c) ? 1 : 0;
            }
        }

        if (rtl == 0 || rtl < ltr)
        {
            return line;
        }

        List<List<PageGlyph>> runs = [];
        bool? current = null;
        foreach (PageGlyph glyph in line)
        {
            bool strongLtr = glyph.Text.Any(c => char.IsAsciiLetterOrDigit(c) || (char.IsLetterOrDigit(c) && !IsRtl(c) && c < 0x0590));
            if (current != strongLtr || runs.Count == 0)
            {
                runs.Add([]);
                current = strongLtr;
            }

            runs[^1].Add(glyph);
        }

        List<PageGlyph> logical = [];
        for (int r = runs.Count - 1; r >= 0; r--)
        {
            bool isLtr = runs[r].Any(g => g.Text.Any(char.IsAsciiLetterOrDigit));
            logical.AddRange(isLtr ? runs[r] : Enumerable.Reverse(runs[r]));
        }

        return logical;
    }

    private static bool IsRtl(char c)
    {
        return c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF';
    }

    /// <summary>The page's visible annotations, popups left out, with link targets resolved.</summary>
    private static List<PageAnnotation> Annotations(PdfDocument document, PdfPage page, PdfMatrix toDisplayed)
    {
        List<PageAnnotation> found = [];
        foreach (PdfAnnotation annotation in page.Annotations)
        {
            if ((annotation.Flags & (PdfAnnotationFlags.Hidden | PdfAnnotationFlags.NoView)) != 0 || annotation.Subtype == "Popup")
            {
                continue;
            }

            PdfRectangle r = annotation.Rectangle.Normalize();
            PageBox box = PageBox.Around(toDisplayed.Transform(new PdfPoint(r.Left, r.Bottom)), toDisplayed.Transform(new PdfPoint(r.Right, r.Top)));
            string? uri = null;
            int? target = null;
            double? top = null;
            if (annotation is PdfLinkAnnotation link)
            {
                uri = (link.Action as PdfUriAction)?.Uri;
                PdfDestination? destination = link.Destination ?? (link.Action as PdfGoToAction)?.ResolveDestination(document);
                (target, top) = Target(document, destination);
            }

            string? author = (annotation as PdfMarkupAnnotation)?.Author;
            List<PageBox> quads = [];
            if (annotation is PdfTextMarkupAnnotation markup && markup.QuadPoints is { Count: >= 4 } points)
            {
                for (int q = 0; q + 3 < points.Count; q += 4)
                {
                    PageBox quad = PageBox.Empty;
                    for (int c = q; c < q + 4; c++)
                    {
                        PdfPoint p = toDisplayed.Transform(points[c]);
                        quad = quad.Union(PageBox.Around(p, p));
                    }

                    quads.Add(quad);
                }
            }

            found.Add(new(annotation, box, annotation.Subtype, annotation.Contents, author, uri, target, top) { Quads = quads });
        }

        return found;
    }

    /// <summary>A destination's page and its top on the displayed page, when it says.</summary>
    private static (int? Page, double? Top) Target(PdfDocument document, PdfDestination? destination)
    {
        if (destination is null)
        {
            return (null, null);
        }

        int index = destination.GetPageIndex(document);
        if (index < 0 || index >= document.Pages.Count)
        {
            return (null, null);
        }

        double? top = null;
        if (destination.Top is double y)
        {
            PdfMatrix m = Displayed(document.Pages[index]).ToDisplayed;
            top = m.Transform(new PdfPoint(destination.Left ?? 0, y)).Y;
        }

        return (index, top);
    }
}
