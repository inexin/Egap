using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.DrawingML;

/// <summary>What a shape painter needs besides the shape: the theme, and the pictures its picture fills name.</summary>
/// <param name="Theme">The document theme, for theme colours and the format scheme's fill, line and effect styles.</param>
/// <param name="Picture">Draws the picture a relationship id names into a rectangle; null when the shape has no picture fills to draw.</param>
internal sealed record ShapePaintContext(Theme Theme, Action<Canvas, string, RectF>? Picture = null);

/// <summary>
/// Draws a DrawingML shape (<c>p:sp</c>, <c>wps:wsp</c>, <c>dsp:sp</c>) on a canvas from its shape properties and its style:
/// geometry, fill, outline with dashes, compound lines and arrowheads, and the outer shadow, glow and soft edge effects. 3D settings
/// are left out: the shape draws as its flat front face.
/// </summary>
internal static class ShapePainter
{
    private static readonly XNamespace A = DrawingNs.A;
    private static readonly Rgba8 White = new(255, 255, 255, 255);
    private static readonly Rgba8 Black = new(0, 0, 0, 255);

    /// <summary>
    /// Draws a shape into <paramref name="box"/>: <paramref name="spPr"/> is its shape properties, <paramref name="style"/> its
    /// <c>style</c> element with the theme references, if any, and <paramref name="groupFill"/> the fill a <c>grpFill</c> takes.
    /// </summary>
    public static void Draw(Canvas canvas, RectF box, XElement? spPr, XElement? style, ShapePaintContext context, XElement? groupFill = null)
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
            Paint(canvas, layout, spPr, style, context, groupFill);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
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

    private static void Paint(Canvas canvas, GeometryLayout layout, XElement? spPr, XElement? style, ShapePaintContext context, XElement? groupFill)
    {
        Theme theme = context.Theme;
        (XElement? fill, Rgba8? fillPlaceholder) = FillOf(spPr, style, theme);
        if (fill?.Name == A + "grpFill")
        {
            fill = groupFill;
        }

        (XElement? line, Rgba8? linePlaceholder) = LineOf(spPr, style, theme);
        (XElement? effects, Rgba8? effectPlaceholder) = EffectsOf(spPr, style, theme);

        Path? outline = null;
        foreach (GeometryPath path in layout.Paths)
        {
            if (path.Fill != PathFill.None)
            {
                outline = outline is null ? path.Path : outline.Op(path.Path, PathOp.Union);
            }
        }

        if (effects is not null && outline is not null)
        {
            DrawOuterEffects(canvas, outline, effects, theme, effectPlaceholder);
        }

        bool soft = effects?.Element(A + "softEdge") is not null;
        float softRadius = soft ? (float)((DrawingNs.Double((string?)effects!.Element(A + "softEdge")!.Attribute("rad")) ?? 0) / DrawingNs.EmuPerPoint) : 0;
        foreach (GeometryPath path in layout.Paths)
        {
            if (path.Fill != PathFill.None && fill is not null)
            {
                FillPath(canvas, path.Path, path.Fill, fill, theme, fillPlaceholder, context, softRadius);
            }
        }

        if (line is null || NoLine(line))
        {
            return;
        }

        foreach (GeometryPath path in layout.Paths)
        {
            if (path.Stroke)
            {
                StrokePath(canvas, path.Path, line, theme, linePlaceholder);
            }
        }
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

    private static void FillPath(Canvas canvas, Path path, PathFill mode, XElement fill, Theme theme, Rgba8? placeholder, ShapePaintContext context, float softRadius)
    {
        if (fill.Name == A + "blipFill")
        {
            if ((string?)fill.Element(A + "blip")?.Attribute(XName.Get("embed", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")) is { } id && context.Picture is { } picture)
            {
                int saved = canvas.Save();
                canvas.ClipPath(path);
                picture(canvas, id, path.Bounds);
                canvas.RestoreToCount(saved);
            }

            return;
        }

        if (PaintOf(fill, path.Bounds, theme, placeholder) is not { } paint)
        {
            return;
        }

        paint = Shade(paint, mode);
        if (softRadius > 0 && paint is SolidPaint solid)
        {
            // A soft edge fades the shape in over the radius from its outline: the shape pulled in by half the radius and blurred
            // by a quarter of it, so it is clear at the outline and solid the radius inside.
            Path inset = path.Op(new Stroke(softRadius) { Join = LineJoin.Round }.ToPath(path), PathOp.Difference);
            Matrix3x2 matrix = canvas.Matrix;
            float scale = MathF.Sqrt(MathF.Abs((matrix.M11 * matrix.M22) - (matrix.M12 * matrix.M21)));
            int saved = canvas.Save();
            canvas.ClipPath(path);
            canvas.FillPath(inset, solid with { Blur = new MaskBlur(Math.Min(MaskBlur.MaxSigma, softRadius / 4 * (scale > 0 ? scale : 1)), BlurStyle.Normal) });
            canvas.RestoreToCount(saved);
            return;
        }

        canvas.FillPath(path, paint);
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
                    List<GradientStop> stops = [];
                    foreach ((double position, XElement stop) in DrawingFills.Stops(fill).OrderBy(s => s.Position))
                    {
                        if (DrawingColors.Resolve(stop, theme, placeholder) is { } stopColor)
                        {
                            stops.Add(new GradientStop((float)Math.Clamp(position, 0, 1), stopColor));
                        }
                    }

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
                        // A path gradient runs from its focus (the fill-to rectangle, the centre by default) out to the shape's edge.
                        XElement? to = shade.Element(A + "fillToRect");
                        double l = (DrawingNs.Double((string?)to?.Attribute("l")) ?? 50000) / 100000;
                        double t = (DrawingNs.Double((string?)to?.Attribute("t")) ?? 50000) / 100000;
                        double r = (DrawingNs.Double((string?)to?.Attribute("r")) ?? 50000) / 100000;
                        double b = (DrawingNs.Double((string?)to?.Attribute("b")) ?? 50000) / 100000;
                        Vector2 focus = new(bounds.X + (float)(bounds.Width * (l + 1 - r) / 2), bounds.Y + (float)(bounds.Height * (t + 1 - b) / 2));
                        float reach = Math.Max(
                            Math.Max(Vector2.Distance(focus, new Vector2(bounds.Left, bounds.Top)), Vector2.Distance(focus, new Vector2(bounds.Right, bounds.Top))),
                            Math.Max(Vector2.Distance(focus, new Vector2(bounds.Left, bounds.Bottom)), Vector2.Distance(focus, new Vector2(bounds.Right, bounds.Bottom))));
                        if ((string?)shade.Attribute("path") != "circle")
                        {
                            reach = Math.Max(Math.Max(focus.X - bounds.Left, bounds.Right - focus.X), Math.Max(focus.Y - bounds.Top, bounds.Bottom - focus.Y));
                        }

                        return new RadialGradient(focus, Math.Max(reach, 0.01f), [.. stops]);
                    }

                    (Vector2 start, Vector2 end) = DrawingFills.LinearEnds(bounds.X, bounds.Y, bounds.Width, bounds.Height, DrawingFills.Angle(fill) ?? 0);
                    return new LinearGradient(start, end, [.. stops]);
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

    private static void StrokePath(Canvas canvas, Path path, XElement line, Theme theme, Rgba8? placeholder)
    {
        float width = (float)((DrawingNs.Double((string?)line.Attribute("w")) ?? 9525) / DrawingNs.EmuPerPoint);
        width = Math.Max(width, 0.25f);
        if (line.Elements().FirstOrDefault(DrawingFills.IsFill) is not { } fill || PaintOf(fill, path.Bounds, theme, placeholder) is not { } paint)
        {
            return;
        }

        Stroke stroke = new(width)
        {
            Cap = (string?)line.Attribute("cap") switch
            {
                "rnd" => LineCap.Round,
                "sq" => LineCap.Square,
                _ => LineCap.Butt,
            },
            Join = line.Element(A + "round") is not null ? LineJoin.Round : line.Element(A + "bevel") is not null ? LineJoin.Bevel : LineJoin.Miter,
        };
        if (line.Element(A + "miter") is { } miter && DrawingNs.Double((string?)miter.Attribute("lim")) is { } limit and > 0)
        {
            stroke = stroke with { MiterLimit = Math.Max(1, (float)(limit / 100000)) };
        }

        if (Dash(line, width) is { } dash)
        {
            stroke = stroke with { Dash = new DashPattern(dash, 0) };
        }

        string compound = (string?)line.Attribute("cmpd") ?? "sng";
        if (compound == "sng")
        {
            canvas.StrokePath(path, stroke, paint);
        }
        else
        {
            canvas.FillPath(Compound(path, stroke, compound), paint);
        }

        Arrowheads(canvas, path, line, width, paint);
    }

    /// <summary>The dash and gap lengths of a line's <c>prstDash</c> or <c>custDash</c>, or null for a solid line.</summary>
    internal static float[]? Dash(XElement line, float width)
    {
        if (line.Element(A + "custDash") is { } custom)
        {
            List<float> lengths = [];
            foreach (XElement ds in custom.Elements(A + "ds"))
            {
                // Dash and space lengths are thousandths of a percent of the line's width.
                lengths.Add((float)Math.Max(0.01, (DrawingNs.Double((string?)ds.Attribute("d")) ?? 0) / 100000 * width));
                lengths.Add((float)Math.Max(0.01, (DrawingNs.Double((string?)ds.Attribute("sp")) ?? 0) / 100000 * width));
            }

            return lengths.Count == 0 ? null : [.. lengths];
        }

        return DrawingFills.Dash((string?)line.Element(A + "prstDash")?.Attribute("val"), width);
    }

    /// <summary>A double, thick-thin, thin-thick or triple line's area: the full stroke less the gaps between its lines.</summary>
    private static Path Compound(Path path, Stroke stroke, string kind)
    {
        float w = stroke.Width;
        Path Band(float width) => (stroke with { Width = width }).ToPath(path);

        return kind switch
        {
            // Two lines and a gap of a third each; thick-thin and thin-thick are drawn the same at this size.
            "dbl" or "thickThin" or "thinThick" => Band(w).Op(Band(w / 3), PathOp.Difference),

            // Three lines and two gaps of a fifth each.
            "tri" => Band(w).Op(Band(w * 3 / 5), PathOp.Difference).Op(Band(w / 5), PathOp.Union),
            _ => Band(w),
        };
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

    /// <summary>The outer shadow and the glow, drawn behind the shape.</summary>
    private static void DrawOuterEffects(Canvas canvas, Path outline, XElement effects, Theme theme, Rgba8? placeholder)
    {
        Matrix3x2 matrix = canvas.Matrix;
        float scale = MathF.Sqrt(MathF.Abs((matrix.M11 * matrix.M22) - (matrix.M12 * matrix.M21)));
        scale = scale > 0 ? scale : 1;

        if (effects.Element(A + "outerShdw") is { } shadow && DrawingColors.Resolve(shadow, theme, placeholder) is { } shadowColor)
        {
            double distance = (DrawingNs.Double((string?)shadow.Attribute("dist")) ?? 0) / DrawingNs.EmuPerPoint;
            double angle = (DrawingNs.Double((string?)shadow.Attribute("dir")) ?? 0) / 60000 * Math.PI / 180;
            float blur = (float)((DrawingNs.Double((string?)shadow.Attribute("blurRad")) ?? 0) / DrawingNs.EmuPerPoint);
            float sx = (float)((DrawingNs.Double((string?)shadow.Attribute("sx")) ?? 100000) / 100000);
            float sy = (float)((DrawingNs.Double((string?)shadow.Attribute("sy")) ?? 100000) / 100000);
            RectF bounds = outline.Bounds;
            Vector2 center = new(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
            Matrix3x2 place = Matrix3x2.CreateScale(sx, sy, center) * Matrix3x2.CreateTranslation((float)(distance * Math.Cos(angle)), (float)(distance * Math.Sin(angle)));
            float sigma = Math.Min(MaskBlur.MaxSigma, blur / 2 * scale);
            canvas.FillPath(outline.Transform(place), new SolidPaint(shadowColor) { Blur = sigma > 0 ? new MaskBlur(sigma, BlurStyle.Normal) : null });
        }

        if (effects.Element(A + "glow") is { } glow && DrawingColors.Resolve(glow, theme, placeholder) is { } glowColor)
        {
            float radius = (float)((DrawingNs.Double((string?)glow.Attribute("rad")) ?? 0) / DrawingNs.EmuPerPoint);
            if (radius > 0)
            {
                float sigma = Math.Min(MaskBlur.MaxSigma, radius / 2 * scale);
                SolidPaint paint = new(glowColor) { Blur = new MaskBlur(sigma, BlurStyle.Normal) };
                canvas.StrokePath(outline, new Stroke(radius) { Join = LineJoin.Round }, paint);
                canvas.FillPath(outline, paint);
            }
        }
    }
}
