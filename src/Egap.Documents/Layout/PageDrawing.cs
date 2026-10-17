using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Pixels;

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

    public static void Draw(PageLayout page, Canvas canvas)
    {
        IEnumerable<ObjectBox> floats = page.Boxes.OfType<ObjectBox>();
        foreach (ObjectBox box in floats.Where(f => Wrap(f) == WrapMode.BehindText))
        {
            DrawBox(box, canvas);
        }

        foreach (LayoutBox box in page.Boxes.Where(b => b is not ObjectBox))
        {
            DrawBox(box, canvas);
        }

        foreach (ObjectBox box in floats.Where(f => Wrap(f) != WrapMode.BehindText))
        {
            DrawBox(box, canvas);
        }

        // The note separator: a third of the body's width, as Word draws it.
        if (page.Boxes.OfType<AreaBox>().FirstOrDefault(a => a.Area == PageArea.Notes) is { } notes)
        {
            canvas.DrawLine(new Vector2((float)notes.Bounds.X, (float)notes.Bounds.Y + 6), new Vector2((float)(notes.Bounds.X + (notes.Bounds.Width / 3)), (float)notes.Bounds.Y + 6), new Stroke(0.5f), Black);
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

    private static void DrawBox(LayoutBox box, Canvas canvas)
    {
        switch (box)
        {
            case CellBox cell:
                if (cell.Fill is { } fill)
                {
                    canvas.FillRect(Rect(cell.Bounds), fill);
                }

                DrawChildren(cell, canvas);
                DrawBorders(canvas, cell.Bounds, cell.Borders.Top, cell.Borders.Left, cell.Borders.Bottom, cell.Borders.Right);
                return;
            case ParagraphBox paragraph:
                DrawParagraphDecorations(paragraph, canvas);
                DrawChildren(paragraph, canvas);
                return;
            case LineBox line:
                DrawLine(line, canvas);
                return;
            case ObjectBox item:
                DrawObject(item, canvas);
                return;
            default:
                DrawChildren(box, canvas);
                return;
        }
    }

    private static void DrawChildren(LayoutBox box, Canvas canvas)
    {
        foreach (LayoutBox child in box.Children)
        {
            DrawBox(child, canvas);
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

        DrawBorders(canvas, area, box.IsContinuation ? null : format.BorderTop, format.BorderLeft, box.Continues ? null : format.BorderBottom, format.BorderRight, theme);
    }

    private static void DrawLine(LineBox line, Canvas canvas)
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
            canvas.DrawGlyphRun(glyphs, origin, style.Color ?? Black);
            if (style.Decoration is { } decoration && run.Advance > 0)
            {
                DrawDecoration(canvas, decoration, style, origin.X + run.X, run.Advance, (float)line.Baseline - (style.BaselineShift ?? 0));
            }
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
    }

    private static void DrawObject(ObjectBox box, Canvas canvas)
    {
        RectF rect = Rect(box.Bounds);
        switch (box.Node)
        {
            case Picture picture when picture.Image.Bytes.Length > 0:
                DrawImage(canvas, picture.Image, rect);
                break;
            case Island { Fallback: { } fallback }:
                DrawImage(canvas, fallback, rect);
                break;
            case Shape shape:
                {
                    Theme theme = shape.Document.Theme;
                    Egami.Drawing.Path path = shape.Kind switch
                    {
                        ShapeKind.Ellipse => Egami.Drawing.Path.Oval(rect),
                        ShapeKind.Line => Egami.Drawing.Path.Polygon([new Vector2(rect.Left, rect.Top), new Vector2(rect.Right, rect.Bottom)], close: false),
                        _ => Egami.Drawing.Path.Rect(rect),
                    };
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

        DrawChildren(box, canvas);
    }

    /// <summary>Draws an image the file stores, decoding it with Egami's codecs; one Egami cannot decode is left out.</summary>
    private static void DrawImage(Canvas canvas, ImageData image, RectF rect)
    {
        try
        {
            if (image.ContentType is "image/x-emf" or "image/x-wmf" or "image/emf" or "image/wmf")
            {
                // Word's EMF and WMF pictures play back through Egami.Metafiles (Phase 6 design Q18).
                using MemoryStream stream = new(image.Bytes.ToArray(), writable: false);
                Egami.Metafiles.Metafile.Load(stream).Draw(canvas, rect);
                return;
            }

            canvas.DrawImage(EncodedImage.Load(image.Bytes), rect);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or ArgumentException or EgamiException)
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
