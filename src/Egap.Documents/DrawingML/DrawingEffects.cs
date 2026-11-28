using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.DrawingML;

/// <summary>
/// DrawingML's shape effects (ECMA-376 Part 1, 20.1.8): the outer and preset shadows, glow and reflection behind the shape, the
/// soft edge and blur on it, and the inner shadow inside it. Each works on the shape as drawn (its fill, picture and outline), in
/// a layer of Egami's canvas: shadows and glows are the layer recoloured and blurred, the reflection the layer mirrored and faded
/// through a soft mask.
/// </summary>
internal static class DrawingEffects
{
    private static readonly XNamespace A = DrawingNs.A;

    /// <summary>What the effects of one shape need besides the effect list.</summary>
    /// <param name="Size">The shape's box, from the origin of the canvas's current coordinates.</param>
    /// <param name="Outline">The area the shape fills, for the soft edge and inner shadow; null for a shape with no filled area.</param>
    /// <param name="PageToLocal">Turns a direction on the page into the shape's own coordinates, so a shadow falls the same way whatever the shape's rotation and flips.</param>
    internal readonly record struct Target(Vector2 Size, Path? Outline, Matrix3x2 PageToLocal);

    /// <summary>Whether the list holds an effect drawn on or around the shape.</summary>
    public static bool Any(XElement? effects) => effects?.Elements().Any(e => e.Name.Namespace == A) == true;

    /// <summary>Draws the shape through <paramref name="body"/> with the effects of <paramref name="effects"/> around, on and in it.</summary>
    public static void Draw(Canvas canvas, XElement effects, Theme theme, Rgba8? placeholder, Target target, Action<Canvas> body)
    {
        float scale = DeviceScale(canvas);

        // Behind the shape: shadows, the reflection and the glow.
        if (effects.Element(A + "outerShdw") is { } outer && DrawingColors.Resolve(outer, theme, placeholder) is { } outerColor)
        {
            Shadow(canvas, outer, outerColor, target, scale, body, Vector2.Zero, 1);
        }

        if (effects.Element(A + "prstShdw") is { } preset && DrawingColors.Resolve(preset, theme, placeholder) is { } presetColor)
        {
            PresetShadow(canvas, preset, presetColor, target, scale, body);
        }

        if (effects.Element(A + "reflection") is { } reflection)
        {
            Reflection(canvas, reflection, target, scale, body);
        }

        if (effects.Element(A + "glow") is { } glow && DrawingColors.Resolve(glow, theme, placeholder) is { } glowColor)
        {
            Glow(canvas, glow, glowColor, scale, body);
        }

        // The shape itself, through its soft edge and blur.
        XElement? soft = effects.Element(A + "softEdge");
        XElement? blur = effects.Element(A + "blur");
        float softRadius = soft is null ? 0 : Points(soft, "rad");
        float blurRadius = blur is null ? 0 : Points(blur, "rad");
        LayerOptions? layer = null;
        if (softRadius > 0 && target.Outline is { } outline)
        {
            layer = new LayerOptions { Mask = SoftEdgeMask(outline, softRadius, scale) };
        }

        if (blurRadius > 0)
        {
            float sigma = Math.Min(MaskBlur.MaxSigma, blurRadius / 2 * scale);
            layer = (layer ?? new LayerOptions()) with { Filter = ops => ops.Blur(sigma) };
        }

        if (layer is not null)
        {
            int saved = canvas.SaveLayer(layer);
            body(canvas);
            canvas.RestoreToCount(saved);
        }
        else
        {
            body(canvas);
        }

        // Inside it: the inner shadow.
        if (effects.Element(A + "innerShdw") is { } inner && target.Outline is { } area && DrawingColors.Resolve(inner, theme, placeholder) is { } innerColor)
        {
            InnerShadow(canvas, inner, innerColor, area, target, scale);
        }
    }

    /// <summary>The canvas's scale from its current units to device pixels.</summary>
    public static float DeviceScale(Canvas canvas)
    {
        Matrix3x2 m = canvas.Matrix;
        float scale = MathF.Sqrt(MathF.Abs((m.M11 * m.M22) - (m.M12 * m.M21)));
        return scale > 0 && float.IsFinite(scale) ? scale : 1;
    }

    private static float Points(XElement element, string name) => (float)((DrawingNs.Double((string?)element.Attribute(name)) ?? 0) / DrawingNs.EmuPerPoint);

    private static double Fraction(XElement element, string name, double fallback) =>
        DrawingNs.Double((string?)element.Attribute(name)) is { } value ? value / 100000 : fallback;

    private static double Degrees(XElement element, string name) => (DrawingNs.Double((string?)element.Attribute(name)) ?? 0) / 60000;

    /// <summary>The offset an effect's <c>dist</c> and <c>dir</c> give, in the shape's coordinates.</summary>
    private static Vector2 Offset(XElement effect, Target target)
    {
        double distance = Points(effect, "dist");
        double angle = Degrees(effect, "dir") * Math.PI / 180;
        Vector2 page = new((float)(distance * Math.Cos(angle)), (float)(distance * Math.Sin(angle)));

        // An effect that turns with the shape (rotWithShape, true unless the file says not) takes its direction in the shape's
        // own coordinates; one that does not keeps the page's.
        return (string?)effect.Attribute("rotWithShape") is "0" or "false" ? Vector2.TransformNormal(page, target.PageToLocal) : page;
    }

    /// <summary>The point of the shape's box an alignment (<c>algn</c>) names, which scaling and skewing keep in place.</summary>
    private static Vector2 Anchor(string? align, Vector2 size) => align switch
    {
        "tl" => new Vector2(0, 0),
        "t" => new Vector2(size.X / 2, 0),
        "tr" => new Vector2(size.X, 0),
        "l" => new Vector2(0, size.Y / 2),
        "ctr" => new Vector2(size.X / 2, size.Y / 2),
        "r" => new Vector2(size.X, size.Y / 2),
        "bl" => new Vector2(0, size.Y),
        "br" => new Vector2(size.X, size.Y),
        _ => new Vector2(size.X / 2, size.Y),
    };

    /// <summary>Scales and skews about an anchor, then moves: a shadow's or reflection's place from the shape's.</summary>
    private static Matrix3x2 Place(Vector2 anchor, float sx, float sy, double kx, double ky, Vector2 offset) =>
        Matrix3x2.CreateTranslation(-anchor)
        * Matrix3x2.CreateScale(sx, sy)
        * Matrix3x2.CreateSkew((float)(kx * Math.PI / 180), (float)(ky * Math.PI / 180))
        * Matrix3x2.CreateTranslation(anchor + offset);

    /// <summary>An outer shadow: the shape as drawn, in the shadow's colour, scaled, skewed, moved and blurred.</summary>
    private static void Shadow(Canvas canvas, XElement shadow, Rgba8 color, Target target, float scale, Action<Canvas> body, Vector2 extra, double alpha)
    {
        Vector2 anchor = Anchor((string?)shadow.Attribute("algn"), target.Size);
        Matrix3x2 place = Place(anchor, (float)Fraction(shadow, "sx", 1), (float)Fraction(shadow, "sy", 1), Degrees(shadow, "kx"), Degrees(shadow, "ky"), Offset(shadow, target) + extra);
        DrawShadow(canvas, place, color, alpha, Points(shadow, "blurRad"), scale, body);
    }

    private static void DrawShadow(Canvas canvas, Matrix3x2 place, Rgba8 color, double alpha, float blurRadius, float scale, Action<Canvas> body)
    {
        float sigma = Math.Min(MaskBlur.MaxSigma, blurRadius / 2 * scale);
        Rgba8 ink = alpha < 1 ? color with { A = (byte)Math.Round(color.A * alpha) } : color;
        int saved = canvas.Save();
        try
        {
            canvas.Concat(place);
            canvas.SaveLayer(new LayerOptions
            {
                Filter = ops =>
                {
                    ops.BlendColor(ink, BlendMode.SourceIn);
                    if (sigma > 0.01f)
                    {
                        ops.Blur(sigma);
                    }
                },
            });
            body(canvas);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>
    /// A preset shadow (<c>prstShdw</c>, ECMA-376 Part 1, 20.1.10.52) at the preset's own scale, skew and second copy, measured from
    /// PowerPoint's drawing of each: drop shadows moved by <c>dist</c> and <c>dir</c>, perspective shadows flattened to half height
    /// and slanted 60 degrees behind or in front, and the doubled ones with a lighter copy.
    /// </summary>
    private static void PresetShadow(Canvas canvas, XElement shadow, Rgba8 color, Target target, float scale, Action<Canvas> body)
    {
        Vector2 offset = Offset(shadow, target);
        Vector2 size = target.Size;
        Vector2 bottom = new(size.X / 2, size.Y);
        Vector2 top = new(size.X / 2, 0);
        const double Slant = 60;
        void Draw(Matrix3x2 place, double alpha = 1) => DrawShadow(canvas, place, color, alpha, 0, scale, body);
        Matrix3x2 Moved(Vector2 by) => Matrix3x2.CreateTranslation(by);

        switch ((string?)shadow.Attribute("prst"))
        {
            case "shdw3" or "shdw11":
                Draw(Place(bottom, 1, 0.5f, Slant, 0, offset));
                break;
            case "shdw4" or "shdw12":
                Draw(Place(bottom, 1, 0.5f, -Slant, 0, offset));
                break;
            case "shdw7" or "shdw15":
                Draw(Place(bottom, 1, -0.5f, -Slant, 0, offset));
                break;
            case "shdw8" or "shdw16":
                Draw(Place(bottom, 1, -0.5f, Slant, 0, offset));
                break;
            case "shdw9":
                // Straight behind the shape.
                Draw(Matrix3x2.Identity);
                break;
            case "shdw10":
                Draw(Place(size / 2, 1.25f, 1.25f, 0, 0, new Vector2(0, offset.Length() * 0.2f)));
                break;
            case "shdw13":
                Draw(Moved(offset * 2), 0.6);
                Draw(Moved(offset));
                break;
            case "shdw17" or "shdw18":
                Draw(Moved(-offset), 0.6);
                Draw(Moved(offset));
                break;
            case "shdw19":
                Draw(Place(bottom, 1, 0.5f, 0, 0, offset));
                break;
            case "shdw20":
                Draw(Place(top, 1, 0.84f, 0, 0, offset + new Vector2(0, size.Y)));
                break;
            default:
                Draw(Moved(offset));
                break;
        }
    }

    /// <summary>
    /// A reflection: the shape mirrored (by a negative <c>sy</c>, as Office writes it) about its alignment point, moved by
    /// <c>dist</c> and <c>dir</c>, faded along <c>fadeDir</c> from <c>stA</c> at <c>stPos</c> to <c>endA</c> at <c>endPos</c>,
    /// and blurred by <c>blurRad</c>.
    /// </summary>
    private static void Reflection(Canvas canvas, XElement reflection, Target target, float scale, Action<Canvas> body)
    {
        Vector2 anchor = Anchor((string?)reflection.Attribute("algn"), target.Size);
        Matrix3x2 place = Place(anchor, (float)Fraction(reflection, "sx", 1), (float)Fraction(reflection, "sy", 1), Degrees(reflection, "kx"), Degrees(reflection, "ky"), Offset(reflection, target));

        // The fade runs across the reflection as drawn, from its edge nearest the shape.
        Vector2[] corners = [Vector2.Transform(Vector2.Zero, place), Vector2.Transform(new Vector2(target.Size.X, 0), place), Vector2.Transform(target.Size, place), Vector2.Transform(new Vector2(0, target.Size.Y), place)];
        float left = corners.Min(c => c.X);
        float right = corners.Max(c => c.X);
        float topEdge = corners.Min(c => c.Y);
        float bottomEdge = corners.Max(c => c.Y);
        double fade = (DrawingNs.Double((string?)reflection.Attribute("fadeDir")) ?? 5400000) / 60000;
        Vector2 direction = Vector2.TransformNormal(new Vector2((float)Math.Cos(fade * Math.PI / 180), (float)Math.Sin(fade * Math.PI / 180)), target.PageToLocal);
        (Vector2 start, Vector2 end) = DrawingFills.LinearEnds(left, topEdge, right - left, bottomEdge - topEdge, Math.Atan2(direction.Y, direction.X) * 180 / Math.PI);
        double stPos = Math.Clamp(Fraction(reflection, "stPos", 0), 0, 1);
        double endPos = Math.Clamp(Fraction(reflection, "endPos", 1), 0, 1);
        double stA = Math.Clamp(Fraction(reflection, "stA", 1), 0, 1);
        double endA = Math.Clamp(Fraction(reflection, "endA", 0), 0, 1);
        if (endPos < stPos)
        {
            (stPos, endPos) = (endPos, stPos);
            (stA, endA) = (endA, stA);
        }

        GradientStop[] stops =
        [
            new GradientStop((float)stPos, new RgbaF32(1, 1, 1, (float)stA)),
            new GradientStop((float)Math.Max(endPos, stPos + 1e-4), new RgbaF32(1, 1, 1, (float)endA)),
        ];
        LinearGradient mask = new(start, end, stops);
        RectF area = RectF.FromEdges(left, topEdge, right, bottomEdge);
        float sigma = Math.Min(MaskBlur.MaxSigma, Points(reflection, "blurRad") / 2 * scale);
        int saved = canvas.SaveLayer(new LayerOptions
        {
            Mask = new SoftMask(c => c.FillRect(area, mask), SoftMaskKind.Alpha),
            Filter = sigma > 0.01f ? ops => ops.Blur(sigma) : null,
        });
        try
        {
            canvas.Concat(place);
            body(canvas);
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>A glow: the shape as drawn, in the glow's colour, grown and blurred by the glow's radius.</summary>
    private static void Glow(Canvas canvas, XElement glow, Rgba8 color, float scale, Action<Canvas> body)
    {
        float radius = Points(glow, "rad");
        if (radius <= 0)
        {
            return;
        }

        int grow = Math.Max(1, (int)Math.Round(radius / 2 * scale));
        float sigma = Math.Min(MaskBlur.MaxSigma, radius / 3 * scale);
        int saved = canvas.SaveLayer(new LayerOptions
        {
            Filter = ops =>
            {
                ops.BlendColor(color with { A = 255 }, BlendMode.SourceIn);
                ops.Morphology(MorphologyOp.Dilate, StructuringElement.Disk(grow));
                ops.Blur(sigma);
                if (color.A < 255)
                {
                    ops.Opacity(color.A / 255f);
                }
            },
        });
        body(canvas);
        canvas.RestoreToCount(saved);
    }

    /// <summary>A soft edge: the shape fades in over the radius from its outline, clear at the outline and solid the radius inside.</summary>
    private static SoftMask SoftEdgeMask(Path outline, float radius, float scale)
    {
        Path inset = outline.Op(new Stroke(radius) { Join = LineJoin.Round }.ToPath(outline), PathOp.Difference);
        float sigma = Math.Min(MaskBlur.MaxSigma, radius / 4 * scale);
        return new SoftMask(
            c =>
            {
                c.ClipPath(outline);
                c.FillPath(inset, new SolidPaint(new Rgba8(255, 255, 255, 255)) { Blur = sigma > 0.01f ? new MaskBlur(sigma, BlurStyle.Normal) : null });
            },
            SoftMaskKind.Alpha);
    }

    /// <summary>An inner shadow: what lies outside the shape, moved and blurred, seen inside it.</summary>
    private static void InnerShadow(Canvas canvas, XElement shadow, Rgba8 color, Path area, Target target, float scale)
    {
        float blurRadius = Points(shadow, "blurRad");
        Vector2 offset = Offset(shadow, target);
        RectF bounds = area.Bounds;
        float margin = (blurRadius * 2) + offset.Length() + 4;
        Path frame = Path.Rect(RectF.FromEdges(bounds.Left - margin, bounds.Top - margin, bounds.Right + margin, bounds.Bottom + margin));
        Path outside = frame.Op(area.Transform(Matrix3x2.CreateTranslation(offset)), PathOp.Difference);
        float sigma = Math.Min(MaskBlur.MaxSigma, blurRadius / 2 * scale);
        int saved = canvas.Save();
        try
        {
            canvas.ClipPath(area);
            canvas.FillPath(outside, new SolidPaint(color) { Blur = sigma > 0.01f ? new MaskBlur(sigma, BlurStyle.Normal) : null });
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }
}
