using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

using Egap.Documents.Charts;
using Egap.Documents.Docx;
using Egap.Documents.DrawingML;

using EgamiBorderStyle = Egap.Documents.BorderStyle;

namespace Egap.Documents.Layout;

/// <summary>
/// Draws laid-out pages onto Egami canvases (Phase 6 Build Plan task 21): cell and paragraph shading, text with its
/// backgrounds, underlines and strikes, pictures, shapes and text boxes, borders, with behind-text floats first and
/// in-front floats last.
/// </summary>
internal static class PageDrawing
{
    private static readonly Rgba8 Black = new(0, 0, 0, 255);

    // Each document's images, decoded once: a PDF canvas writes one image object per EncodedImage instance, so a logo in a
    // running header is embedded once, not once per page. ImageData compares by its bytes' memory, so pictures that share
    // a file (a DOCX media part used twice) share the entry too.
    private static readonly ConditionalWeakTable<Document, ConcurrentDictionary<ImageData, EncodedImage>> Images = [];
    private static readonly ConditionalWeakTable<Document, FontProvider> Fonts = [];

    public static void Draw(PageLayout page, Canvas canvas, LayoutDrawObserver? observer)
    {
        IEnumerable<ObjectBox> floats = page.Boxes.OfType<ObjectBox>();
        foreach (ObjectBox box in floats.Where(f => Wrap(f) == WrapMode.BehindText))
        {
            DrawBox(box, canvas, observer);
        }

        foreach (LayoutBox box in page.Boxes.Where(b => b is not ObjectBox))
        {
            DrawBox(box, canvas, observer);
        }

        foreach (ObjectBox box in floats.Where(f => Wrap(f) != WrapMode.BehindText))
        {
            DrawBox(box, canvas, observer);
        }

        if (page.ChangeBars)
        {
            DrawChangeBars(page, canvas, observer);
        }

        // The note separator: a third of the body's width, as Word draws it.
        if (page.Boxes.OfType<AreaBox>().FirstOrDefault(a => a.Area == PageArea.Notes) is { } notes)
        {
            observer?.BeginDecoration();
            canvas.DrawLine(new Vector2((float)notes.Bounds.X, (float)notes.Bounds.Y + 6), new Vector2((float)(notes.Bounds.X + (notes.Bounds.Width / 3)), (float)notes.Bounds.Y + 6), new Stroke(0.5f), Black);
            observer?.EndDecoration();
        }
    }

    /// <summary>
    /// A bar in the outside margin beside each line with a tracked change, as Word marks changed lines: in the left margin,
    /// or with mirrored margins in the right one on odd pages.
    /// </summary>
    private static void DrawChangeBars(PageLayout page, Canvas canvas, LayoutDrawObserver? observer)
    {
        if (page.Boxes.OfType<AreaBox>().FirstOrDefault(a => a.Area == PageArea.Body) is not { } body)
        {
            return;
        }

        bool right = page.Section.Document.Settings.MirrorMargins && page.Index % 2 == 0;
        float x = (float)(right ? body.Bounds.X + body.Bounds.Width + 9 : body.Bounds.X - 9);
        bool begun = false;
        foreach (LineBox line in page.Boxes.SelectMany(b => b.Descendants()).OfType<LineBox>())
        {
            if (line.Content.Changes is not { Count: > 0 } changes || !Touches(changes, line.Line.Start, line.Line.Length))
            {
                continue;
            }

            if (!begun)
            {
                observer?.BeginDecoration();
                begun = true;
            }

            canvas.DrawLine(new Vector2(x, (float)line.Bounds.Y), new Vector2(x, (float)line.Bounds.Bottom), new Stroke(1f), Black);
        }

        if (begun)
        {
            observer?.EndDecoration();
        }

        static bool Touches(IReadOnlyList<(int Start, int Length)> changes, int start, int length)
        {
            foreach ((int changeStart, int changeLength) in changes)
            {
                if (changeLength == 0 ? changeStart >= start && changeStart <= start + length : changeStart < start + length && changeStart + changeLength > start)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static WrapMode Wrap(ObjectBox box)
    {
        return box.Node switch
        {
            Picture { Floating: { } f } => f.Wrap,
            Shape { Floating: { } f } => f.Wrap,
            Island { Floating: { } f } => f.Wrap,
            _ => WrapMode.InFrontOfText,
        };
    }

    private static void DrawBox(LayoutBox box, Canvas canvas, LayoutDrawObserver? observer)
    {
        observer?.BeginBox(box);
        DrawBoxContent(box, canvas, observer);
        observer?.EndBox(box);
    }

    private static void DrawBoxContent(LayoutBox box, Canvas canvas, LayoutDrawObserver? observer)
    {
        switch (box)
        {
            case CellBox cell:
                if (cell.Fill is { } fill)
                {
                    observer?.BeginDecoration();
                    canvas.FillRect(Rect(cell.Bounds), fill);
                    observer?.EndDecoration();
                }

                DrawChildren(cell, canvas, observer);
                observer?.BeginDecoration();
                DrawBorders(canvas, cell.Bounds, cell.Borders.Top, cell.Borders.Left, cell.Borders.Bottom, cell.Borders.Right);
                observer?.EndDecoration();
                return;
            case ParagraphBox paragraph:
                observer?.BeginDecoration();
                DrawParagraphDecorations(paragraph, canvas);
                observer?.EndDecoration();
                DrawChildren(paragraph, canvas, observer);
                return;
            case LineBox line:
                DrawLine(line, canvas, observer);
                return;
            case ObjectBox item:
                DrawObject(item, canvas, observer);
                return;
            case ContainerBox container:
                DrawStyledBox(container, container.Style, container.Block.Document.Theme, container.IsContinuation, container.Continues, canvas, observer);
                return;
            case ItemBox item:
                DrawStyledBox(item, item.Style, item.Item.Document.Theme, item.IsContinuation, item.Continues, canvas, observer);
                return;
            case ScaledBox scaled:
                canvas.Save();
                canvas.Translate((float)scaled.Bounds.X, (float)scaled.Bounds.Y);
                canvas.Scale((float)scaled.Scale);
                canvas.Translate((float)-scaled.Bounds.X, (float)-scaled.Bounds.Y);
                DrawChildren(scaled, canvas, observer);
                canvas.Restore();
                return;
            default:
                DrawChildren(box, canvas, observer);
                return;
        }
    }

    private static void DrawChildren(LayoutBox box, Canvas canvas, LayoutDrawObserver? observer)
    {
        foreach (LayoutBox child in box.Children)
        {
            DrawBox(child, canvas, observer);
        }
    }

    private static void DrawParagraphDecorations(ParagraphBox box, Canvas canvas)
    {
        ResolvedParagraphFormat format = box.Paragraph.GetEffectiveFormat();
        Theme theme = box.Paragraph.Document.Theme;
        LayoutRect bounds = box.Bounds;
        double left = bounds.X + format.LeftIndent;
        LayoutRect area = new(left, bounds.Y, Math.Max(0, bounds.Width - format.LeftIndent - format.RightIndent), bounds.Height);
        if (format.Shading is { } shading && shading.Fill.Kind != DocumentColorKind.Auto)
        {
            canvas.FillRect(Rect(area), shading.Fill.Resolve(theme, default));
        }

        // The top and bottom borders sit their space away from the text (the page flow leaves that room).
        Border? top = box.IsContinuation ? null : format.BorderTop;
        Border? bottom = box.Continues ? null : format.BorderBottom;
        double above = top is { IsVisible: true } ? top.Space + (top.Width / 2) : 0;
        double below = bottom is { IsVisible: true } ? bottom.Space + (bottom.Width / 2) : 0;
        LayoutRect framed = new(area.X, area.Y - above, area.Width, area.Height + above + below);
        DrawBorders(canvas, framed, top, format.BorderLeft, bottom, format.BorderRight, theme);
    }

    /// <summary>
    /// The run with only its own text: a laid-out run carries its whole paragraph's text, and a canvas that writes text
    /// (PDF's ToUnicode and ActualText) would give each line the paragraph's text.
    /// </summary>
    private static GlyphRun OwnText(GlyphRun glyphs, LayoutRun run)
    {
        if (glyphs.Text is not { } text || glyphs.Clusters.Length != glyphs.Count || !glyphs.Transforms.IsEmpty
            || (run.Start <= 0 && run.Start + run.Length >= text.Length))
        {
            return glyphs;
        }

        int start = Math.Clamp(run.Start, 0, text.Length);
        int length = Math.Clamp(run.Length, 0, text.Length - start);
        int[] clusters = new int[glyphs.Count];
        for (int i = 0; i < clusters.Length; i++)
        {
            clusters[i] = Math.Clamp(glyphs.Clusters[i] - start, 0, length);
        }

        return GlyphRun.Create(glyphs.Font, glyphs.Glyphs, glyphs.Positions, text.Substring(start, length), clusters);
    }

    /// <summary>
    /// The number of hyphen glyphs automatic hyphenation added at the end of a line's last run (left to right), or 0: the line
    /// ends inside a word, not at an optional hyphen, and the run ends in the hyphen-minus glyph the paragraph style draws
    /// there, which the word's own last letter cannot be.
    /// </summary>
    private static int AutoHyphenGlyphs(TextLine line, LayoutRun run, GlyphRun glyphs, string text)
    {
        int end = line.Start + line.Length;
        if (run.Start + run.Length != end || (run.BidiLevel & 1) != 0 || end <= 0 || end >= text.Length
            || !char.IsLetter(text[end - 1]) || !char.IsLetter(text[end]) || glyphs.Count < 2
            || !glyphs.Transforms.IsEmpty || glyphs.Clusters.Length != glyphs.Count)
        {
            return 0;
        }

        ushort hyphen = glyphs.Font.Typeface.GetGlyph('-');
        return hyphen != 0 && glyphs.Glyphs[^1] == hyphen && glyphs.Clusters[^1] >= end - 1 ? 1 : 0;
    }

    private static void DrawLine(LineBox line, Canvas canvas, LayoutDrawObserver? observer)
    {
        TextLine text = line.Line;
        Egami.Fonts.Layout.Paragraph paragraph = line.Content.Text;
        Vector2 origin = new((float)line.Bounds.X, (float)(line.Baseline - text.Ascent - text.Top));

        // Backgrounds (highlight and run shading) fill the line's height behind each run.
        foreach (LayoutRun run in text.Runs)
        {
            TextStyle style = paragraph.GetStyle(run.StyleIndex);
            if (style.Background is { } background && run.Advance > 0)
            {
                canvas.FillRect(RectF.FromEdges(origin.X + run.X, (float)line.Bounds.Y, origin.X + run.X + run.Advance, (float)line.Bounds.Bottom), background);
            }
        }

        foreach (LayoutRun run in text.Runs)
        {
            if (run.Glyphs is not GlyphRun glyphs || glyphs.Count == 0)
            {
                continue;
            }

            TextStyle style = paragraph.GetStyle(run.StyleIndex);
            Rgba8 color = style.Color ?? Black;
            if (AutoHyphenGlyphs(text, run, glyphs, paragraph.Text) is > 0 and int hyphen)
            {
                // The hyphen automatic hyphenation adds stands for no text: it is marked as a soft hyphen (U+00AD), as the
                // PDF standard asks (ISO 32000-2, 14.8.2.2.3), so the word reads whole when text is extracted or searched.
                int body = glyphs.Count - hyphen;
                canvas.DrawGlyphRun(OwnText(GlyphRun.Create(glyphs.Font, glyphs.Glyphs[..body], glyphs.Positions[..body], glyphs.Text, glyphs.Clusters[..body]), run), origin, color);
                canvas.BeginMarkedContent(new MarkedContent("Span") { ActualText = "­" });
                canvas.DrawGlyphRun(GlyphRun.Create(glyphs.Font, glyphs.Glyphs[body..], glyphs.Positions[body..]), origin, color);
                canvas.EndMarkedContent();
            }
            else if (observer is not null && line.Content.Rubies is { } rubies && text.AnnotationRuns.Count > 0)
            {
                DrawWithRubyBases(canvas, observer, text, rubies, run, glyphs, origin, color);
            }
            else
            {
                canvas.DrawGlyphRun(OwnText(glyphs, run), origin, color);
            }

            if (style.Decoration is { } decoration && run.Advance > 0)
            {
                DrawDecoration(canvas, decoration, style, origin.X + run.X, run.Advance, (float)line.Baseline - (style.BaselineShift ?? 0));
            }
        }

        // Ruby in its own style's color, emphasis marks in theirs (the text's when they set none); the marks stand for no text.
        foreach (LayoutRun run in text.AnnotationRuns)
        {
            if (run.Glyphs is not GlyphRun glyphs || glyphs.Count == 0)
            {
                continue;
            }

            TextStyle style = paragraph.GetStyle(run.StyleIndex);
            Ruby? ruby = run.Kind == LayoutRunKind.Ruby && line.Content.Rubies is { } rubies && run.Annotation >= 0 && run.Annotation < rubies.Count ? rubies[run.Annotation] : null;
            if (ruby is not null)
            {
                observer?.BeginRuby(ruby, true);
            }
            else
            {
                observer?.BeginMarks();
            }

            Rgba8 color = run.Kind == LayoutRunKind.Emphasis && style.Emphasis?.Color is { } mark ? mark : style.Color ?? Black;
            canvas.DrawGlyphRun(glyphs, origin, color);
            observer?.EndLinePart();
        }
    }

    /// <summary>
    /// A run drawn in pieces at the edges of the ruby bases it holds, each base's piece inside
    /// <see cref="LayoutDrawObserver.BeginRuby"/>, so a tagged PDF puts it in its ruby's RB element.
    /// </summary>
    private static void DrawWithRubyBases(Canvas canvas, LayoutDrawObserver observer, TextLine line, IReadOnlyList<Ruby> rubies, LayoutRun run, GlyphRun glyphs, Vector2 origin, Rgba8 color)
    {
        int RubyAt(int index)
        {
            foreach (LayoutRun annotation in line.AnnotationRuns)
            {
                if (annotation.Kind == LayoutRunKind.Ruby && index >= annotation.Start && index < annotation.Start + annotation.Length)
                {
                    return annotation.Annotation;
                }
            }

            return -1;
        }

        int end = run.Start + run.Length;
        bool any = false;
        for (int i = run.Start; i < end && !any; i++)
        {
            any = RubyAt(i) >= 0;
        }

        if (!any || (run.BidiLevel & 1) != 0 || glyphs.Clusters.Length != glyphs.Count || !glyphs.Transforms.IsEmpty || glyphs.Text is not { } text)
        {
            canvas.DrawGlyphRun(OwnText(glyphs, run), origin, color);
            return;
        }

        for (int first = 0; first < glyphs.Count;)
        {
            int ruby = RubyAt(glyphs.Clusters[first]), last = first + 1;
            while (last < glyphs.Count && RubyAt(glyphs.Clusters[last]) == ruby)
            {
                last++;
            }

            int textStart = Math.Clamp(glyphs.Clusters[first], 0, text.Length);
            int textEnd = last < glyphs.Count ? Math.Clamp(glyphs.Clusters[last], textStart, text.Length) : Math.Clamp(end, textStart, text.Length);
            int[] clusters = new int[last - first];
            for (int g = 0; g < clusters.Length; g++)
            {
                clusters[g] = Math.Clamp(glyphs.Clusters[first + g] - textStart, 0, textEnd - textStart);
            }

            GlyphRun piece = GlyphRun.Create(glyphs.Font, glyphs.Glyphs[first..last], glyphs.Positions[first..last], text[textStart..textEnd], clusters);
            if (ruby >= 0 && ruby < rubies.Count)
            {
                observer.BeginRuby(rubies[ruby], false);
                canvas.DrawGlyphRun(piece, origin, color);
                observer.EndLinePart();
            }
            else
            {
                canvas.DrawGlyphRun(piece, origin, color);
            }

            first = last;
        }
    }

    private static void DrawDecoration(Canvas canvas, TextDecoration decoration, TextStyle style, float x, float width, float baseline)
    {
        float size = style.Size ?? 10;
        float thickness = decoration.Thickness ?? Math.Max(0.5f, size / 18);
        Rgba8 color = decoration.Color ?? style.Color ?? Black;
        Stroke stroke = new(thickness);
        if (decoration.Style is TextDecorationStyle.Dotted or TextDecorationStyle.Dashed)
        {
            stroke = stroke with { Dash = new DashPattern(decoration.Style == TextDecorationStyle.Dotted ? [thickness, thickness * 2] : [thickness * 4, thickness * 2], 0) };
        }

        void Line(float y)
        {
            canvas.DrawLine(new Vector2(x, y), new Vector2(x + width, y), stroke, color);
            if (decoration.Style == TextDecorationStyle.Double)
            {
                canvas.DrawLine(new Vector2(x, y + (thickness * 2)), new Vector2(x + width, y + (thickness * 2)), stroke, color);
            }
        }

        if (decoration.Lines.HasFlag(TextDecorationLines.Underline))
        {
            Line(baseline + (size * 0.12f));
        }

        if (decoration.Lines.HasFlag(TextDecorationLines.LineThrough))
        {
            Line(baseline - (size * 0.28f));
        }

        // An overline at the em box's top, as browsers draw it, clear of capitals and most accents.
        if (decoration.Lines.HasFlag(TextDecorationLines.Overline))
        {
            Line(baseline - (size * 0.86f));
        }
    }

    private static void DrawObject(ObjectBox box, Canvas canvas, LayoutDrawObserver? observer)
    {
        RectF rect = Rect(box.Bounds);

        // A text box's frame is decoration around its text; a picture or a shape without text is the content itself.
        bool frame = box.Children.Count > 0;
        if (frame)
        {
            observer?.BeginDecoration();
        }

        switch (box.Node)
        {
            case Picture picture when picture.Image.Bytes.Length > 0:
                DrawImage(canvas, picture.Document, picture.Image, rect);
                break;
            case Island { Kind: IslandKind.Math } island when OfficeMath.MathRendering.Box(island) is { } math:
                math.Draw(canvas, new Vector2(rect.Left, rect.Top + math.Ascent), new SolidPaint(Black));
                break;
            case Island island when DrawDrawing(island, canvas, rect):
                break;
            case Island { Fallback: { } fallback } island:
                DrawImage(canvas, island.Document, fallback, rect);
                break;
            case Shape shape when DrawDrawing(shape, canvas, rect):
                break;
            case Shape shape:
                {
                    Theme theme = shape.Document.Theme;
                    Egami.Drawing.Path path = shape.Kind switch
                    {
                        ShapeKind.Ellipse => Egami.Drawing.Path.Oval(rect),
                        ShapeKind.Line => Egami.Drawing.Path.Polygon([new Vector2(rect.Left, rect.Top), new Vector2(rect.Right, rect.Bottom)], close: false),
                        ShapeKind.RoundedRectangle => Egami.Drawing.Path.RoundRect(new RoundRect(rect, Corner(shape, rect))),
                        ShapeKind.Rectangle when shape.CornerRadius > 0 => Egami.Drawing.Path.RoundRect(new RoundRect(rect, Corner(shape, rect))),
                        _ => Egami.Drawing.Path.Rect(rect),
                    };
                    if (shape.Shadow is { } shadow && shape.Kind != ShapeKind.Line)
                    {
                        CornerRadii corners = shape.Kind == ShapeKind.RoundedRectangle || shape.CornerRadius > 0 ? new CornerRadii(Corner(shape, rect)) : CornerRadii.Zero;
                        DrawShadow(canvas, rect, corners, shadow, theme, shape.Kind == ShapeKind.Ellipse);
                    }

                    if (shape.Fill is { } fill && shape.Kind != ShapeKind.Line)
                    {
                        canvas.FillPath(path, fill.Resolve(theme, new Rgba8(255, 255, 255, 255)));
                    }

                    if (shape.Outline is { Style: not EgamiBorderStyle.None } outline)
                    {
                        canvas.StrokePath(path, new Stroke((float)Math.Max(0.25, outline.Width)), outline.Color.Resolve(theme, Black));
                    }

                    break;
                }
        }

        if (frame)
        {
            observer?.EndDecoration();
        }

        DrawChildren(box, canvas, observer);
    }

    /// <summary>
    /// Draws a drawing Egap draws itself into <paramref name="rect"/>: a chart (one without a picture of its own, or one
    /// the chart renderer draws), a DrawingML shape group, SmartArt, or a DrawingML shape; false, drawing nothing, for
    /// anything else. Formats that hold no such drawing write what this draws as a picture.
    /// </summary>
    internal static bool DrawDrawing(Inline node, Canvas canvas, RectF rect)
    {
        switch (node)
        {
            case Island { Kind: IslandKind.Chart, Chart: { } chart } island when island.Fallback is null || ChartRenderer.Draws(chart):
                DrawChart(canvas, island, chart, rect);
                return true;
            case Island { Kind: IslandKind.ShapeGroup } island when DocxDrawings.GroupMarkup(island) is { } group:
                GroupPainter.Draw(canvas, rect, group, DocxDrawings.Context(island, (c, image, r) => DrawImage(c, island.Document, image, r)));
                return true;
            case Island { Kind: IslandKind.SmartArt } island when DocxDrawings.DiagramTree(island) is { } tree:
                GroupPainter.Draw(
                    canvas,
                    rect,
                    tree,
                    DocxDrawings.Context(island, (c, image, r) => DrawImage(c, island.Document, image, r)),
                    space: (0, 0, island.Width * DrawingNs.EmuPerPoint, island.Height * DrawingNs.EmuPerPoint));
                return true;
            case Island island when DocxDrawings.DrawsAsList(island):
                SmartArtList.Draw(canvas, rect, island.Text, DocxDrawings.Context(island, (_, _, _) => { }));
                return true;
            case Shape shape when DocxDrawings.ShapeMarkup(shape) is { } wsp:
                ShapePainter.Draw(canvas, rect, wsp.Element(Docx.Ns.Wps + "spPr"), wsp.Element(Docx.Ns.Wps + "style"), DocxDrawings.Context(shape, (c, image, r) => DrawImage(c, shape.Document, image, r)));
                return true;
            default:
                return false;
        }
    }

    /// <summary>A rounded shape's corner radius: its own, or a sixth of its shorter side, as Word's rounded rectangle draws it.</summary>
    private static float Corner(Shape shape, RectF rect)
    {
        return shape.CornerRadius > 0 ? (float)shape.CornerRadius : Math.Min(rect.Width, rect.Height) / 6;
    }

    /// <summary>
    /// A flex or grid container, an item or a boxed group: its shadow, background, children and borders, the corners rounded;
    /// a part that continues from or on another page is drawn open on that side, square there.
    /// </summary>
    private static void DrawStyledBox(LayoutBox box, BoxStyle? style, Theme theme, bool continued, bool continues, Canvas canvas, LayoutDrawObserver? observer)
    {
        if (style is null || !style.Draws)
        {
            DrawChildren(box, canvas, observer);
            return;
        }

        RectF rect = Rect(box.Bounds);
        BoxCorners r = style.CornerRadius;
        CornerRadii corners = r.IsSquare ? CornerRadii.Zero : new CornerRadii(
            continued ? 0 : (float)r.TopLeft, continued ? 0 : (float)r.TopRight, continues ? 0 : (float)r.BottomRight, continues ? 0 : (float)r.BottomLeft);
        observer?.BeginDecoration();
        if (style.Shadow is { } shadow)
        {
            DrawShadow(canvas, rect, corners, shadow, theme, oval: false);
        }

        if (style.Background is { } background && background.Kind != DocumentColorKind.Auto)
        {
            Rgba8 fill = background.Resolve(theme, default);
            if (r.IsSquare)
            {
                canvas.FillRect(rect, fill);
            }
            else
            {
                canvas.FillRoundRect(new RoundRect(rect, corners), fill);
            }
        }

        observer?.EndDecoration();
        DrawChildren(box, canvas, observer);
        observer?.BeginDecoration();
        Border? top = continued ? null : style.BorderTop;
        Border? bottom = continues ? null : style.BorderBottom;
        if (!r.IsSquare && Uniform(style) is { } border && top is not null && bottom is not null)
        {
            // One border all round with rounded corners: one stroke along the rounded outline, inside the box's edge.
            float half = (float)border.Width / 2;
            RectF inner = RectF.FromEdges(rect.Left + half, rect.Top + half, rect.Right - half, rect.Bottom - half);
            CornerRadii radii = new(Math.Max(0, (float)r.TopLeft - half), Math.Max(0, (float)r.TopRight - half), Math.Max(0, (float)r.BottomRight - half), Math.Max(0, (float)r.BottomLeft - half));
            canvas.StrokeRoundRect(new RoundRect(inner, radii), BorderStroke(border), border.Color.Kind == DocumentColorKind.Auto ? Black : border.Color.Resolve(theme, Black));
        }
        else
        {
            // CSS draws a border inside the box's edge: each line is centred half its width in.
            LayoutRect b = box.Bounds;
            double left = Half(style.BorderLeft), right = Half(style.BorderRight), above = Half(top), below = Half(bottom);
            DrawBorders(canvas, new LayoutRect(b.X + left, b.Y + above, b.Width - left - right, b.Height - above - below), top, style.BorderLeft, bottom, style.BorderRight, theme);
        }

        observer?.EndDecoration();

        static double Half(Border? side) => side is { IsVisible: true } ? side.Width / 2 : 0;
    }

    /// <summary>The border every side has, when all four are the same and visible; null otherwise.</summary>
    private static Border? Uniform(BoxStyle style)
    {
        return style.BorderTop is { IsVisible: true } top && Equals(top, style.BorderRight) && Equals(top, style.BorderBottom) && Equals(top, style.BorderLeft) ? top : null;
    }

    private static Stroke BorderStroke(Border border)
    {
        float width = (float)border.Width;
        Stroke stroke = new(width);
        return border.Style is EgamiBorderStyle.Dotted or EgamiBorderStyle.Dashed or EgamiBorderStyle.DashSmallGap or EgamiBorderStyle.DotDash or EgamiBorderStyle.DotDotDash
            ? stroke with { Dash = new DashPattern(border.Style == EgamiBorderStyle.Dotted ? [width, width * 2] : [width * 4, width * 2], 0) }
            : stroke;
    }

    /// <summary>
    /// A box's shadow (CSS box-shadow): its shape moved, grown by the spread and blurred, the blur's standard deviation half its
    /// radius as CSS defines it, scaled to the canvas's device pixels.
    /// </summary>
    private static void DrawShadow(Canvas canvas, RectF rect, CornerRadii corners, BoxShadow shadow, Theme theme, bool oval)
    {
        float spread = (float)shadow.Spread;
        RectF moved = RectF.FromEdges(rect.Left + (float)shadow.OffsetX - spread, rect.Top + (float)shadow.OffsetY - spread, rect.Right + (float)shadow.OffsetX + spread, rect.Bottom + (float)shadow.OffsetY + spread);
        if (shadow.Opacity <= 0 || moved.Width <= 0 || moved.Height <= 0)
        {
            return;
        }

        Rgba8 color = shadow.Color.Kind == DocumentColorKind.Auto ? Black : shadow.Color.Resolve(theme, Black);
        Matrix3x2 matrix = canvas.Matrix;
        float scale = MathF.Sqrt(MathF.Abs((matrix.M11 * matrix.M22) - (matrix.M12 * matrix.M21)));
        float sigma = Math.Min(MaskBlur.MaxSigma, (float)shadow.Blur / 2 * (scale > 0 ? scale : 1));
        SolidPaint paint = new(color) { Opacity = (float)Math.Clamp(shadow.Opacity, 0, 1), Blur = sigma > 0 ? new MaskBlur(sigma, BlurStyle.Normal) : null };
        if (oval)
        {
            canvas.FillOval(moved, paint);
        }
        else
        {
            canvas.FillRoundRect(new RoundRect(moved, Grow(corners, spread)), paint);
        }
    }

    /// <summary>Each rounded corner grown by <paramref name="by"/>, as CSS grows a shadow's corners with its spread; square ones stay square.</summary>
    private static CornerRadii Grow(CornerRadii corners, float by)
    {
        return new CornerRadii(G(corners.TopLeft), G(corners.TopRight), G(corners.BottomRight), G(corners.BottomLeft));

        float G(Vector2 radius) => radius.X <= 0 ? 0 : Math.Max(0, radius.X + by);
    }

    /// <summary>Draws an image the file stores, decoding it with Egami's codecs; one Egami cannot decode is left out.</summary>
    /// <summary>The font chain of a document's last layout, for what draws later than it lays out.</summary>
    internal static void RememberFonts(Document document, FontProvider fonts) => Fonts.AddOrUpdate(document, fonts);

    /// <summary>The fonts the document was laid out with, if it was.</summary>
    internal static FontProvider? FontsOf(Document document) => Fonts.TryGetValue(document, out FontProvider? fonts) ? fonts : null;

    private static void DrawChart(Canvas canvas, Island island, Chart chart, RectF rect)
    {
        Fonts.TryGetValue(island.Document, out FontProvider? fonts);
        chart.Draw(canvas, rect, new ChartDrawOptions { Theme = island.Document.Theme, Provider = fonts, Host = ChartHost.Word });
    }

    private static void DrawImage(Canvas canvas, Document document, ImageData image, RectF rect)
    {
        try
        {
            // EMF, WMF and SVG stay vector, drawn by their own drawer onto whatever canvas this is.
            if (PictureDrawers.For(image.ContentType) is { } drawer)
            {
                drawer.Draw(image.Bytes, canvas, rect);
                return;
            }

            canvas.DrawImage(Images.GetOrCreateValue(document).GetOrAdd(image, i => EncodedImage.Load(i.Bytes)), rect);
        }
        catch (Exception e) when (PictureDrawers.IsDrawFailure(e))
        {
            // A format Egami has no decoder for (or a damaged image): the box stays empty.
        }
    }

    private static void DrawBorders(Canvas canvas, LayoutRect bounds, Border? top, Border? left, Border? bottom, Border? right, Theme? theme = null)
    {
        Edge(top, bounds.X, bounds.Y, bounds.Right, bounds.Y);
        Edge(bottom, bounds.X, bounds.Bottom, bounds.Right, bounds.Bottom);
        Edge(left, bounds.X, bounds.Y, bounds.X, bounds.Bottom);
        Edge(right, bounds.Right, bounds.Y, bounds.Right, bounds.Bottom);

        void Edge(Border? border, double x1, double y1, double x2, double y2)
        {
            if (border is null || border.Style == EgamiBorderStyle.None || border.Width <= 0)
            {
                return;
            }

            float width = (float)border.Width;
            Stroke stroke = new(width);
            if (border.Style is EgamiBorderStyle.Dotted or EgamiBorderStyle.Dashed or EgamiBorderStyle.DashSmallGap or EgamiBorderStyle.DotDash or EgamiBorderStyle.DotDotDash)
            {
                stroke = stroke with { Dash = new DashPattern(border.Style == EgamiBorderStyle.Dotted ? [width, width * 2] : [width * 4, width * 2], 0) };
            }

            Rgba8 color = border.Color.Kind == DocumentColorKind.Auto ? Black : border.Color.Resolve(theme, Black);
            canvas.DrawLine(new Vector2((float)x1, (float)y1), new Vector2((float)x2, (float)y2), stroke, color);
            if (border.Style == EgamiBorderStyle.Double)
            {
                // A double border: two lines of the width with a gap of the width between.
                bool horizontal = Math.Abs(y1 - y2) < 0.001;
                float d = width * 2;
                canvas.DrawLine(new Vector2((float)x1 + (horizontal ? 0 : d), (float)y1 + (horizontal ? d : 0)), new Vector2((float)x2 + (horizontal ? 0 : d), (float)y2 + (horizontal ? d : 0)), stroke, color);
            }
        }
    }

    private static RectF Rect(LayoutRect rect)
    {
        return RectF.FromEdges((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom);
    }
}
