using System.Numerics;

using Egami.Colors;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>A shading made ready to paint: the paint in shading space, its bounding box there, and its background.</summary>
internal sealed record ShadingPaint(Paint? Paint, PdfRectangle? BBox, RgbaF32? Background, PdfDictionary Dictionary, MeshShading? Mesh = null)
{
    private Picture? picture;

    /// <summary>A mesh shading as a paint for patterns: recorded once as a picture and drawn where it covers, nothing elsewhere.</summary>
    public Paint? MeshPaint()
    {
        // A mesh with no area (all its points on a line) draws nothing, and a picture needs bounds with area.
        if (Mesh is null || Mesh.IsEmpty || !(Mesh.Bounds.Width > 0 && Mesh.Bounds.Height > 0))
        {
            return null;
        }

        Picture recorded = LazyInitializer.EnsureInitialized(ref picture, Mesh.ToPicture);
        return new PicturePaint(recorded, TileMode.Decal, TileMode.Decal, Mesh.Bounds);
    }
}

/// <summary>
/// Shadings (8.7.4.5, design Q10) as Egami paints in shading space: axial and radial shadings as linear and two-point
/// conical gradients whose stops sample the function (256 of them), with Extend as ExtendStart and ExtendEnd; function-based
/// shadings as a <see cref="CustomPaint"/> that evaluates the function per pixel. Mesh shadings are drawn elsewhere.
/// </summary>
internal static class Shadings
{
    private const int Stops = 256;

    /// <summary>The paint for a shading, or one with no paint for a mesh type or a shading that cannot be read.</summary>
    public static ShadingPaint Read(PdfObject shading, ColorCache colors, PdfDictionary? resources, RenderingIntent intent)
    {
        PdfDictionary dictionary = shading switch
        {
            PdfStream stream => stream.Dictionary,
            PdfDictionary d => d,
            _ => throw new InvalidDataException("A shading is not a dictionary or stream."),
        };
        ImageColorSpace space = dictionary.Get("ColorSpace") is PdfObject cs && colors.Space(cs.Resolve(), resources, intent) is ImageColorSpace found
            ? found
            : throw new InvalidDataException("A shading has no usable colour space.");
        PdfRectangle? box = PdfRectangle.FromArray(dictionary.Get<PdfArray>("BBox"));
        RgbaF32? background = dictionary.Get<PdfArray>("Background") is PdfArray bg ? Convert(space, Numbers(bg)) : null;
        int type = (int)(dictionary.GetInt("ShadingType") ?? 0);
        if (type is >= 4 and <= 7)
        {
            return shading is PdfStream mesh
                ? new ShadingPaint(null, box, background, dictionary, MeshShading.Read(mesh, type, space))
                : throw new InvalidDataException("A mesh shading is not a stream.");
        }

        Paint paint = type switch
        {
            1 => Function(dictionary, space),
            2 => Axial(dictionary, space),
            3 => Radial(dictionary, space),
            _ => throw new InvalidDataException($"The shading type {type} is not known."),
        };
        return new ShadingPaint(paint, box, background, dictionary);
    }

    /// <summary>A colour in the shading's space as sRGB; components past the space's count are ignored.</summary>
    public static RgbaF32 Convert(ImageColorSpace space, ReadOnlySpan<double> components)
    {
        Span<double> rgb = stackalloc double[3];
        Span<double> c = stackalloc double[Math.Max(space.Components, 1)];
        components[..Math.Min(components.Length, c.Length)].CopyTo(c);
        space.ToRgb(c[..space.Components], rgb);
        return space.PaintsNothing ? default : new RgbaF32(Unit(rgb[0]), Unit(rgb[1]), Unit(rgb[2]), 1);
    }

    private static LinearGradient Axial(PdfDictionary d, ImageColorSpace space)
    {
        double[] coords = Numbers(d.Get<PdfArray>("Coords")) is { Length: >= 4 } c ? c : throw new InvalidDataException("An axial shading has no /Coords.");
        (bool start, bool end) = Extend(d);
        return new LinearGradient(new Vector2((float)coords[0], (float)coords[1]), new Vector2((float)coords[2], (float)coords[3]), StopsOf(d, space))
        {
            ExtendStart = start,
            ExtendEnd = end,
        };
    }

    private static ConicalGradient Radial(PdfDictionary d, ImageColorSpace space)
    {
        double[] coords = Numbers(d.Get<PdfArray>("Coords")) is { Length: >= 6 } c ? c : throw new InvalidDataException("A radial shading has no /Coords.");
        (bool start, bool end) = Extend(d);
        return new ConicalGradient(
            new Vector2((float)coords[0], (float)coords[1]),
            (float)Math.Max(coords[2], 0),
            new Vector2((float)coords[3], (float)coords[4]),
            (float)Math.Max(coords[5], 0),
            StopsOf(d, space))
        {
            ExtendStart = start,
            ExtendEnd = end,
        };
    }

    /// <summary>The colour at evenly spaced t over /Domain, as gradient stops from 0 to 1.</summary>
    private static GradientStop[] StopsOf(PdfDictionary d, ImageColorSpace space)
    {
        PdfFunction function = PdfFunction.Read(d.Get("Function") ?? throw new InvalidDataException("A shading has no /Function."));
        double[] domain = Numbers(d.Get<PdfArray>("Domain")) is { Length: >= 2 } given ? given : [0, 1];
        GradientStop[] stops = new GradientStop[Stops];
        double[] input = new double[1];
        double[] output = new double[Math.Max(Math.Max(function.Outputs, space.Components), 1)];
        for (int i = 0; i < Stops; i++)
        {
            float offset = i / (float)(Stops - 1);
            input[0] = domain[0] + ((domain[1] - domain[0]) * offset);
            Array.Clear(output);
            function.Evaluate(input, output);
            stops[i] = new GradientStop(offset, Convert(space, output));
        }

        return stops;
    }

    private static FunctionShading Function(PdfDictionary d, ImageColorSpace space)
    {
        PdfFunction function = PdfFunction.Read(d.Get("Function") ?? throw new InvalidDataException("A function shading has no /Function."));
        double[] domain = Numbers(d.Get<PdfArray>("Domain")) is { Length: >= 4 } given ? given : [0, 1, 0, 1];
        PdfMatrix matrix = PdfMatrix.FromArray(d.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity;
        return new FunctionShading(function, space, domain, matrix);
    }

    private static (bool Start, bool End) Extend(PdfDictionary d)
    {
        PdfArray? extend = d.Get<PdfArray>("Extend");
        return (extend?.Count > 0 && extend[0].Resolve() is PdfBoolean { Value: true }, extend?.Count > 1 && extend[1].Resolve() is PdfBoolean { Value: true });
    }

    private static double[] Numbers(PdfArray? array)
    {
        return array is null ? [] : [.. Enumerable.Range(0, array.Count).Select(i => array.GetNumber(i) ?? 0)];
    }

    private static float Unit(double value)
    {
        return double.IsFinite(value) ? (float)Math.Clamp(value, 0, 1) : 0;
    }

    /// <summary>
    /// A function-based shading (type 1): each pixel's point in shading space goes back through /Matrix into the domain,
    /// where the two-input function gives its colour; outside the domain nothing is painted.
    /// </summary>
    private sealed record class FunctionShading : CustomPaint
    {
        private readonly PdfFunction function;
        private readonly ImageColorSpace space;
        private readonly double[] domain;
        private readonly Matrix3x2 toDomain;

        public FunctionShading(PdfFunction function, ImageColorSpace space, double[] domain, PdfMatrix matrix)
        {
            this.function = function;
            this.space = space;
            this.domain = domain;
            Matrix3x2 m = new((float)matrix.A, (float)matrix.B, (float)matrix.C, (float)matrix.D, (float)matrix.E, (float)matrix.F);
            toDomain = Matrix3x2.Invert(m, out Matrix3x2 inverse) ? inverse : default;
        }

        public override void Shade(int y, int x, Span<PremulRgbaF32> row, in Matrix3x2 inverse)
        {
            Span<double> input = stackalloc double[2];
            Span<double> output = stackalloc double[Math.Max(Math.Max(function.Outputs, space.Components), 1)];
            for (int i = 0; i < row.Length; i++)
            {
                Vector2 p = Vector2.Transform(Vector2.Transform(new Vector2(x + i + 0.5f, y + 0.5f), inverse), toDomain);
                if (p.X < domain[0] || p.X > domain[1] || p.Y < domain[2] || p.Y > domain[3] || !float.IsFinite(p.X) || !float.IsFinite(p.Y))
                {
                    row[i] = default;
                    continue;
                }

                input[0] = p.X;
                input[1] = p.Y;
                output.Clear();
                function.Evaluate(input, output);
                RgbaF32 c = Convert(space, output);
                row[i] = new PremulRgbaF32 { R = c.R * c.A, G = c.G * c.A, B = c.B * c.A, A = c.A };
            }
        }
    }
}
