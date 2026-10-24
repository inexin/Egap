using Egami;
using Egami.Pixels;

using Egap.Pdf.Images;

namespace Egap.Pdf.Rendering;

/// <summary>
/// A graphics state's transfer function (10.5, <c>/TR</c> and <c>/TR2</c>) sampled into a 1,024-entry table per component:
/// one function for every component, or four for red, green, blue and gray, which on plates are cyan, magenta, yellow and
/// black (spot plates take the fourth, as for gray). Functions map additive values, so on plates a tint t becomes
/// 1 - f(1 - t). They change source colours before they are blended, as the spec places them.
/// </summary>
internal sealed class TransferTable
{
    private const int Size = 1024;

    /// <summary>The table for each of the four components; null where the component is left as it is (Identity).</summary>
    private readonly float[]?[] tables;

    private TransferTable(float[]?[] tables)
    {
        this.tables = tables;
    }

    /// <summary>
    /// The table for a <c>/TR</c> or <c>/TR2</c> value, or null when it changes nothing (Identity, Default, or four
    /// Identity entries). Throws <see cref="InvalidDataException"/> for a value that is not a function or four of them.
    /// </summary>
    public static TransferTable? Read(PdfObject value)
    {
        value = value.Resolve();
        if (value is PdfName)
        {
            return null;
        }

        if (value is PdfArray array)
        {
            if (array.Count != 4)
            {
                throw new InvalidDataException("A transfer function array does not hold four functions.");
            }

            float[]?[] four = [.. Enumerable.Range(0, 4).Select(i => array.Get(i).Resolve() is PdfName ? null : Sample(array.Get(i)))];
            return four.All(t => t is null) ? null : new TransferTable(four);
        }

        float[] one = Sample(value);
        return new TransferTable([one, one, one, one]);
    }

    /// <summary>An additive component through its function, 0 to 1.</summary>
    public float Map(int component, float value)
    {
        if (tables[component] is not float[] table)
        {
            return value;
        }

        float position = float.IsFinite(value) ? Math.Clamp(value, 0, 1) * (Size - 1) : 0;
        int i = Math.Min((int)position, Size - 2);
        return table[i] + ((table[i + 1] - table[i]) * (position - i));
    }

    /// <summary>An ink tint on a plate through the plate's function: 0 is no ink, so the function sees 1 - tint.</summary>
    public float Tint(int component, float tint)
    {
        return 1 - Map(component, 1 - tint);
    }

    /// <summary>The colour with red, green and blue through their functions; alpha as it is.</summary>
    public RgbaF32 Apply(RgbaF32 color)
    {
        return new RgbaF32(Map(0, color.R), Map(1, color.G), Map(2, color.B), color.A);
    }

    /// <summary>A copy of an 8- or 16-bit RGBA image with its colour through the functions; other rasters come back as they are.</summary>
    public Raster Apply(Raster raster)
    {
        switch (raster)
        {
            case Raster<Rgba8> narrow:
                {
                    byte[][] levels = [.. Enumerable.Range(0, 3).Select(c => Enumerable.Range(0, 256).Select(v => (byte)MathF.Round(Map(c, v / 255f) * 255)).ToArray())];
                    Raster<Rgba8> mapped = Raster.Create<Rgba8>(narrow.Width, narrow.Height);
                    for (int y = 0; y < narrow.Height; y++)
                    {
                        ReadOnlySpan<Rgba8> from = narrow.Row(y);
                        Span<Rgba8> to = mapped.Row(y);
                        for (int x = 0; x < from.Length; x++)
                        {
                            to[x] = new Rgba8(levels[0][from[x].R], levels[1][from[x].G], levels[2][from[x].B], from[x].A);
                        }
                    }

                    return mapped;
                }

            case Raster<Rgba16> deep:
                {
                    Raster<Rgba16> mapped = Raster.Create<Rgba16>(deep.Width, deep.Height);
                    for (int y = 0; y < deep.Height; y++)
                    {
                        ReadOnlySpan<Rgba16> from = deep.Row(y);
                        Span<Rgba16> to = mapped.Row(y);
                        for (int x = 0; x < from.Length; x++)
                        {
                            to[x] = new Rgba16(Unit16(Map(0, from[x].R / 65535f)), Unit16(Map(1, from[x].G / 65535f)), Unit16(Map(2, from[x].B / 65535f)), from[x].A);
                        }
                    }

                    return mapped;
                }

            default:
                return raster;
        }
    }

    private static float[] Sample(PdfObject function)
    {
        PdfFunction read = PdfFunction.Read(function);
        float[] values = new float[Size];
        Span<double> input = stackalloc double[1];
        Span<double> output = stackalloc double[Math.Max(read.Outputs, 1)];
        for (int i = 0; i < Size; i++)
        {
            input[0] = i / (double)(Size - 1);
            output.Clear();
            read.Evaluate(input, output);
            values[i] = double.IsFinite(output[0]) ? (float)Math.Clamp(output[0], 0, 1) : 0;
        }

        return values;
    }

    private static ushort Unit16(float v)
    {
        return (ushort)MathF.Round(Math.Clamp(v, 0, 1) * 65535);
    }
}
