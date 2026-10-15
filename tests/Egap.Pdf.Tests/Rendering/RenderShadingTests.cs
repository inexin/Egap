using System.Text;

using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Axial, radial and function shadings (Phase 4 task 12): the sh operator, Extend, BBox and shading patterns.</summary>
public sealed class RenderShadingTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void AxialShadingsRunAlongTheirAxisAndExtendOnlyWhenAsked()
    {
        PdfDictionary resources = Resources(Axial(extend: false), Axial(extend: true));
        using Raster<Rgba8> plain = RenderCoreTests.Render("/A sh", width: 40, height: 10, resources: resources);
        using Raster<Rgba8> extended = RenderCoreTests.Render("/E sh", width: 40, height: 10, resources: resources);

        // Black at x 10 to white at x 30; outside that, nothing unless extended.
        Assert.InRange(plain.GetPixel(20, 5).R, 120, 136);
        Assert.InRange(plain.GetPixel(11, 5).R, 0, 20);
        Assert.Equal(White, plain.GetPixel(5, 5));
        Assert.Equal(new Rgba8(0, 0, 0, 255), extended.GetPixel(5, 5));
        Assert.Equal(White, extended.GetPixel(35, 5));
    }

    [Fact]
    public void RadialShadingsGoFromTheInnerToTheOuterCircle()
    {
        PdfDictionary radial = new()
        {
            ["ShadingType"] = new PdfInteger(3),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
            ["Coords"] = Numbers(20, 20, 0, 20, 20, 20),
            ["Function"] = Exponential([1, 0, 0], [0, 0, 1]),
        };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/R sh", width: 40, height: 40, resources: new PdfDictionary { ["Shading"] = new PdfDictionary { ["R"] = radial } });

        Rgba8 center = raster.GetPixel(20, 20), half = raster.GetPixel(30, 20);
        Assert.True(center.R > 240 && center.B < 15, center.ToString());
        Assert.InRange(half.R, 110, 145);
        Assert.InRange(half.B, 110, 145);
        Assert.Equal(White, raster.GetPixel(2, 2));
    }

    [Fact]
    public void FunctionShadingsEvaluateEachPixelInsideTheirDomain()
    {
        PdfStream function = new(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(4),
            ["Domain"] = Numbers(0, 1, 0, 1),
            ["Range"] = Numbers(0, 1, 0, 1, 0, 1),
        });
        function.SetDecodedBytes(Encoding.ASCII.GetBytes("{ 0 }"), compress: false);
        PdfDictionary shading = new()
        {
            ["ShadingType"] = new PdfInteger(1),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
            ["Matrix"] = Numbers(10, 0, 0, 10, 5, 5),
            ["Function"] = RenderCoreTests.Add(function),
        };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/F sh", resources: new PdfDictionary { ["Shading"] = new PdfDictionary { ["F"] = shading } });

        // The function maps (x, y) to (x, y, 0): the domain square sits at 5 to 15 on the page.
        Rgba8 inside = raster.GetPixel(13, 7);
        Assert.InRange(inside.R, 190, 230);
        Assert.InRange(inside.G, 190, 230);
        Assert.Equal(White, raster.GetPixel(2, 2));
    }

    [Fact]
    public void ShadingPatternsFillShapesInPatternSpace()
    {
        PdfDictionary pattern = new()
        {
            ["PatternType"] = new PdfInteger(2),
            ["Shading"] = Axial(extend: true),
            ["Matrix"] = Numbers(0.5, 0, 0, 0.5, 0, 0),
        };
        PdfDictionary resources = new() { ["Pattern"] = new PdfDictionary { ["P"] = pattern } };

        // The axis is 10 to 30 in shading space, 5 to 15 on the page; the pattern stays put under a transformed fill.
        using Raster<Rgba8> raster = RenderCoreTests.Render("/Pattern cs /P scn q 2 0 0 1 0 0 cm 0 0 20 10 re f Q", width: 40, height: 10, resources: resources);

        Assert.InRange(raster.GetPixel(10, 5).R, 115, 140);
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(2, 5));
        Assert.Equal(White, raster.GetPixel(30, 5));
    }

    [Fact]
    public void BBoxClipsShAndBrokenShadingsWarn()
    {
        PdfDictionary boxed = Axial(extend: true);
        boxed["BBox"] = Numbers(0, 0, 20, 10);
        PdfDictionary resources = new() { ["Shading"] = new PdfDictionary { ["B"] = boxed, ["X"] = new PdfDictionary { ["ShadingType"] = new PdfInteger(2) } } };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/B sh /X sh", width: 40, height: 10, resources: resources);

        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(5, 5));
        Assert.Equal(White, raster.GetPixel(30, 5));
    }

    private static PdfDictionary Resources(PdfDictionary a, PdfDictionary e)
    {
        return new PdfDictionary { ["Shading"] = new PdfDictionary { ["A"] = a, ["E"] = e } };
    }

    private static PdfDictionary Axial(bool extend)
    {
        PdfDictionary shading = new()
        {
            ["ShadingType"] = new PdfInteger(2),
            ["ColorSpace"] = PdfName.Get("DeviceGray"),
            ["Coords"] = Numbers(10, 0, 30, 0),
            ["Function"] = Exponential([0], [1]),
        };
        if (extend)
        {
            shading["Extend"] = new PdfArray { PdfBoolean.True, PdfBoolean.True };
        }

        return shading;
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
