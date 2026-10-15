using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Images;

using BlendMode = Egami.Operations.BlendMode;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Mesh shadings (8.7.4.5.5 to 8.7.4.5.8, design Q10): free-form and lattice triangle meshes as Egami meshes, Coons and
/// tensor-product patch meshes as Egami patches. With a /Function each vertex carries its parametric value, which the canvas
/// interpolates and looks up in stops sampled from the function, so colour between vertices is the function's, as the spec
/// asks; without one the vertex colours are converted to sRGB and interpolated there.
/// </summary>
internal sealed class MeshShading
{
    /// <summary>The most vertices or patches read from one shading, against data that would exhaust memory.</summary>
    private const int MaxElements = 1 << 20;

    private const int Stops = 256;

    private readonly List<Mesh> meshes = [];
    private readonly List<Patch> patches = [];
    private readonly GradientStop[]? stops;

    private MeshShading(GradientStop[]? stops)
    {
        this.stops = stops;
    }

    /// <summary>The bounds of every vertex and control point, in shading space.</summary>
    public RectF Bounds { get; private set; }

    public bool IsEmpty => meshes.Count == 0 && patches.Count == 0;

    /// <summary>Draws the shading with the canvas's current matrix as shading space.</summary>
    public void Draw(Canvas canvas, Paint paint)
    {
        foreach (Mesh mesh in meshes)
        {
            canvas.DrawMesh(mesh, paint, BlendMode.Modulate, stops);
        }

        foreach (Patch patch in patches)
        {
            canvas.DrawPatch(patch, paint, BlendMode.Modulate, stops);
        }
    }

    /// <summary>The shading recorded once as a picture, for use as a pattern paint.</summary>
    public Picture ToPicture()
    {
        PictureRecorder recorder = new(Bounds);
        Draw(recorder, new SolidPaint(new RgbaF32(1, 1, 1, 1)));
        return recorder.Finish();
    }

    /// <summary>Reads a mesh shading's stream; throws <see cref="InvalidDataException"/> for one that cannot be read.</summary>
    public static MeshShading Read(PdfStream stream, int type, ImageColorSpace space)
    {
        PdfDictionary d = stream.Dictionary;
        int coordBits = (int)(d.GetInt("BitsPerCoordinate") ?? 0), componentBits = (int)(d.GetInt("BitsPerComponent") ?? 0);
        int flagBits = (int)(d.GetInt("BitsPerFlag") ?? 0);
        if (coordBits is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32) || componentBits is not (1 or 2 or 4 or 8 or 12 or 16)
            || (type != 5 && flagBits is not (2 or 4 or 8)))
        {
            throw new InvalidDataException("A mesh shading's bit counts are not ones the spec allows.");
        }

        PdfFunction? function = d.Get("Function") is PdfObject f ? PdfFunction.Read(f) : null;
        int components = function is null ? space.Components : 1;
        double[] decode = Numbers(d.Get<PdfArray>("Decode"));
        if (decode.Length < 4 + (2 * components))
        {
            throw new InvalidDataException("A mesh shading's /Decode is too short.");
        }

        GradientStop[]? stops = null;
        if (function is not null)
        {
            stops = new GradientStop[Stops];
            double[] input = new double[1];
            double[] output = new double[Math.Max(Math.Max(function.Outputs, space.Components), 1)];
            for (int i = 0; i < Stops; i++)
            {
                float offset = i / (float)(Stops - 1);
                input[0] = decode[4] + ((decode[5] - decode[4]) * offset);
                Array.Clear(output);
                function.Evaluate(input, output);
                stops[i] = new GradientStop(offset, Shadings.Convert(space, output));
            }
        }

        MeshShading shading = new(stops);
        BitReader reader = new(stream.GetDecodedBytes());
        Reading read = new(reader, coordBits, componentBits, flagBits, decode, components, space, function is not null);
        switch (type)
        {
            case 4:
                shading.FreeForm(read);
                break;
            case 5:
                int perRow = (int)(d.GetInt("VerticesPerRow") ?? 0);
                if (perRow < 2)
                {
                    throw new InvalidDataException("A lattice shading has fewer than two vertices per row.");
                }

                shading.Lattice(read, perRow);
                break;
            default:
                shading.Patches(read, tensor: type == 7);
                break;
        }

        return shading;
    }

    /// <summary>Type 4: each vertex's flag says whether it starts a triangle or extends the last one (8.7.4.5.5).</summary>
    private void FreeForm(Reading read)
    {
        Vertex a = default, b = default, c = default;
        List<Vertex> vertices = [];
        int count = 0;
        while (read.Reader.Remaining >= read.VertexBits(withFlag: true) && count++ < MaxElements)
        {
            int flag = read.Flag();
            Vertex v = read.Vertex();
            read.Reader.Align();
            if (flag == 0)
            {
                // Two more vertices complete the new triangle.
                if (read.Reader.Remaining < 2 * read.VertexBits(withFlag: true))
                {
                    break;
                }

                read.Flag();
                Vertex v2 = read.Vertex();
                read.Reader.Align();
                read.Flag();
                Vertex v3 = read.Vertex();
                read.Reader.Align();
                (a, b, c) = (v, v2, v3);
            }
            else if (vertices.Count == 0)
            {
                // A continuation before any triangle has nothing to share.
                continue;
            }
            else
            {
                (a, b, c) = flag == 1 ? (b, c, v) : (a, c, v);
            }

            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
        }

        AddTriangles(vertices);
    }

    /// <summary>Type 5: rows of vertices, each quadrilateral between two rows cut into two triangles (8.7.4.5.6).</summary>
    private void Lattice(Reading read, int perRow)
    {
        List<Vertex> grid = [];
        while (read.Reader.Remaining >= read.VertexBits(withFlag: false) && grid.Count < MaxElements)
        {
            grid.Add(read.Vertex());
            read.Reader.Align();
        }

        List<Vertex> vertices = [];
        int rows = grid.Count / perRow;
        for (int r = 0; r + 1 < rows; r++)
        {
            for (int i = 0; i + 1 < perRow; i++)
            {
                Vertex p = grid[(r * perRow) + i], q = grid[(r * perRow) + i + 1], s = grid[((r + 1) * perRow) + i], t = grid[((r + 1) * perRow) + i + 1];
                vertices.AddRange([p, q, s, q, t, s]);
            }
        }

        AddTriangles(vertices);
    }

    /// <summary>Types 6 and 7: patches, each new or sharing an edge of the one before (8.7.4.5.7, 8.7.4.5.8).</summary>
    private void Patches(Reading read, bool tensor)
    {
        // Points in stream order: the 12 boundary points from the first corner round, then for a tensor patch the 4 inside.
        int total = tensor ? 16 : 12;
        Vector2[]? previous = null;
        Vertex[]? previousCorners = null;
        int count = 0;
        while (read.Reader.Remaining >= read.FlagBits && count++ < MaxElements)
        {
            int flag = read.Flag();
            int shared = flag == 0 ? 0 : 4;
            if (flag != 0 && previous is null)
            {
                break;
            }

            int newPoints = total - shared, newColors = flag == 0 ? 4 : 2;
            if (read.Reader.Remaining < (newPoints * 2 * read.CoordBits) + (newColors * read.Components * read.ComponentBits))
            {
                break;
            }

            Vector2[] points = new Vector2[total];
            Vertex[] corners = new Vertex[4];
            if (flag != 0)
            {
                // The shared edge, in the order the spec lists for each flag: D (corner 2 to 3), C (3 to 4) or B (4 to 1).
                int start = flag switch { 1 => 3, 2 => 6, _ => 9 };
                for (int k = 0; k < 4; k++)
                {
                    points[k] = previous![(start + k) % 12];
                }

                corners[0] = previousCorners![flag];
                corners[1] = previousCorners[(flag + 1) % 4];
            }

            for (int k = shared; k < total; k++)
            {
                points[k] = read.Point();
            }

            for (int k = 4 - newColors; k < 4; k++)
            {
                corners[k] = new Vertex(default, read.Color());
            }

            read.Reader.Align();
            for (int k = 0; k < 4; k++)
            {
                corners[k] = corners[k] with { Position = points[k * 3] };
            }

            AddPatch(points, corners, tensor);
            previous = points;
            previousCorners = corners;
        }
    }

    private void AddTriangles(List<Vertex> vertices)
    {
        if (vertices.Count < 3)
        {
            return;
        }

        Vector2[] positions = [.. vertices.Select(v => v.Position)];
        Mesh mesh = stops is not null
            ? new Mesh(MeshMode.Triangles, positions, values: [.. vertices.Select(v => v.Color.R)])
            : new Mesh(MeshMode.Triangles, positions, colors: [.. vertices.Select(v => v.Color)]);
        meshes.Add(mesh);
        Grow(mesh.Bounds);
    }

    private void AddPatch(Vector2[] points, Vertex[] corners, bool tensor)
    {
        RgbaF32[] colors = [.. corners.Select(c => c.Color)];
        float[] values = [.. corners.Select(c => c.Color.R)];
        Patch patch;
        if (tensor)
        {
            // Stream order to the 4 by 4 net row by row: the boundary round from p00, then p11, p12, p22, p21.
            Vector2[] net = new Vector2[16];
            int[] at = [0, 1, 2, 3, 7, 11, 15, 14, 13, 12, 8, 4, 5, 6, 10, 9];
            for (int k = 0; k < 16; k++)
            {
                net[at[k]] = points[k];
            }

            patch = stops is not null ? Patch.Tensor(net, cornerValues: values) : Patch.Tensor(net, colors);
        }
        else
        {
            patch = stops is not null ? Patch.Coons(points, cornerValues: values) : Patch.Coons(points, colors);
        }

        patches.Add(patch);
        Grow(patch.Bounds);
    }

    private void Grow(RectF bounds)
    {
        Bounds = meshes.Count + patches.Count == 1 ? bounds : Bounds.Union(bounds);
    }

    private static double[] Numbers(PdfArray? array)
    {
        return array is null ? [] : [.. Enumerable.Range(0, array.Count).Select(i => array.GetNumber(i) ?? 0)];
    }

    /// <summary>
    /// A vertex: its point in shading space and colour, sRGB with alpha 1; with a function, the parametric value normalized
    /// to 0 to 1 over its decode range is kept in R.
    /// </summary>
    private readonly record struct Vertex(Vector2 Position, RgbaF32 Color);

    /// <summary>How a shading's data is laid out and decoded.</summary>
    private sealed class Reading(BitReader reader, int coordBits, int componentBits, int flagBits, double[] decode, int components, ImageColorSpace space, bool parametric)
    {
        public BitReader Reader => reader;

        public int CoordBits => coordBits;

        public int ComponentBits => componentBits;

        public int FlagBits => flagBits;

        public int Components => components;

        public long VertexBits(bool withFlag)
        {
            return (withFlag ? flagBits : 0) + (2L * coordBits) + ((long)components * componentBits);
        }

        public int Flag()
        {
            return (int)reader.Read(flagBits) & 3;
        }

        public Vector2 Point()
        {
            double x = Decode(reader.Read(coordBits), coordBits, decode[0], decode[1]);
            double y = Decode(reader.Read(coordBits), coordBits, decode[2], decode[3]);
            return new Vector2(Finite(x), Finite(y));
        }

        public RgbaF32 Color()
        {
            Span<double> c = stackalloc double[Math.Max(components, 1)];
            for (int i = 0; i < components; i++)
            {
                c[i] = Decode(reader.Read(componentBits), componentBits, decode[4 + (2 * i)], decode[5 + (2 * i)]);
            }

            if (parametric)
            {
                double range = decode[5] - decode[4];
                return new RgbaF32(range == 0 ? 0 : (float)Math.Clamp((c[0] - decode[4]) / range, 0, 1), 0, 0, 1);
            }

            return Shadings.Convert(space, c);
        }

        public Vertex Vertex()
        {
            Vector2 point = Point();
            return new Vertex(point, Color());
        }

        private static double Decode(ulong value, int bits, double min, double max)
        {
            double top = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
            return min + (value * (max - min) / top);
        }

        private static float Finite(double value)
        {
            return double.IsFinite(value) ? (float)Math.Clamp(value, -1e7, 1e7) : 0;
        }
    }

    /// <summary>Reads big-endian bit fields up to 32 bits from a byte array.</summary>
    private sealed class BitReader(byte[] data)
    {
        private long position;

        public long Remaining => (8L * data.Length) - position;

        public ulong Read(int bits)
        {
            ulong value = 0;
            for (int i = 0; i < bits; i++)
            {
                long at = position >> 3;
                int bit = at < data.Length ? (data[at] >> (7 - (int)(position & 7))) & 1 : 0;
                value = (value << 1) | (uint)bit;
                position++;
            }

            return value;
        }

        public void Align()
        {
            position = (position + 7) & ~7L;
        }
    }
}
