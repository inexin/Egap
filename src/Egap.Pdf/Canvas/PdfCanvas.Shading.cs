using System.Buffers.Binary;
using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using DrawingPath = Egami.Drawing.Path;

namespace Egap.Pdf;

/// <summary>
/// Gradients and image paints on the PDF canvas (Phase 3 design Q14). A gradient is drawn as its shading painted with
/// <c>sh</c> inside the shape as a clip: linear, radial and two-point conical gradients as axial and radial shadings, sweep
/// gradients as a mesh of Coons patches, one per arc of at most 15 degrees, and repeat and mirror tiling as one shading per
/// period, each clipped to its strip or ring. Stops with alpha add a luminosity soft mask of the same shadings in gray. Image
/// paints become tiling patterns. Strokes with these paints are filled as their outlines.
/// </summary>
/// <remarks>
/// The shapes were chosen by rendering in PDFium and pdf.js: PDFium samples a shading's function at 256 points over its whole
/// domain, so a shading across many periods bands; pdf.js draws a function-based (type 1) shading on a coarse grid; and pdf.js
/// places a pattern used for stroking wrongly under a scaled transform.
/// </remarks>
public sealed partial class PdfCanvas
{
    /// <summary>The most periods a repeating gradient may cross in one drawing before it falls back to an image.</summary>
    private const int MaxPeriods = 256;

    /// <summary>How many points a gradient is sampled at when its colours cannot be written exactly.</summary>
    private const int Samples = 256;

    /// <summary>The widest arc one patch of a sweep gradient covers, in degrees.</summary>
    private const double MaxSweepArc = 15;

    /// <summary>Whether the drawing call in progress opened its own <c>q</c> that <see cref="Finish"/> must close.</summary>
    private bool restoreAfterDraw;

    /// <summary>For a gradient fill: the shadings <see cref="Finish"/> paints inside the path, once it is the clip.</summary>
    private List<ShadingPiece>? pendingShadings;

    /// <summary>
    /// Paints other than a solid colour: a gradient's shadings, painted by <see cref="Finish"/>, or an image pattern as the
    /// fill colour, in a <c>q</c> of the call's own; <see langword="false"/> to fall back.
    /// </summary>
    private bool BeginShaded(Paint paint, Stroke? stroke)
    {
        GraphicsGroup g = OpenGroup();
        List<ShadingPiece>? pieces = null;
        PdfReference? pattern = null, mask = null;
        switch (paint)
        {
            case Gradient gradient when stroke is null:
                if (GradientShadings(gradient) is not { } shadings)
                {
                    return false;
                }

                (pieces, mask) = shadings;
                break;
            case ImagePaint image when stroke is null:
                pattern = ImagePattern(image, Pdf(paint.LocalMatrix * Matrix) * baseMatrix);
                if (pattern is null)
                {
                    return false;
                }

                break;
            case PicturePaint picture when stroke is null:
                pattern = PicturePattern(picture, Pdf(paint.LocalMatrix * Matrix) * baseMatrix);
                if (pattern is null)
                {
                    return false;
                }

                break;
            default:
                return false;
        }

        content.Save();
        restoreAfterDraw = true;
        double alpha = Math.Round(paint.Opacity, 4);
        if (mask is PdfReference group)
        {
            string mode = PdfBlendModes.TryGetValue(paint.BlendMode, out string? name) ? name : "Normal";
            PdfReference state = Page.Document.Objects.Add(new PdfDictionary
            {
                ["Type"] = PdfName.Get("ExtGState"),
                ["CA"] = new PdfReal(alpha),
                ["ca"] = new PdfReal(alpha),
                ["BM"] = PdfName.Get(mode),
                ["SMask"] = new PdfDictionary { ["Type"] = PdfName.Get("Mask"), ["S"] = PdfName.Get("Luminosity"), ["G"] = group },
            });
            content.GraphicsState(AddResource("ExtGState", state));
        }
        else
        {
            SetAlpha(new GraphicsGroup(g.Matrix) { Alpha = g.Alpha, Blend = g.Blend }, alpha, paint.BlendMode);
        }

        if (pattern is not null)
        {
            content.ColorSpace("Pattern").Color([], AddResource("Pattern", pattern));
        }

        pendingShadings = pieces;
        return true;
    }

    /// <summary>Whether a stroke with this paint is filled as its outline instead (gradients and image paints, not hairlines).</summary>
    private static bool FillsStroke(Paint paint, Stroke? stroke)
    {
        return stroke is { IsHairline: false } && paint is Gradient or ImagePaint or PicturePaint;
    }

    /// <summary>Paints the pending shadings inside the path just written, which becomes the clip.</summary>
    private void PaintShadings(FillRule rule)
    {
        content.Clip(rule == FillRule.EvenOdd).EndPath();
        WriteShadings(content, pendingShadings!, name => AddResource("Shading", name));
        pendingShadings = null;
    }

    /// <summary>Writes shadings under the paint's matrix, each in its own clip when it has one.</summary>
    private static void WriteShadings(PdfContentWriter writer, List<ShadingPiece> pieces, Func<PdfReference, PdfName> name)
    {
        foreach (ShadingPiece piece in pieces)
        {
            writer.Save().Transform(piece.Matrix);
            if (piece.Clip is DrawingPath clip)
            {
                WritePath(writer, clip);
                writer.Clip(clip.FillRule == FillRule.EvenOdd).EndPath();
            }

            writer.Shading(name(piece.Shading)).Restore();
        }
    }

    /// <summary>
    /// The shadings that draw a gradient over the clip, in the canvas's local coordinates, and the soft mask group for its
    /// alpha when it has any; <see langword="null"/> when PDF cannot draw it.
    /// </summary>
    private (List<ShadingPiece> Pieces, PdfReference? Mask)? GradientShadings(Gradient gradient)
    {
        if (!Matrix3x2.Invert(gradient.LocalMatrix * Matrix, out Matrix3x2 toPaint) || !Matrix3x2.Invert(Matrix, out Matrix3x2 toLocal))
        {
            return null;
        }

        RectF area = Map(DeviceClipBounds, toPaint);
        Ramp ramp = Ramp.For(gradient);
        PdfDocument document = Page.Document;
        PdfMatrix local = Pdf(gradient.LocalMatrix);
        List<ShadingPiece>? colors = Pieces(document, gradient, ramp, area, local, alpha: false);
        if (colors is null)
        {
            return null;
        }

        // A gradient that does not extend leaves its outside unpainted by itself: Extend says so, and a sweep has no patch there.
        if (!ramp.HasAlpha)
        {
            return (colors, null);
        }

        List<ShadingPiece> gray = Pieces(document, gradient, ramp, area, local, alpha: true)!;
        RectF bounds = Map(DeviceClipBounds, toLocal);
        PdfStream mask = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { Real(bounds.Left), Real(bounds.Top), Real(bounds.Right), Real(bounds.Bottom) },
            ["Group"] = new PdfDictionary { ["S"] = PdfName.Get("Transparency"), ["CS"] = PdfName.Get("DeviceGray") },
        });
        PdfDictionary shadings = [];
        PdfContentWriter writer = new();
        WriteShadings(writer, gray, reference =>
        {
            PdfName name = PdfName.Get("Sh" + shadings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            shadings[name] = reference;
            return name;
        });
        mask.Dictionary["Resources"] = new PdfDictionary { ["Shading"] = shadings };
        mask.SetDecodedBytes(writer.ToArray());
        return (colors, document.Objects.Add(mask));
    }

    /// <summary>The shadings for a gradient's colours, or with <paramref name="alpha"/> its alpha in gray, each with its clip in paint coordinates.</summary>
    private static List<ShadingPiece>? Pieces(PdfDocument document, Gradient gradient, Ramp ramp, RectF area, PdfMatrix local, bool alpha)
    {
        string space = alpha ? "DeviceGray" : "DeviceRGB";
        int channels = alpha ? 1 : 3;
        if (gradient is SweepGradient sweep)
        {
            return [new ShadingPiece(document.Objects.Add(SweepMesh(sweep, ramp, area, alpha)), local, null)];
        }

        PdfObject function = ramp.Function(document, alpha, channels);
        if (gradient.Tile is TileMode.Clamp or TileMode.Decal)
        {
            bool extendStart = gradient.Tile == TileMode.Clamp && gradient.ExtendStart, extendEnd = gradient.Tile == TileMode.Clamp && gradient.ExtendEnd;
            PdfArray coords = gradient switch
            {
                LinearGradient l => [Real(l.Start.X), Real(l.Start.Y), Real(l.End.X), Real(l.End.Y)],
                RadialGradient r => [Real(r.Center.X), Real(r.Center.Y), Real(0), Real(r.Center.X), Real(r.Center.Y), Real(r.Radius)],
                ConicalGradient c => [Real(c.Start.X), Real(c.Start.Y), Real(c.StartRadius), Real(c.End.X), Real(c.End.Y), Real(c.EndRadius)],
                _ => [],
            };
            return [new ShadingPiece(document.Objects.Add(Axial(gradient is LinearGradient ? 2 : 3, space, coords, function, extendStart, extendEnd)), local, null)];
        }

        if (Periods(gradient, area) is not (int k0, int k1))
        {
            return null;
        }

        List<ShadingPiece> pieces = [];
        float reach = Math.Abs(area.Left) + Math.Abs(area.Right) + Math.Abs(area.Top) + Math.Abs(area.Bottom) + 1;
        for (int k = k0; k < k1; k++)
        {
            bool reversed = gradient.Tile == TileMode.Mirror && ((k % 2) + 2) % 2 == 1;
            PdfArray coords;
            DrawingPath clip;
            switch (gradient)
            {
                case LinearGradient l:
                    Vector2 from = l.Start + ((l.End - l.Start) * k), to = l.Start + ((l.End - l.Start) * (k + 1));
                    Vector2 across = Vector2.Normalize(new Vector2(-(l.End - l.Start).Y, (l.End - l.Start).X)) * reach;
                    clip = DrawingPath.Polygon([from - across, to - across, to + across, from + across]);
                    (Vector2 a, Vector2 b) = reversed ? (to, from) : (from, to);
                    coords = [Real(a.X), Real(a.Y), Real(b.X), Real(b.Y)];
                    break;
                case RadialGradient r:
                    float inner = r.Radius * k, outer = r.Radius * (k + 1);
                    clip = (k == 0 ? DrawingPath.Circle(r.Center, outer) : DrawingPath.Circle(r.Center, outer).Op(DrawingPath.Circle(r.Center, inner), PathOp.Difference));
                    (float ra, float rb) = reversed ? (outer, inner) : (inner, outer);
                    coords = [Real(r.Center.X), Real(r.Center.Y), Real(ra), Real(r.Center.X), Real(r.Center.Y), Real(rb)];
                    break;
                default:
                    return null;
            }

            pieces.Add(new ShadingPiece(document.Objects.Add(Axial(gradient is LinearGradient ? 2 : 3, space, coords, function, true, true)), local, clip));
        }

        return pieces;
    }

    private static PdfDictionary Axial(int type, string space, PdfArray coords, PdfObject function, bool extendStart, bool extendEnd)
    {
        return new PdfDictionary
        {
            ["ShadingType"] = new PdfInteger(type),
            ["ColorSpace"] = PdfName.Get(space),
            ["Coords"] = coords,
            ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
            ["Function"] = function is PdfDictionary dictionary ? dictionary.Clone() : function,
            ["Extend"] = new PdfArray { PdfBoolean.Get(extendStart), PdfBoolean.Get(extendEnd) },
        };
    }

    /// <summary>
    /// The whole periods of a repeating linear or radial gradient that the area can reach, from the gradient parameter at the
    /// area's corners; <see langword="null"/> when there are too many to write, or for other gradients.
    /// </summary>
    private static (int, int)? Periods(Gradient gradient, RectF area)
    {
        Vector2[] corners = [new(area.Left, area.Top), new(area.Right, area.Top), new(area.Left, area.Bottom), new(area.Right, area.Bottom)];
        double min, max;
        switch (gradient)
        {
            case LinearGradient l:
                Vector2 axis = l.End - l.Start;
                float length = axis.LengthSquared();
                if (length == 0)
                {
                    return null;
                }

                double[] ts = [.. corners.Select(c => (double)Vector2.Dot(c - l.Start, axis) / length)];
                (min, max) = (ts.Min(), ts.Max());
                break;
            case RadialGradient r when r.Radius > 0:
                min = 0;
                max = corners.Max(c => Vector2.Distance(c, r.Center)) / r.Radius;
                break;
            default:
                return null;
        }

        double k0 = Math.Floor(min), k1 = Math.Ceiling(max);
        return !double.IsFinite(k0) || !double.IsFinite(k1) || k1 - k0 > MaxPeriods ? null : ((int)k0, (int)Math.Max(k1, k0 + 1));
    }

    /// <summary>
    /// A sweep gradient as a Coons patch mesh (type 6): one patch per arc of at most 15 degrees between the centre and a
    /// circle past the area, its colours changing only around the circle, so they run straight between the arc's ends.
    /// Arcs the gradient does not reach (decal, or a clamp that does not extend) get no patch.
    /// </summary>
    private static PdfStream SweepMesh(SweepGradient sweep, Ramp ramp, RectF area, bool alpha)
    {
        Vector2 center = sweep.Center;
        float radius = 1 + new[] { new Vector2(area.Left, area.Top), new Vector2(area.Right, area.Top), new Vector2(area.Left, area.Bottom), new Vector2(area.Right, area.Bottom) }
            .Max(c => Vector2.Distance(c, center));
        double start = sweep.StartAngle, span = sweep.EndAngle - sweep.StartAngle;

        // Arc ends: every 15 degrees, and wherever the gradient's parameter meets a knot or a period's end.
        SortedSet<double> angles = [0, 360];
        for (double a = MaxSweepArc; a < 360; a += MaxSweepArc)
        {
            angles.Add(a);
        }

        double tLow = (0 - start) / span, tHigh = (360 - start) / span;
        for (int period = (int)Math.Floor(tLow) - 1; period <= (int)Math.Ceiling(tHigh) && period - tLow < 4096; period++)
        {
            foreach (double knot in ramp.Offsets)
            {
                double angle = start + ((period + knot) * span);
                if (angle is > 0 and < 360)
                {
                    angles.Add(angle);
                }
            }
        }

        double[] ends = [.. angles];
        int channels = alpha ? 1 : 3;
        List<byte> data = [];
        double xMin = center.X - radius, xMax = center.X + radius, yMin = center.Y - radius, yMax = center.Y + radius;
        for (int i = 0; i + 1 < ends.Length; i++)
        {
            double a0 = ends[i], a1 = ends[i + 1];
            if (a1 - a0 < 1e-9)
            {
                continue;
            }

            double middle = ((a0 + a1) / 2 - start) / span;
            if (Tile(sweep, middle) is not double)
            {
                continue;
            }

            // The parameter at each end, taken from inside this arc so a seam between periods falls between patches.
            double t0 = Within(sweep, (a0 - start) / span, middle), t1 = Within(sweep, (a1 - start) / span, middle);
            RgbaF32 c0 = ramp.At(t0), c1 = ramp.At(t1);
            Vector2 p30 = Polar(center, radius, a0), p33 = Polar(center, radius, a1);
            double k = 4.0 / 3 * Math.Tan((a1 - a0) * Math.PI / 180 / 4);
            Vector2 p31 = p30 + ((float)(k * radius) * Tangent(a0)), p32 = p33 - ((float)(k * radius) * Tangent(a1));
            Vector2[] points =
            [
                center, center, center, center,
                center + ((p33 - center) / 3), center + ((p33 - center) * 2 / 3), p33,
                p32, p31, p30,
                center + ((p30 - center) * 2 / 3), center + ((p30 - center) / 3),
            ];
            data.Add(0);
            foreach (Vector2 p in points)
            {
                Coordinate(data, p.X, xMin, xMax);
                Coordinate(data, p.Y, yMin, yMax);
            }

            foreach (RgbaF32 c in (RgbaF32[])[c0, c1, c1, c0])
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    double value = alpha ? c.A : channel switch { 0 => c.R, 1 => c.G, _ => c.B };
                    ushort sample = (ushort)Math.Round(Math.Clamp(value, 0, 1) * ushort.MaxValue);
                    data.Add((byte)(sample >> 8));
                    data.Add((byte)sample);
                }
            }
        }

        PdfArray decode = [Real(xMin), Real(xMax), Real(yMin), Real(yMax)];
        for (int channel = 0; channel < channels; channel++)
        {
            decode.Add(new PdfInteger(0));
            decode.Add(new PdfInteger(1));
        }

        PdfStream mesh = new(new PdfDictionary
        {
            ["ShadingType"] = new PdfInteger(6),
            ["ColorSpace"] = PdfName.Get(alpha ? "DeviceGray" : "DeviceRGB"),
            ["BitsPerCoordinate"] = new PdfInteger(32),
            ["BitsPerComponent"] = new PdfInteger(16),
            ["BitsPerFlag"] = new PdfInteger(8),
            ["Decode"] = decode,
        });
        mesh.SetDecodedBytes([.. data]);
        return mesh;
    }

    /// <summary>A sweep's parameter tiled into [0, 1], or <see langword="null"/> where it draws nothing.</summary>
    private static double? Tile(SweepGradient sweep, double t)
    {
        switch (sweep.Tile)
        {
            case TileMode.Repeat:
                return t - Math.Floor(t);
            case TileMode.Mirror:
                double u = t - (2 * Math.Floor(t / 2));
                return u > 1 ? 2 - u : u;
            case TileMode.Decal:
                return t is >= 0 and <= 1 ? t : null;
            default:
                return (t < 0 && !sweep.ExtendStart) || (t > 1 && !sweep.ExtendEnd) ? null : Math.Clamp(t, 0, 1);
        }
    }

    /// <summary>The tiled parameter at an arc's end, in the same period as the arc's middle.</summary>
    private static double Within(SweepGradient sweep, double t, double middle)
    {
        double period = Math.Floor(middle);
        return sweep.Tile switch
        {
            TileMode.Repeat => Math.Clamp(t - period, 0, 1),
            TileMode.Mirror => ((((int)period % 2) + 2) % 2 == 1) ? Math.Clamp(1 - (t - period), 0, 1) : Math.Clamp(t - period, 0, 1),
            _ => Math.Clamp(t, 0, 1),
        };
    }

    private static Vector2 Polar(Vector2 center, float radius, double degrees)
    {
        (double sin, double cos) = Math.SinCos(degrees * Math.PI / 180);
        return center + new Vector2((float)(radius * cos), (float)(radius * sin));
    }

    private static Vector2 Tangent(double degrees)
    {
        (double sin, double cos) = Math.SinCos(degrees * Math.PI / 180);
        return new Vector2((float)-sin, (float)cos);
    }

    private static void Coordinate(List<byte> data, double value, double min, double max)
    {
        uint encoded = (uint)Math.Round(Math.Clamp((value - min) / (max - min), 0, 1) * uint.MaxValue);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, encoded);
        data.AddRange(bytes);
    }

    /// <summary>A tiling pattern for an image paint that repeats or mirrors on both axes, or decals; <see langword="null"/> for clamping.</summary>
    private PdfReference? ImagePattern(ImagePaint paint, PdfMatrix toPage)
    {
        if (paint.TileX == TileMode.Clamp || paint.TileY == TileMode.Clamp || !Matrix3x2.Invert(paint.LocalMatrix * Matrix, out Matrix3x2 toPaint))
        {
            return null;
        }

        Raster image = paint.Image;
        float w = image.Width, h = image.Height;
        RectF area = Map(DeviceClipBounds, toPaint);
        PdfReference xObject = ImageXObject(image);
        bool mirrorX = paint.TileX == TileMode.Mirror, mirrorY = paint.TileY == TileMode.Mirror;
        float stepX = mirrorX ? 2 * w : w, stepY = mirrorY ? 2 * h : h;

        // A decal axis does not repeat: a step past everything the drawing can reach leaves one copy.
        float reach = Math.Abs(area.Left) + Math.Abs(area.Right) + Math.Abs(area.Top) + Math.Abs(area.Bottom) + w + h;
        if (paint.TileX == TileMode.Decal)
        {
            stepX = Math.Max(reach * 2, stepX);
        }

        if (paint.TileY == TileMode.Decal)
        {
            stepY = Math.Max(reach * 2, stepY);
        }

        PdfContentWriter tile = new();
        for (int i = 0; i < (mirrorX ? 2 : 1); i++)
        {
            for (int j = 0; j < (mirrorY ? 2 : 1); j++)
            {
                // Each copy has its first row at the top of its cell; the second cell on a mirrored axis is flipped.
                tile.Save().Transform(i == 1 ? -w : w, 0, 0, j == 1 ? h : -h, i == 1 ? 2 * w : 0, h).Draw("Im0").Restore();
            }
        }

        PdfStream pattern = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Pattern"),
            ["PatternType"] = new PdfInteger(1),
            ["PaintType"] = new PdfInteger(1),
            ["TilingType"] = new PdfInteger(1),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), Real(mirrorX ? 2 * w : w), Real(mirrorY ? 2 * h : h) },
            ["XStep"] = Real(stepX),
            ["YStep"] = Real(stepY),
            ["Matrix"] = MatrixArray(toPage),
            ["Resources"] = new PdfDictionary { ["XObject"] = new PdfDictionary { ["Im0"] = xObject } },
        });
        pattern.SetDecodedBytes(tile.ToArray());
        return Page.Document.Objects.Add(pattern);
    }

    private static PdfArray MatrixArray(PdfMatrix m)
    {
        return [Real(m.A), Real(m.B), Real(m.C), Real(m.D), Real(m.E), Real(m.F)];
    }

    private static PdfMatrix Pdf(Matrix3x2 m)
    {
        return new PdfMatrix(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);
    }

    private static RectF Map(RectF rect, Matrix3x2 matrix)
    {
        return RectF.FromPoints(
        [
            Vector2.Transform(new Vector2(rect.Left, rect.Top), matrix),
            Vector2.Transform(new Vector2(rect.Right, rect.Top), matrix),
            Vector2.Transform(new Vector2(rect.Left, rect.Bottom), matrix),
            Vector2.Transform(new Vector2(rect.Right, rect.Bottom), matrix),
        ]);
    }

    private static PdfReal Real(double value)
    {
        return new PdfReal(Math.Round(value, 5));
    }

    /// <summary>One shading painted for a gradient: under the paint's matrix, within its clip in paint coordinates when it has one.</summary>
    private sealed record ShadingPiece(PdfReference Shading, PdfMatrix Matrix, DrawingPath? Clip);

    /// <summary>
    /// A gradient's colours along its parameter as knots joined by straight lines: the stops themselves when the gradient
    /// mixes stored sRGB values straight (or its alpha is the same everywhere), else 256 samples of Egami's own rendering.
    /// </summary>
    private sealed class Ramp
    {
        private Ramp(double[] offsets, RgbaF32[] colors, bool sampled)
        {
            Offsets = offsets;
            Colors = colors;
            Sampled = sampled;
            HasAlpha = colors.Any(c => c.A < 1);
        }

        public double[] Offsets { get; }

        public RgbaF32[] Colors { get; }

        public bool Sampled { get; }

        public bool HasAlpha { get; }

        public static Ramp For(Gradient gradient)
        {
            IReadOnlyList<GradientStop> stops = gradient.Stops;
            bool sameAlpha = stops.All(s => s.Color.A == stops[0].Color.A);
            if (gradient.Interpolation.Space == InterpolationSpace.Encoded && (sameAlpha || !gradient.Premultiplied))
            {
                // Offsets below the one before count as equal to it, as CSS does; the ends are held past the first and last stops.
                List<double> offsets = [];
                List<RgbaF32> colors = [];
                double last = double.NegativeInfinity;
                foreach (GradientStop stop in stops)
                {
                    last = Math.Clamp(Math.Max(last, stop.Offset), 0, 1);
                    offsets.Add(last);
                    colors.Add(Clamp(stop.Color));
                }

                if (offsets[0] > 0)
                {
                    offsets.Insert(0, 0);
                    colors.Insert(0, colors[0]);
                }

                if (offsets[^1] < 1)
                {
                    offsets.Add(1);
                    colors.Add(colors[^1]);
                }

                return new Ramp([.. offsets], [.. colors], sampled: false);
            }

            return Sample(gradient);
        }

        /// <summary>The colour at a parameter in [0, 1], straight between the knots around it.</summary>
        public RgbaF32 At(double t)
        {
            int i = Array.BinarySearch(Offsets, t);
            if (i >= 0)
            {
                // At a knot shared by two stops, the later one's colour, as a hard edge reads past it.
                while (i + 1 < Offsets.Length && Offsets[i + 1] == t)
                {
                    i++;
                }

                return Colors[i];
            }

            int high = ~i;
            if (high <= 0)
            {
                return Colors[0];
            }

            if (high >= Offsets.Length)
            {
                return Colors[^1];
            }

            double span = Offsets[high] - Offsets[high - 1];
            float f = span > 0 ? (float)((t - Offsets[high - 1]) / span) : 1;
            RgbaF32 a = Colors[high - 1], b = Colors[high];
            return new RgbaF32(a.R + ((b.R - a.R) * f), a.G + ((b.G - a.G) * f), a.B + ((b.B - a.B) * f), a.A + ((b.A - a.A) * f));
        }

        /// <summary>Samples the gradient by drawing it once along a row with Egami, at the centre of each of 256 pixels.</summary>
        private static Ramp Sample(Gradient gradient)
        {
            LinearGradient line = new(new Vector2(0, 0), new Vector2(Samples - 1, 0), [.. gradient.Stops])
            {
                Interpolation = gradient.Interpolation,
                Premultiplied = gradient.Premultiplied,
                BlendMode = Egami.Operations.BlendMode.Source,
            };
            using Raster<RgbaF32> row = Raster.Create<RgbaF32>(Samples, 1);
            using (RasterCanvas<RgbaF32> canvas = new(row))
            {
                canvas.Translate(0.5f, 0);
                canvas.FillRect(new RectF(-0.5f, 0, Samples, 1), line);
            }

            Span<RgbaF32> pixels = row.Row(0);
            double[] offsets = new double[Samples];
            RgbaF32[] colors = new RgbaF32[Samples];
            for (int i = 0; i < Samples; i++)
            {
                offsets[i] = i / (double)(Samples - 1);
                colors[i] = Clamp(pixels[i]);
            }

            return new Ramp(offsets, colors, sampled: true);
        }

        private static RgbaF32 Clamp(RgbaF32 c)
        {
            return new RgbaF32(Math.Clamp(c.R, 0, 1), Math.Clamp(c.G, 0, 1), Math.Clamp(c.B, 0, 1), Math.Clamp(c.A, 0, 1));
        }

        /// <summary>
        /// The PDF function over [0, 1] for the colours (or the alpha): a reference to a sampled (type 0) function for sampled
        /// ramps, else exponential (type 2) pieces stitched (type 3) at the stops.
        /// </summary>
        public PdfObject Function(PdfDocument document, bool alpha, int channels)
        {
            if (Sampled)
            {
                byte[] samples = new byte[Samples * channels];
                for (int i = 0; i < Samples; i++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        samples[(i * channels) + c] = (byte)Math.Round(Channel(Colors[i], alpha, c) * 255);
                    }
                }

                PdfStream sampled = new(new PdfDictionary
                {
                    ["FunctionType"] = new PdfInteger(0),
                    ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                    ["Range"] = new PdfArray(Enumerable.Range(0, channels).SelectMany(_ => new PdfObject[] { new PdfInteger(0), new PdfInteger(1) })),
                    ["Size"] = new PdfArray { new PdfInteger(Samples) },
                    ["BitsPerSample"] = new PdfInteger(8),
                });
                sampled.SetDecodedBytes(samples);
                return document.Objects.Add(sampled);
            }

            PdfArray functions = [], bounds = [], encode = [];
            for (int i = 0; i + 1 < Offsets.Length; i++)
            {
                functions.Add(new PdfDictionary
                {
                    ["FunctionType"] = new PdfInteger(2),
                    ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                    ["C0"] = new PdfArray(Enumerable.Range(0, channels).Select(c => (PdfObject)Real(Channel(Colors[i], alpha, c)))),
                    ["C1"] = new PdfArray(Enumerable.Range(0, channels).Select(c => (PdfObject)Real(Channel(Colors[i + 1], alpha, c)))),
                    ["N"] = new PdfInteger(1),
                });
                if (i > 0)
                {
                    bounds.Add(Real(Offsets[i]));
                }

                encode.Add(new PdfInteger(0));
                encode.Add(new PdfInteger(1));
            }

            return new PdfDictionary
            {
                ["FunctionType"] = new PdfInteger(3),
                ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) },
                ["Functions"] = functions,
                ["Bounds"] = bounds,
                ["Encode"] = encode,
            };
        }

        private static double Channel(RgbaF32 color, bool alpha, int channel)
        {
            return alpha ? color.A : channel switch { 0 => color.R, 1 => color.G, _ => color.B };
        }
    }
}
