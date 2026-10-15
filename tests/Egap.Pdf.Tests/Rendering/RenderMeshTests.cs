using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Mesh shadings (Phase 4 task 13): free-form and lattice triangles, Coons and tensor patches, with and without functions.</summary>
public sealed class RenderMeshTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void FreeFormTrianglesInterpolateVertexColours()
    {
        // One flag byte, x and y bytes, then r g b per vertex; coordinates decode 0..255 to 0..20.
        byte[] data =
        [
            0, 0, 0, 255, 0, 0,
            0, 255, 0, 0, 255, 0,
            0, 0, 255, 0, 0, 255,
            1, 255, 255, 255, 255, 255,
        ];
        using Raster<Rgba8> raster = Draw(4, data, components: 3, extra: null);

        Rgba8 nearRed = raster.GetPixel(1, 18), nearWhite = raster.GetPixel(18, 1);
        Assert.True(nearRed.R > 200 && nearRed.G < 60, nearRed.ToString());
        Assert.True(nearWhite.R > 200 && nearWhite.G > 200 && nearWhite.B > 200, nearWhite.ToString());
    }

    [Fact]
    public void LatticesFillTheirGrid()
    {
        // Two rows of two vertices (no flags): gray 0 at the bottom, 1 at the top.
        byte[] data = [0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 255];
        PdfDictionary extra = new() { ["VerticesPerRow"] = new PdfInteger(2) };
        using Raster<Rgba8> raster = Draw(5, data, components: 1, extra);

        Assert.True(raster.GetPixel(10, 18).R < 40);
        Assert.True(raster.GetPixel(10, 1).R > 215);
        Assert.InRange(raster.GetPixel(10, 10).R, 100, 155);
    }

    [Fact]
    public void FunctionMeshesTakeTheFunctionsColourBetweenVertices()
    {
        // t from 0 to 1 across a quad; the function is a step at 0.5 sampled finely, which interpolated colours would miss.
        PdfDictionary function = new()
        {
            ["FunctionType"] = new PdfInteger(3),
            ["Domain"] = Numbers(0, 1),
            ["Functions"] = new PdfArray { Exponential([1, 0, 0], [1, 0, 0]), Exponential([0, 0, 1], [0, 0, 1]) },
            ["Bounds"] = Numbers(0.5),
            ["Encode"] = Numbers(0, 1, 0, 1),
        };
        byte[] data = [0, 0, 0, 255, 0, 255, 0, 255, 0, 255, 255, 255];
        PdfDictionary extra = new() { ["VerticesPerRow"] = new PdfInteger(2), ["Function"] = function };
        using Raster<Rgba8> raster = Draw(5, data, components: 1, extra, space: "DeviceRGB");

        Rgba8 left = raster.GetPixel(6, 10), right = raster.GetPixel(14, 10);
        Assert.True(left.R > 230 && left.B < 25, left.ToString());
        Assert.True(right.B > 230 && right.R < 25, right.ToString());
    }

    [Fact]
    public void CoonsAndTensorPatchesCoverTheirBoundaryWithCornerColours()
    {
        // A square patch with straight edges: 12 boundary points from the bottom left round, then the 4 corner grays.
        byte[] coons = [0, .. Square(), 0, 85, 170, 255];
        byte[] tensor = [0, .. Square(), 85, 85, 170, 85, 170, 170, 85, 170, 0, 85, 170, 255];
        using Raster<Rgba8> c = Draw(6, coons, components: 1, extra: null);
        using Raster<Rgba8> t = Draw(7, tensor, components: 1, extra: null);

        foreach (Raster<Rgba8> raster in (Raster<Rgba8>[])[c, t])
        {
            // Corners 0, 85, 170 and 255 from the bottom left round, a little inside each corner.
            Assert.InRange(raster.GetPixel(1, 18).R, 10, 40);
            Assert.InRange(raster.GetPixel(18, 18).R, 75, 100);
            Assert.InRange(raster.GetPixel(18, 1).R, 155, 180);
            Assert.InRange(raster.GetPixel(1, 1).R, 215, 245);
            Assert.DoesNotContain(RenderCoreTests.Pixels(raster).Take(raster.Width * 10), p => p == White);
        }
    }

    [Fact]
    public void BrokenMeshesWarnAndDrawWhatTheyHave()
    {
        using Raster<Rgba8> truncated = Draw(4, [0, 0, 0, 255, 0, 0], components: 3, extra: null);
        using Raster<Rgba8> badBits = Draw(4, [0, 0, 0], components: 3, extra: new PdfDictionary { ["BitsPerCoordinate"] = new PdfInteger(7) });

        Assert.All(RenderCoreTests.Pixels(truncated), p => Assert.Equal(White, p));
        Assert.All(RenderCoreTests.Pixels(badBits), p => Assert.Equal(White, p));
    }

    private static byte[] Square()
    {
        // p00 p01 p02 p03 along the bottom, p13 p23 p33 up the right, p32 p31 p30 along the top, p20 p10 down the left.
        return [0, 0, 85, 0, 170, 0, 255, 0, 255, 85, 255, 170, 255, 255, 170, 255, 85, 255, 0, 255, 0, 170, 0, 85];
    }

    private static Raster<Rgba8> Draw(int type, byte[] data, int components, PdfDictionary? extra, string? space = null)
    {
        PdfDictionary dictionary = extra?.Clone() ?? [];
        dictionary["ShadingType"] = new PdfInteger(type);
        dictionary["ColorSpace"] = PdfName.Get(space ?? (components == 3 ? "DeviceRGB" : "DeviceGray"));
        dictionary["BitsPerCoordinate"] = dictionary.Get("BitsPerCoordinate") ?? new PdfInteger(8);
        dictionary["BitsPerComponent"] = new PdfInteger(8);
        if (type != 5)
        {
            dictionary["BitsPerFlag"] = new PdfInteger(8);
        }

        PdfArray decode = Numbers(0, 20, 0, 20);
        for (int i = 0; i < (dictionary.ContainsKey("Function") ? 1 : components); i++)
        {
            decode.Add(new PdfReal(0));
            decode.Add(new PdfReal(1));
        }

        dictionary["Decode"] = decode;
        PdfStream stream = new(dictionary);
        stream.SetDecodedBytes(data, compress: false);
        PdfDictionary resources = new() { ["Shading"] = new PdfDictionary { ["M"] = RenderCoreTests.Add(stream) } };
        return RenderCoreTests.Render("/M sh", resources: resources);
    }

    private static PdfDictionary Exponential(double[] c0, double[] c1)
    {
        return new PdfDictionary { ["FunctionType"] = new PdfInteger(2), ["Domain"] = Numbers(0, 1), ["C0"] = Numbers(c0), ["C1"] = Numbers(c1), ["N"] = new PdfInteger(1) };
    }

    private static PdfArray Numbers(params double[] values)
    {
        PdfArray array = [];
        foreach (double v in values)
        {
            array.Add(new PdfReal(v));
        }

        return array;
    }
}
