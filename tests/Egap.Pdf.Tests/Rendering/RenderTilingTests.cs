using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Tiling patterns (Phase 4 task 14): coloured and uncoloured cells repeated by their steps, and recursion limits.</summary>
public sealed class RenderTilingTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void ColouredPatternsRepeatTheirCellByTheSteps()
    {
        // A 10 by 10 cell with a red 5 by 5 square in its lower left corner.
        PdfDictionary resources = Pattern("1 0 0 rg 0 0 5 5 re f", paintType: 1, step: 10);
        using Raster<Rgba8> raster = RenderCoreTests.Render("/Pattern cs /P scn 0 0 20 20 re f", resources: resources);

        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(2, 17));
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(12, 7));
        Assert.Equal(White, raster.GetPixel(7, 17));
        Assert.Equal(White, raster.GetPixel(2, 12));
    }

    [Fact]
    public void UncolouredPatternsPaintInTheGivenColour()
    {
        PdfDictionary resources = Pattern("1 0 0 rg 0 0 5 5 re f", paintType: 2, step: 10);
        resources["ColorSpace"] = new PdfDictionary { ["U"] = new PdfArray { PdfName.Get("Pattern"), PdfName.Get("DeviceRGB") } };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/U cs 0 0 1 /P scn 0 0 20 20 re f", resources: resources);

        Assert.Equal(new Rgba8(0, 0, 255, 255), raster.GetPixel(2, 17));
        Assert.Equal(White, raster.GetPixel(7, 17));
    }

    [Fact]
    public void PatternsStayInPatternSpaceUnderTheCtm()
    {
        PdfDictionary resources = Pattern("1 0 0 rg 0 0 5 5 re f", paintType: 1, step: 10);
        using Raster<Rgba8> raster = RenderCoreTests.Render("/Pattern cs /P scn q 2 0 0 2 0 0 cm 0 0 10 10 re f Q", resources: resources);

        // The CTM scales the shape, not the pattern: the cell is still 10 wide.
        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(12, 17));
        Assert.Equal(White, raster.GetPixel(7, 17));
    }

    [Fact]
    public void PatternsThatDrawThemselvesStop()
    {
        PdfDictionary resources = Pattern("/Pattern cs /P scn 0 0 10 10 re f", paintType: 1, step: 10);
        PdfStream pattern = (PdfStream)resources.Get<PdfDictionary>("Pattern")!.Get("P")!.Resolve();
        pattern.Dictionary["Resources"] = resources.Clone();

        using Raster<Rgba8> raster = RenderCoreTests.Render("/Pattern cs /P scn 0 0 20 20 re f", resources: resources);

        Assert.All(RenderCoreTests.Pixels(raster), p => Assert.Equal(White, p));
    }

    private static PdfDictionary Pattern(string content, int paintType, double step)
    {
        PdfStream cell = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Pattern"),
            ["PatternType"] = new PdfInteger(1),
            ["PaintType"] = new PdfInteger(paintType),
            ["TilingType"] = new PdfInteger(1),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfReal(step), new PdfReal(step) },
            ["XStep"] = new PdfReal(step),
            ["YStep"] = new PdfReal(step),
        });
        cell.SetDecodedBytes(System.Text.Encoding.ASCII.GetBytes(content), compress: false);
        return new PdfDictionary { ["Pattern"] = new PdfDictionary { ["P"] = RenderCoreTests.Add(cell) } };
    }
}
