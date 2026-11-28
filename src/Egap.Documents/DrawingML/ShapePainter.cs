using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.DrawingML;

/// <summary>What a shape painter needs besides the shape: the theme, and the pictures its picture fills name.</summary>
/// <param name="Theme">The document theme, for theme colours and the format scheme's fill, line and effect styles.</param>
/// <param name="Picture">Draws the picture a relationship id names into a rectangle; null when the shape has no picture fills to draw.</param>
/// <param name="Fonts">The fonts text bodies are set in; null for the document's own.</param>
/// <param name="Blip">The picture a relationship id names, for picture fills drawn with their crops, tiles and colour changes; when
/// null, <paramref name="Picture"/> draws them.</param>
/// <param name="Page">The canvas's matrix at the page's corner, in points: preset patterns are laid from it, as Office lays them
/// on the page; null to lay them from each shape's corner.</param>
internal sealed record ShapePaintContext(Theme Theme, Action<Canvas, string, RectF>? Picture = null, Layout.FontProvider? Fonts = null, Func<string, BlipImage?>? Blip = null, Matrix3x2? Page = null);

/// <summary>
/// Draws a DrawingML shape (<c>p:sp</c>, <c>wps:wsp</c>, <c>dsp:sp</c>) on a canvas from its shape properties and its style:
/// geometry, fill, outline with dashes, compound lines and arrowheads, and the outer shadow, glow and soft edge effects. 3D settings
/// are left out: the shape draws as its flat front face.
/// </summary>
internal static partial class ShapePainter
{
    private static readonly XNamespace A = DrawingNs.A;
    private static readonly Rgba8 White = new(255, 255, 255, 255);
    private static readonly Rgba8 Black = new(0, 0, 0, 255);

    /// <summary>
    /// Draws a shape into <paramref name="box"/>: <paramref name="spPr"/> is its shape properties, <paramref name="style"/> its
    /// <c>style</c> element with the theme references, if any, and <paramref name="groupFill"/> the fill a <c>grpFill</c> takes.
    /// </summary>
    public static void Draw(Canvas canvas, RectF box, XElement? spPr, XElement? style, ShapePaintContext context, XElement? groupFill = null, XElement? txBody = null, RectF? textBox = null, double textRotation = 0)
    {
        if (box.Width < 0 || box.Height < 0)
        {
            return;
        }

        XElement? xfrm = spPr?.Element(A + "xfrm");
        ShapeGeometry geometry = ShapeGeometry.Read(spPr) ?? ShapeGeometry.Preset("rect")!;
        XElement? adjusts = spPr?.Element(A + "prstGeom")?.Element(A + "avLst");
        GeometryLayout layout = geometry.Layout(box.Width, box.Height, adjusts);

        int saved = canvas.Save();
        try
        {
            Transform(canvas, box, xfrm);
            canvas.Translate(box.X, box.Y);
            Paint(canvas, layout, spPr, style, context, groupFill, new Vector2(box.Width, box.Height), xfrm);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }

        if (txBody is not null)
        {
            DrawText(canvas, box, xfrm, layout.TextRect, txBody, style, context, textBox, textRotation);
        }
    }

    /// <summary>
    /// Draws a shape's text into its geometry's text rectangle, turned with the shape but never mirrored, as Office draws it; or,
    /// when the file gives the text its own box (a SmartArt shape's <c>txXfrm</c>), into <paramref name="textBox"/>, in page
    /// space, turned by the shape's rotation plus that box's own <paramref name="textRotation"/>, in degrees.
    /// </summary>
    private static void DrawText(Canvas canvas, RectF box, XElement? xfrm, RectF textRect, XElement txBody, XElement? style, ShapePaintContext context, RectF? textBox, double textRotation)
    {
        double rotation = (DrawingNs.Double((string?)xfrm?.Attribute("rot")) ?? 0) / 60000;
        RectF rect;
        if (textBox is { } own)
        {
            rect = own;
            rotation += textRotation;
        }
        else
        {
            float x = Flag(xfrm, "flipH") ? box.Width - textRect.X - textRect.Width : textRect.X;
            float y = Flag(xfrm, "flipV") ? box.Height - textRect.Y - textRect.Height : textRect.Y;
            rect = new RectF(box.X + x, box.Y + y, textRect.Width, textRect.Height);

            // The text rectangle turns with the shape about the shape's centre; the text itself about its own.
            if (rotation != 0)
            {
                Vector2 center = new(box.X + (box.Width / 2), box.Y + (box.Height / 2));
                Vector2 own2 = new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
                Vector2 moved = Vector2.Transform(own2 - center, Matrix3x2.CreateRotation((float)(rotation * Math.PI / 180))) + center;
                rect = new RectF(moved.X - (rect.Width / 2), moved.Y - (rect.Height / 2), rect.Width, rect.Height);
            }
        }

        TextBodyPainter.Draw(canvas, rect, rotation, txBody, style, context);
    }

    /// <summary>Turns the canvas by the shape's rotation and flips about the box's centre.</summary>
    internal static void Transform(Canvas canvas, RectF box, XElement? xfrm)
    {
        double rotation = (DrawingNs.Double((string?)xfrm?.Attribute("rot")) ?? 0) / 60000;
        bool flipH = Flag(xfrm, "flipH");
        bool flipV = Flag(xfrm, "flipV");
        if (rotation == 0 && !flipH && !flipV)
        {
            return;
        }

        Vector2 center = new(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        canvas.Translate(center.X, center.Y);
        if (rotation != 0)
        {
            canvas.Rotate((float)rotation);
        }

        canvas.Scale(flipH ? -1 : 1, flipV ? -1 : 1);
        canvas.Translate(-center.X, -center.Y);
    }

    internal static bool Flag(XElement? element, string name) => (string?)element?.Attribute(name) is "1" or "true";

    private static void Paint(Canvas canvas, GeometryLayout layout, XElement? spPr, XElement? style, ShapePaintContext context, XElement? groupFill, Vector2 size, XElement? xfrm)
    {
        Theme theme = context.Theme;
        (XElement? fill, Rgba8? fillPlaceholder) = FillOf(spPr, style, theme);
        if (fill?.Name == A + "grpFill")
        {
            fill = groupFill;
        }

        (XElement? line, Rgba8? linePlaceholder) = LineOf(spPr, style, theme);
        (XElement? effects, Rgba8? effectPlaceholder) = EffectsOf(spPr, style, theme);
        Bevel? bevel = DrawingBevels.Of(spPr, effects?.Parent);
        bool stroked = line is not null && !NoLine(line);

        void Body(Canvas target)
        {
            foreach (GeometryPath path in layout.Paths)
            {
                if (path.Fill != PathFill.None && fill is not null)
                {
                    FillPath(target, path.Path, path.Fill, fill, theme, fillPlaceholder, context, bevel);
                }
            }

            if (stroked)
            {
                foreach (GeometryPath path in layout.Paths)
                {
                    if (path.Stroke)
                    {
                        StrokePath(target, path.Path, line!, theme, linePlaceholder, context);
                    }
                }
            }
        }

        if (!DrawingEffects.Any(effects))
        {
            Body(canvas);
            return;
        }

        Path? outline = null;
        foreach (GeometryPath path in layout.Paths)
        {
            if (path.Fill != PathFill.None)
            {
                outline = outline is null ? path.Path : outline.Op(path.Path, PathOp.Union);
            }
        }

        DrawingEffects.Draw(canvas, effects!, theme, effectPlaceholder, new DrawingEffects.Target(size, outline, PageToLocal(canvas, context, xfrm)), Body);
    }

    /// <summary>
    /// The linear map from page directions to the shape's own coordinates: from the page matrix where the caller gave one (so the
    /// groups the shape sits in count too), else from the shape's own rotation and flips.
    /// </summary>
    private static Matrix3x2 PageToLocal(Canvas canvas, ShapePaintContext context, XElement? xfrm)
    {
        Matrix3x2 localToPage;
        if (context.Page is { } page && Matrix3x2.Invert(page, out Matrix3x2 deviceToPage))
        {
            localToPage = canvas.Matrix * deviceToPage;
        }
        else
        {
            double rotation = (DrawingNs.Double((string?)xfrm?.Attribute("rot")) ?? 0) / 60000;
            localToPage = Matrix3x2.CreateScale(Flag(xfrm, "flipH") ? -1 : 1, Flag(xfrm, "flipV") ? -1 : 1) * Matrix3x2.CreateRotation((float)(rotation * Math.PI / 180));
        }

        localToPage.Translation = Vector2.Zero;
        return Matrix3x2.Invert(localToPage, out Matrix3x2 pageToLocal) ? pageToLocal : Matrix3x2.Identity;
    }

    /// <summary>The shape's fill element, its own or the theme's for its <c>fillRef</c>, and the colour that stands in for <c>phClr</c>.</summary>
    internal static (XElement? Fill, Rgba8? Placeholder) FillOf(XElement? spPr, XElement? style, Theme theme)
    {
        if (DrawingFills.Kind(spPr) is { } own)
        {
            return (spPr!.Element(A + own), null);
        }

        if (style?.Element(A + "fillRef") is not { } reference)
        {
            return (null, null);
        }

        Rgba8? placeholder = DrawingColors.Resolve(reference, theme, null);
        int index = (int)(DrawingNs.Double((string?)reference.Attribute("idx")) ?? 0);
        XElement? scheme = theme.FormatScheme;
        XElement? fill = index switch
        {
            0 => null,
            >= 1001 => scheme?.Element(A + "bgFillStyleLst")?.Elements().ElementAtOrDefault(index - 1001),
            _ => scheme?.Element(A + "fillStyleLst")?.Elements().ElementAtOrDefault(index - 1),
        };

        // Without a format scheme, a referenced fill is the reference's own colour.
        return (fill ?? (index == 0 ? null : new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr")))), placeholder);
    }

    /// <summary>The shape's line: the theme's for its <c>lnRef</c> with the shape's own <c>a:ln</c> laid over it.</summary>
    internal static (XElement? Line, Rgba8? Placeholder) LineOf(XElement? spPr, XElement? style, Theme theme)
    {
        XElement? own = spPr?.Element(A + "ln");
        XElement? themed = null;
        Rgba8? placeholder = null;
        if (style?.Element(A + "lnRef") is { } reference)
        {
            placeholder = DrawingColors.Resolve(reference, theme, null);
            int index = (int)(DrawingNs.Double((string?)reference.Attribute("idx")) ?? 0);
            if (index > 0)
            {
                themed = theme.FormatScheme?.Element(A + "lnStyleLst")?.Elements(A + "ln").ElementAtOrDefault(index - 1)
                    ?? new XElement(A + "ln", new XAttribute("w", 6350 * index), new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr"))));
            }
        }

        if (themed is null)
        {
            return (own, null);
        }

        if (own is null)
        {
            return (themed, placeholder);
        }

        // The shape's own settings win: its attributes, its fill and each of its other children replace the theme's.
        XElement merged = new(themed);
        foreach (XAttribute attribute in own.Attributes())
        {
            merged.SetAttributeValue(attribute.Name, attribute.Value);
        }

        if (DrawingFills.Kind(own) is not null)
        {
            merged.Elements().Where(DrawingFills.IsFill).Remove();
        }

        foreach (XElement child in own.Elements())
        {
            if (!DrawingFills.IsFill(child))
            {
                merged.Elements(child.Name).Remove();
                if (child.Name.LocalName is "prstDash" or "custDash")
                {
                    merged.Elements(A + "prstDash").Remove();
                    merged.Elements(A + "custDash").Remove();
                }

                if (child.Name.LocalName is "round" or "bevel" or "miter")
                {
                    merged.Elements(A + "round").Remove();
                    merged.Elements(A + "bevel").Remove();
                    merged.Elements(A + "miter").Remove();
                }
            }

            merged.Add(new XElement(child));
        }

        // A colour of the shape's own keeps the theme's placeholder only where its own fill still names phClr.
        return (merged, placeholder);
    }

    /// <summary>The shape's effects: its own <c>a:effectLst</c>, else the theme's for its <c>effectRef</c>.</summary>
    internal static (XElement? Effects, Rgba8? Placeholder) EffectsOf(XElement? spPr, XElement? style, Theme theme)
    {
        if (spPr?.Element(A + "effectLst") is { } own)
        {
            return (own, null);
        }

        if (style?.Element(A + "effectRef") is not { } reference)
        {
            return (null, null);
        }

        int index = (int)(DrawingNs.Double((string?)reference.Attribute("idx")) ?? 0);
        XElement? effects = index > 0
            ? theme.FormatScheme?.Element(A + "effectStyleLst")?.Elements(A + "effectStyle").ElementAtOrDefault(index - 1)?.Element(A + "effectLst")
            : null;
        return (effects, DrawingColors.Resolve(reference, theme, null));
    }

    private static bool NoLine(XElement line) => DrawingFills.Kind(line) is null or "noFill";

    private static void FillPath(Canvas canvas, Path path, PathFill mode, XElement fill, Theme theme, Rgba8? placeholder, ShapePaintContext context, Bevel? bevel)
    {
        if (fill.Name == A + "blipFill")
        {
            int saved = canvas.Save();
            canvas.ClipPath(path);
            if (context.Blip is { } blip)
            {
                BlipFill.Draw(canvas, path.Bounds, fill, blip, theme);
            }
            else if (BlipFill.Id(fill) is { } id && context.Picture is { } picture)
            {
                picture(canvas, id, path.Bounds);
            }

            canvas.RestoreToCount(saved);
            if (bevel is not null)
            {
                canvas.DrawBevel(path, bevel);
            }

            return;
        }

        if (fill.Name == A + "gradFill" && mode == PathFill.Normal && DrawingGradients.FillPath(canvas, path, fill, theme, placeholder, bevel))
        {
            return;
        }

        Paint? paint = fill.Name == A + "pattFill" ? PatternPaint(canvas, fill, path.Bounds, theme, placeholder, context) : PaintOf(fill, path.Bounds, theme, placeholder);
        if (paint is null)
        {
            return;
        }

        paint = Shade(paint, mode);
        if (bevel is not null)
        {
            canvas.FillBevel(path, paint, bevel);
            return;
        }

        canvas.FillPath(path, paint);
    }

    /// <summary>Fills an area, such as a page's background, with a fill element as a shape's own fill draws.</summary>
    internal static void FillArea(Canvas canvas, Path area, XElement fill, Rgba8? placeholder, ShapePaintContext context) =>
        FillPath(canvas, area, PathFill.Normal, fill, context.Theme, placeholder, context, bevel: null);

    /// <summary>
    /// A preset pattern's paint, its bits laid from the page's corner when the caller gave the page (as Office lays them, upright
    /// and still whatever the shape does) or else from the shape's.
    /// </summary>
    private static Paint PatternPaint(Canvas canvas, XElement fill, RectF bounds, Theme theme, Rgba8? placeholder, ShapePaintContext context)
    {
        Rgba8 ink = DrawingColors.Resolve(fill.Element(A + "fgClr"), theme, placeholder) ?? Black;
        Rgba8 paper = DrawingColors.Resolve(fill.Element(A + "bgClr"), theme, placeholder) ?? White;
        Matrix3x2 bits = Matrix3x2.CreateScale(DrawingPatterns.BitSize);
        Matrix3x2 place = context.Page is { } page && Matrix3x2.Invert(canvas.Matrix, out Matrix3x2 deviceToLocal)
            ? bits * page * deviceToLocal
            : bits * Matrix3x2.CreateTranslation(bounds.X, bounds.Y);
        return DrawingPatterns.Paint((string?)fill.Attribute("prst"), ink, paper, place);
    }

    /// <summary>The paint for a fill element over <paramref name="bounds"/>; null for none or one this does not draw.</summary>
    internal static Paint? PaintOf(XElement fill, RectF bounds, Theme theme, Rgba8? placeholder)
    {
        switch (fill.Name.LocalName)
        {
            case "solidFill":
                return DrawingColors.Resolve(fill, theme, placeholder) is { } color ? new SolidPaint(color) : null;
            case "gradFill":
                {
                    List<GradientStop> stops = DrawingGradients.Stops(fill, theme, placeholder);

                    if (stops.Count == 0)
                    {
                        return null;
                    }

                    if (stops.Count == 1)
                    {
                        return new SolidPaint(stops[0].Color);
                    }

                    if (fill.Element(A + "path") is { } shade)
                    {
                        // A circle path gradient runs from its focus out to the box's farthest corner. PowerPoint takes the focus
                        // at the fill-to rectangle's top left corner (its centre only when the rectangle is a point); without a
                        // fill-to rectangle it is the centre, and within one each missing side is 0, as for any relative rectangle.
                        XElement? to = shade.Element(A + "fillToRect");
                        double side = to is null ? 50000 : 0;
                        double l = (DrawingNs.Double((string?)to?.Attribute("l")) ?? side) / 100000;
                        double t = (DrawingNs.Double((string?)to?.Attribute("t")) ?? side) / 100000;
                        double r = (DrawingNs.Double((string?)to?.Attribute("r")) ?? side) / 100000;
                        double b = (DrawingNs.Double((string?)to?.Attribute("b")) ?? side) / 100000;
                        Vector2 focus = new(bounds.X + (float)(bounds.Width * l), bounds.Y + (float)(bounds.Height * t));
                        float reach = Math.Max(
                            Math.Max(Vector2.Distance(focus, new Vector2(bounds.Left, bounds.Top)), Vector2.Distance(focus, new Vector2(bounds.Right, bounds.Top))),
                            Math.Max(Vector2.Distance(focus, new Vector2(bounds.Left, bounds.Bottom)), Vector2.Distance(focus, new Vector2(bounds.Right, bounds.Bottom))));
                        if ((string?)shade.Attribute("path") != "circle")
                        {
                            reach = Math.Max(Math.Max(focus.X - bounds.Left, bounds.Right - focus.X), Math.Max(focus.Y - bounds.Top, bounds.Bottom - focus.Y));
                        }

                        return new RadialGradient(focus, Math.Max(reach, 0.01f), [.. stops]);
                    }

                    return DrawingGradients.Linear(fill, bounds, [.. stops]);
                }

            case "pattFill":
                {
                    // A hatch comes out at a page's size as its two inks' blend, by how much of the pattern is ink, as charts draw it.
                    Rgba8 ink = DrawingColors.Resolve(fill.Element(A + "fgClr"), theme, placeholder) ?? Black;
                    Rgba8 paper = DrawingColors.Resolve(fill.Element(A + "bgClr"), theme, placeholder) ?? White;
                    return new SolidPaint(DrawingColors.Mix(paper, ink, DrawingFills.PatternInk((string?)fill.Attribute("prst"))));
                }

            default:
                return null;
        }
    }

    /// <summary>A path's own shade of the fill: lighter or darker, as a 3D-looking preset's side faces ask.</summary>
    private static Paint Shade(Paint paint, PathFill mode)
    {
        if (mode is PathFill.Normal or PathFill.None || paint is not SolidPaint { Color: var color } solid)
        {
            return paint;
        }

        Rgba8 rgba = color.ToRgba8();
        Rgba8 shaded = mode switch
        {
            PathFill.Lighten => DrawingColors.Mix(rgba, White, 0.4),
            PathFill.LightenLess => DrawingColors.Mix(rgba, White, 0.2),
            PathFill.Darken => DrawingColors.Scale(rgba, 0.6),
            _ => DrawingColors.Scale(rgba, 0.8),
        };
        return solid with { Color = RgbaF32.FromRgba8(shaded) };
    }

    /// <summary>Draws the head and tail ends (<c>headEnd</c> at the path's start, <c>tailEnd</c> at its end) of an open path.</summary>
    private static void Arrowheads(Canvas canvas, Path path, XElement line, float width, Paint paint)
    {
        XElement? head = line.Element(A + "headEnd");
        XElement? tail = line.Element(A + "tailEnd");
        if (head is null && tail is null)
        {
            return;
        }

        ReadOnlySpan<Vector2> points = path.Points;
        ReadOnlySpan<PathVerb> verbs = path.Verbs;
        if (points.Length < 2 || verbs.Contains(PathVerb.Close))
        {
            return;
        }

        Arrowhead(canvas, head, points[0], points[1], width, paint);
        Arrowhead(canvas, tail, points[^1], points[^2], width, paint);
    }

    private static void Arrowhead(Canvas canvas, XElement? end, Vector2 tip, Vector2 from, float width, Paint paint)
    {
        string type = (string?)end?.Attribute("type") ?? "none";
        if (type == "none")
        {
            return;
        }

        Vector2 direction = tip - from;
        if (direction.LengthSquared() < 1e-9f)
        {
            return;
        }

        direction = Vector2.Normalize(direction);
        Vector2 normal = new(-direction.Y, direction.X);
        float scale = Math.Max(width, 1);
        float length = Size((string?)end!.Attribute("len")) * scale;
        float half = Size((string?)end.Attribute("w")) * scale / 2;
        Vector2 back = tip - (direction * length);
        switch (type)
        {
            case "triangle":
                canvas.FillPath(Path.Polygon([tip, back + (normal * half), back - (normal * half)]), paint);
                break;
            case "stealth":
                canvas.FillPath(Path.Polygon([tip, back + (normal * half), tip - (direction * length * 0.6f), back - (normal * half)]), paint);
                break;
            case "diamond":
                {
                    Vector2 middle = tip - (direction * length / 2);
                    canvas.FillPath(Path.Polygon([tip + (direction * length / 2), middle + (normal * half), tip - (direction * length / 2), middle - (normal * half)]), paint);
                    break;
                }

            case "oval":
                canvas.FillOval(new RectF(tip.X - (length / 2), tip.Y - half, length, half * 2), paint);
                break;
            case "arrow":
                canvas.StrokePath(Path.Polygon([back + (normal * half), tip, back - (normal * half)], close: false), new Stroke(width) { Join = LineJoin.Miter }, paint);
                break;
        }

        // Small, medium and large heads are two, three and five line widths.
        static float Size(string? value) => value switch
        {
            "sm" => 2,
            "lg" => 5,
            _ => 3,
        };
    }
}
