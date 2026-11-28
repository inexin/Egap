using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.DrawingML;

/// <summary>
/// DrawingML's gradient fills (<c>a:gradFill</c>, ECMA-376 Part 1, 20.1.8.33): linear ones at their angle, scaled with the box
/// when <c>scaled</c> says so; path ones from the focus (<c>fillToRect</c>) out to the box's edge as a circle, a rectangle or the
/// shape's own outline; and the tile rectangle (<c>tileRect</c>) a gradient spans, mirrored past it as <c>flip</c> asks.
/// </summary>
internal static class DrawingGradients
{
    private static readonly XNamespace A = DrawingNs.A;

    /// <summary>The gradient's stops in order, their colours resolved.</summary>
    public static List<GradientStop> Stops(XElement fill, Theme theme, Rgba8? placeholder)
    {
        List<GradientStop> stops = [];
        foreach ((double position, XElement stop) in DrawingFills.Stops(fill).OrderBy(s => s.Position))
        {
            if (DrawingColors.Resolve(stop, theme, placeholder) is { } color)
            {
                stops.Add(new GradientStop((float)Math.Clamp(position, 0, 1), color));
            }
        }

        return stops;
    }

    /// <summary>The rectangle the gradient spans: the box, or the box moved out (or in) by the tile rectangle's fractions.</summary>
    private static RectF Span(XElement fill, RectF bounds)
    {
        if (fill.Element(A + "tileRect") is not { } tile)
        {
            return bounds;
        }

        double l = Fraction(tile, "l"), t = Fraction(tile, "t"), r = Fraction(tile, "r"), b = Fraction(tile, "b");
        return RectF.FromEdges(
            (float)(bounds.Left + (l * bounds.Width)),
            (float)(bounds.Top + (t * bounds.Height)),
            (float)(bounds.Right - (r * bounds.Width)),
            (float)(bounds.Bottom - (b * bounds.Height)));
    }

    private static double Fraction(XElement? element, string name) => (DrawingNs.Double((string?)element?.Attribute(name)) ?? 0) / 100000;

    private static TileMode Tile(XElement fill) => (string?)fill.Attribute("flip") is "x" or "y" or "xy" ? TileMode.Mirror : TileMode.Clamp;

    /// <summary>
    /// A linear gradient over <paramref name="bounds"/>. Its angle is the page's; with <c>scaled</c> it is the angle in a square
    /// stretched to the box, so a 45-degree gradient runs corner to corner.
    /// </summary>
    public static Paint Linear(XElement fill, RectF bounds, GradientStop[] stops)
    {
        RectF span = Span(fill, bounds);
        double degrees = DrawingFills.Angle(fill) ?? 0;
        if ((string?)fill.Element(A + "lin")?.Attribute("scaled") is "1" or "true" && span.Width > 0 && span.Height > 0)
        {
            double radians = degrees * Math.PI / 180;
            degrees = Math.Atan2(Math.Sin(radians) * span.Width, Math.Cos(radians) * span.Height) * 180 / Math.PI;
        }

        (Vector2 start, Vector2 end) = DrawingFills.LinearEnds(span.X, span.Y, span.Width, span.Height, degrees);
        return new LinearGradient(start, end, stops) { Tile = Tile(fill) };
    }

    /// <summary>
    /// Fills a path with a rectangle or shape path gradient, in bands from the box's edge (or the shape's outline) in to the
    /// focus; false for any other gradient, which <see cref="ShapePainter.PaintOf"/> paints.
    /// </summary>
    public static bool FillPath(Canvas canvas, Path path, XElement fill, Theme theme, Rgba8? placeholder, Bevel? bevel)
    {
        if (fill.Element(A + "path") is not { } shade || (string?)shade.Attribute("path") is not ("rect" or "shape"))
        {
            return false;
        }

        List<GradientStop> stops = Stops(fill, theme, placeholder);
        if (stops.Count < 2)
        {
            return false;
        }

        RectF bounds = Span(fill, path.Bounds);
        XElement? to = shade.Element(A + "fillToRect");
        double side = to is null ? 0.5 : 0;
        double l = to?.Attribute("l") is null ? side : Fraction(to, "l");
        double t = to?.Attribute("t") is null ? side : Fraction(to, "t");
        double r = to?.Attribute("r") is null ? side : Fraction(to, "r");
        double b = to?.Attribute("b") is null ? side : Fraction(to, "b");
        RectF focus = RectF.FromEdges(
            bounds.Left + (float)(l * bounds.Width),
            bounds.Top + (float)(t * bounds.Height),
            bounds.Right - (float)(r * bounds.Width),
            bounds.Bottom - (float)(b * bounds.Height));
        if (focus.Width < 0 || focus.Height < 0)
        {
            // Crossed sides meet at their middle.
            Vector2 middle = new(focus.X + (focus.Width / 2), focus.Y + (focus.Height / 2));
            focus = new RectF(middle.X, middle.Y, 0, 0);
        }

        bool rect = (string?)shade.Attribute("path") == "rect";
        float scale = DrawingEffects.DeviceScale(canvas);
        float reach = Math.Max(Math.Max(focus.Left - bounds.Left, bounds.Right - focus.Right), Math.Max(focus.Top - bounds.Top, bounds.Bottom - focus.Bottom));
        int bands = Math.Clamp((int)MathF.Ceiling(reach * scale / 1.5f), 1, 256);

        // The band at f reaches a fraction f of the way from the focus out to the box: for a rectangle path the box itself
        // shrunk toward the focus rectangle, for a shape path the shape's outline mapped the same way.
        RectF Band(float f) => RectF.FromEdges(
            focus.Left + ((bounds.Left - focus.Left) * f),
            focus.Top + ((bounds.Top - focus.Top) * f),
            focus.Right + ((bounds.Right - focus.Right) * f),
            focus.Bottom + ((bounds.Bottom - focus.Bottom) * f));
        Path Shape(float f)
        {
            RectF band = Band(f);
            float sx = bounds.Width > 0 ? band.Width / bounds.Width : 0;
            float sy = bounds.Height > 0 ? band.Height / bounds.Height : 0;
            return path.Transform(Matrix3x2.CreateTranslation(-bounds.X, -bounds.Y) * Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(band.X, band.Y));
        }

        int saved = canvas.Save();
        try
        {
            canvas.ClipPath(path);

            // Outer bands first, each in the stops' colour at its middle, the inner ones over them; the focus itself is the
            // first stop's colour.
            for (int i = bands; i >= 0; i--)
            {
                float f = (float)i / bands;
                SolidPaint paint = new(At(stops, i == 0 ? 0 : f - (0.5f / bands)));
                if (rect)
                {
                    RectF band = Band(f);
                    if (i == bands)
                    {
                        band = RectF.FromEdges(band.Left - 1, band.Top - 1, band.Right + 1, band.Bottom + 1);
                    }

                    canvas.FillRect(band, paint);
                }
                else
                {
                    canvas.FillPath(i == bands ? path : Shape(f), paint);
                }
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }

        if (bevel is not null)
        {
            canvas.DrawBevel(path, bevel);
        }

        return true;
    }

    /// <summary>The stops' colour at <paramref name="t"/>, mixed as the gradient paints mix them.</summary>
    private static RgbaF32 At(List<GradientStop> stops, float t)
    {
        if (t <= stops[0].Offset)
        {
            return stops[0].Color;
        }

        for (int i = 1; i < stops.Count; i++)
        {
            if (t <= stops[i].Offset)
            {
                GradientStop a = stops[i - 1];
                GradientStop b = stops[i];
                float span = b.Offset - a.Offset;
                float k = span <= 0 ? 1 : (t - a.Offset) / span;
                return new RgbaF32(
                    a.Color.R + ((b.Color.R - a.Color.R) * k),
                    a.Color.G + ((b.Color.G - a.Color.G) * k),
                    a.Color.B + ((b.Color.B - a.Color.B) * k),
                    a.Color.A + ((b.Color.A - a.Color.A) * k));
            }
        }

        return stops[^1].Color;
    }
}
