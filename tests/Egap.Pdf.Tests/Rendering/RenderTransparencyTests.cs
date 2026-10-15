using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Transparency (Phase 4 task 11): blend modes, groups (isolated, non-isolated, knockout) and soft masks.</summary>
public sealed class RenderTransparencyTests
{
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void BlendModesApplyToEachObject()
    {
        PdfDictionary resources = new() { ["ExtGState"] = new PdfDictionary { ["M"] = new PdfDictionary { ["BM"] = PdfName.Get("Multiply") } } };
        using Raster<Rgba8> raster = RenderCoreTests.Render("1 1 0 rg 0 0 14 20 re f /M gs 0 1 1 rg 6 0 14 20 re f", resources: resources);

        Assert.Equal(new Rgba8(255, 255, 0, 255), raster.GetPixel(2, 10));
        Assert.Equal(new Rgba8(0, 255, 0, 255), raster.GetPixel(10, 10));
        Assert.Equal(new Rgba8(0, 255, 255, 255), raster.GetPixel(17, 10));
    }

    [Fact]
    public void AGroupTakesItsAlphaAsAWhole()
    {
        // Two overlapping black squares: in a group drawn at half alpha the overlap is as light as the rest; drawn one by
        // one at half alpha it is darker.
        PdfDictionary resources = Resources("0 0 0 rg 0 0 12 20 re f 8 0 12 20 re f", group: "/I true", alpha: 0.5);
        using Raster<Rgba8> grouped = RenderCoreTests.Render("/H gs /G Do", resources: resources);
        using Raster<Rgba8> separate = RenderCoreTests.Render("/H gs 0 0 0 rg 0 0 12 20 re f 8 0 12 20 re f", resources: resources);

        Assert.InRange(grouped.GetPixel(4, 10).R, 120, 135);
        Assert.InRange(grouped.GetPixel(10, 10).R, 120, 135);
        Assert.InRange(separate.GetPixel(10, 10).R, 56, 70);
    }

    [Fact]
    public void KnockoutGroupsDoNotDarkenTheirOwnOverlaps()
    {
        PdfDictionary plain = Resources("/H gs 0 0 0 rg 0 0 12 20 re f 8 0 12 20 re f", group: "/I true", alpha: 0.5);
        PdfDictionary knockout = Resources("/H gs 0 0 0 rg 0 0 12 20 re f 8 0 12 20 re f", group: "/I true /K true", alpha: 0.5);
        using Raster<Rgba8> normal = RenderCoreTests.Render("/G Do", resources: plain);
        using Raster<Rgba8> knocked = RenderCoreTests.Render("/G Do", resources: knockout);

        Assert.InRange(normal.GetPixel(10, 10).R, 56, 70);
        Assert.InRange(knocked.GetPixel(10, 10).R, 120, 135);
    }

    [Fact]
    public void NonIsolatedGroupsBlendWithTheirBackdrop()
    {
        PdfDictionary resources = Resources("/M gs 0 1 1 rg 0 0 20 20 re f", group: "", alpha: 1);
        resources.Get<PdfDictionary>("ExtGState")!["M"] = new PdfDictionary { ["BM"] = PdfName.Get("Multiply") };
        using Raster<Rgba8> raster = RenderCoreTests.Render("1 1 0 rg 0 0 20 20 re f /G Do", resources: resources);

        Assert.Equal(new Rgba8(0, 255, 0, 255), raster.GetPixel(10, 10));
    }

    [Fact]
    public void LuminositySoftMasksShowWhereTheMaskIsLight()
    {
        PdfDictionary resources = MaskResources("Luminosity", "1 g 0 0 10 20 re f 0 g 10 0 10 20 re f", backdrop: null);
        using Raster<Rgba8> raster = RenderCoreTests.Render("/S gs 1 0 0 rg 0 0 20 20 re f", resources: resources);

        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(5, 10));
        Assert.Equal(White, raster.GetPixel(15, 10));
    }

    [Fact]
    public void AlphaSoftMasksUseCoverageAndBackdropsFillTheRest()
    {
        PdfDictionary alpha = MaskResources("Alpha", "0 0 10 20 re f", backdrop: null);
        PdfDictionary lit = MaskResources("Luminosity", "0 g 0 0 10 20 re f", backdrop: 1);
        using Raster<Rgba8> raster = RenderCoreTests.Render("/S gs 1 0 0 rg 0 0 20 20 re f", resources: alpha);
        using Raster<Rgba8> backdrop = RenderCoreTests.Render("/S gs 1 0 0 rg 0 0 20 20 re f", resources: lit);

        Assert.Equal(new Rgba8(255, 0, 0, 255), raster.GetPixel(5, 10));
        Assert.Equal(White, raster.GetPixel(15, 10));

        // Outside the mask group's drawing the white backdrop shows the object; inside, black hides it.
        Assert.Equal(White, backdrop.GetPixel(5, 10));
        Assert.Equal(new Rgba8(255, 0, 0, 255), backdrop.GetPixel(15, 10));
    }

    [Fact]
    public void TransferFunctionsShapeTheMask()
    {
        PdfDictionary resources = MaskResources("Luminosity", "1 g 0 0 20 20 re f", backdrop: null);
        PdfDictionary smask = resources.Get<PdfDictionary>("ExtGState")!.Get<PdfDictionary>("S")!.Get<PdfDictionary>("SMask")!;
        smask["TR"] = new PdfDictionary { ["FunctionType"] = new PdfInteger(2), ["Domain"] = new PdfArray { new PdfInteger(0), new PdfInteger(1) }, ["C0"] = new PdfArray { new PdfInteger(0) }, ["C1"] = new PdfArray { new PdfReal(0.5) }, ["N"] = new PdfInteger(1) };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/S gs 0 0 0 rg 0 0 20 20 re f", resources: resources);

        Assert.InRange(raster.GetPixel(10, 10).R, 120, 135);
    }

    private static PdfDictionary Resources(string content, string group, double alpha)
    {
        PdfDictionary groupDictionary = new() { ["S"] = PdfName.Get("Transparency") };
        if (group.Contains("/I true", StringComparison.Ordinal))
        {
            groupDictionary["I"] = PdfBoolean.True;
        }

        if (group.Contains("/K true", StringComparison.Ordinal))
        {
            groupDictionary["K"] = PdfBoolean.True;
        }

        return new PdfDictionary
        {
            ["XObject"] = new PdfDictionary { ["G"] = RenderCoreTests.Form(content, 0, 0, 20, 20, new PdfDictionary { ["Group"] = groupDictionary }) },
            ["ExtGState"] = new PdfDictionary { ["H"] = new PdfDictionary { ["ca"] = new PdfReal(alpha) } },
        };
    }

    private static PdfDictionary MaskResources(string kind, string maskContent, double? backdrop)
    {
        PdfDictionary smask = new()
        {
            ["Type"] = PdfName.Get("Mask"),
            ["S"] = PdfName.Get(kind),
            ["G"] = RenderCoreTests.Form(maskContent, 0, 0, 10, 20, new PdfDictionary { ["Group"] = new PdfDictionary { ["S"] = PdfName.Get("Transparency"), ["CS"] = PdfName.Get("DeviceGray") } }),
        };
        if (backdrop is double bc)
        {
            smask["BC"] = new PdfArray { new PdfReal(bc) };
        }

        if (maskContent.StartsWith("1 g 0 0 10", StringComparison.Ordinal) || maskContent.StartsWith("1 g 0 0 20", StringComparison.Ordinal))
        {
            // These mask groups paint the whole page width.
            smask["G"] = RenderCoreTests.Form(maskContent, 0, 0, 20, 20, new PdfDictionary { ["Group"] = new PdfDictionary { ["S"] = PdfName.Get("Transparency"), ["CS"] = PdfName.Get("DeviceGray") } });
        }

        return new PdfDictionary { ["ExtGState"] = new PdfDictionary { ["S"] = new PdfDictionary { ["SMask"] = smask } } };
    }
}
