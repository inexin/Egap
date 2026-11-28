using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egap.Documents.DrawingML;

/// <summary>A shape's outline (<c>a:ln</c>, ECMA-376 Part 1, 20.1.2.2.24): its paint, width, caps, joins, dashes and compound lines.</summary>
internal static partial class ShapePainter
{
    private static void StrokePath(Canvas canvas, Path path, XElement line, Theme theme, Rgba8? placeholder, ShapePaintContext context)
    {
        float width = (float)((DrawingNs.Double((string?)line.Attribute("w")) ?? 9525) / DrawingNs.EmuPerPoint);
        width = Math.Max(width, 0.25f);
        if (line.Elements().FirstOrDefault(DrawingFills.IsFill) is not { } fill)
        {
            return;
        }

        // A line's gradient runs over the area the line covers, its pattern lies on the page as a fill's does.
        RectF bounds = path.Bounds;
        RectF covered = new(bounds.X - (width / 2), bounds.Y - (width / 2), bounds.Width + width, bounds.Height + width);
        Paint? paint = fill.Name.LocalName == "pattFill" ? PatternPaint(canvas, fill, covered, theme, placeholder, context) : PaintOf(fill, covered, theme, placeholder);
        if (paint is null)
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

    /// <summary>
    /// The dash and gap lengths of a line's <c>prstDash</c> or <c>custDash</c>, or null for a solid line. With round or square
    /// caps each dash is drawn a line's width shorter and each gap that much longer, so the caps bring the dash back to its own
    /// length, as Office draws them.
    /// </summary>
    internal static float[]? Dash(XElement line, float width)
    {
        float[]? lengths;
        if (line.Element(A + "custDash") is { } custom)
        {
            List<float> list = [];
            foreach (XElement ds in custom.Elements(A + "ds"))
            {
                // Dash and space lengths are thousandths of a percent of the line's width.
                list.Add((float)Math.Max(0.01, (DrawingNs.Double((string?)ds.Attribute("d")) ?? 0) / 100000 * width));
                list.Add((float)Math.Max(0.01, (DrawingNs.Double((string?)ds.Attribute("sp")) ?? 0) / 100000 * width));
            }

            lengths = list.Count == 0 ? null : [.. list];
        }
        else
        {
            lengths = DrawingFills.Dash((string?)line.Element(A + "prstDash")?.Attribute("val"), width);
        }

        if (lengths is not null && (string?)line.Attribute("cap") is "rnd" or "sq")
        {
            for (int i = 0; i + 1 < lengths.Length; i += 2)
            {
                lengths[i] = Math.Max(0.01f, lengths[i] - width);
                lengths[i + 1] += width;
            }
        }

        return lengths;
    }

    /// <summary>
    /// A compound line's area. Measured across the line from its left (as the path runs) to its right: a double line is a third
    /// line, a third gap and a third line; a triple line lines of a sixth, a third and a sixth with gaps of a sixth; thick-thin a
    /// line of three fifths, a gap of a fifth and a line of a fifth, thin-thick the same from the right.
    /// </summary>
    private static Path Compound(Path path, Stroke stroke, string kind)
    {
        float w = stroke.Width;
        Path Band(float width) => (stroke with { Width = width, Dash = null }).ToPath(path);
        Path whole = stroke.ToPath(path);

        switch (kind)
        {
            case "dbl":
                return whole.Op(Band(w / 3), PathOp.Difference);
            case "tri":
                return whole.Op(Band(w * 2 / 3).Op(Band(w / 3), PathOp.Difference), PathOp.Difference);
            case "thickThin" or "thinThick":
                {
                    // The gap runs from a tenth to three tenths of the width on one side of the path's middle.
                    bool right = kind == "thickThin";
                    Path ring = Band(w * 0.6f).Op(Band(w * 0.2f), PathOp.Difference);
                    Path gap = ring.Op(Side(path, right, w), PathOp.Intersect);
                    return whole.Op(gap, PathOp.Difference);
                }

            default:
                return whole;
        }
    }

    /// <summary>
    /// The area on one side of a path, as it runs, out to <paramref name="reach"/>: inside or outside a closed path by its turning
    /// direction, a strip along an open one.
    /// </summary>
    private static Path Side(Path path, bool right, float reach)
    {
        PathMeasure measure = new(path);
        PathBuilder builder = new();
        foreach (ContourMeasure contour in measure.Contours)
        {
            if (contour.Length <= 0)
            {
                continue;
            }

            int steps = Math.Clamp((int)MathF.Ceiling(contour.Length / Math.Max(reach / 2, 0.5f)), 2, 4096);
            List<Vector2> near = [];
            List<Vector2> far = [];
            for (int i = 0; i <= steps; i++)
            {
                float distance = contour.Length * i / steps;
                Vector2 point = contour.GetPosition(distance);
                Vector2 tangent = contour.GetTangent(distance);

                // On a y-down page the left of a path running along +x is up, at (y, -x) of its direction.
                Vector2 normal = right ? new Vector2(-tangent.Y, tangent.X) : new Vector2(tangent.Y, -tangent.X);
                near.Add(point);
                far.Add(point + (normal * reach));
            }

            if (contour.IsClosed)
            {
                // A closed contour's side is everything inside or outside it, strip or not: its turning decides which.
                double area = 0;
                for (int i = 0; i < near.Count - 1; i++)
                {
                    area += (near[i].X * near[i + 1].Y) - (near[i + 1].X * near[i].Y);
                }

                // A positive area turns clockwise on the page, keeping the inside on the right.
                bool inside = (area > 0) == right;
                RectF bounds = path.Bounds;
                float margin = reach * 2;
                Path contourPath = Path.Polygon([.. near]);
                Path side = inside
                    ? contourPath
                    : Path.Rect(RectF.FromEdges(bounds.Left - margin, bounds.Top - margin, bounds.Right + margin, bounds.Bottom + margin)).Op(contourPath, PathOp.Difference);
                builder.AddPath(side);
                continue;
            }

            far.Reverse();
            builder.AddPath(Path.Polygon([.. near, .. far]));
        }

        return builder.ToPath();
    }
}
