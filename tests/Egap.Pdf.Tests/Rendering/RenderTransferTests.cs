using System.Text;

using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Transfer functions: ExtGState /TR and /TR2 on fills, images and shadings, on plates, and turned off.</summary>
public sealed class RenderTransferTests
{
    [Fact]
    public void OneFunctionChangesEveryComponent()
    {
        using Raster<Rgba8> raster = RenderCoreTests.Render("/T gs 0.2 0.4 0.6 rg 0 0 20 20 re f", resources: State(new PdfDictionary { ["TR"] = Invert() }));

        AssertNear(new Rgba8(204, 153, 102, 255), raster.GetPixel(10, 10));
    }

    [Fact]
    public void FourFunctionsChangeTheirOwnComponentsAndIdentityLeavesOne()
    {
        PdfArray four = [Invert(), PdfName.Get("Identity"), Invert(), PdfName.Get("Identity")];
        using Raster<Rgba8> raster = RenderCoreTests.Render("/T gs 0.2 0.4 0.6 rg 0 0 20 20 re f", resources: State(new PdfDictionary { ["TR"] = four }));

        AssertNear(new Rgba8(204, 102, 102, 255), raster.GetPixel(10, 10));
    }

    [Fact]
    public void Tr2TakesThePlaceOfTrAndDefaultChangesNothing()
    {
        PdfDictionary resources = new()
        {
            ["ExtGState"] = new PdfDictionary
            {
                ["A"] = new PdfDictionary { ["TR"] = Invert(), ["TR2"] = Power(2) },
                ["B"] = new PdfDictionary { ["TR"] = Invert(), ["TR2"] = PdfName.Get("Default") },
                ["C"] = new PdfDictionary { ["TR"] = Invert() },
                ["I"] = new PdfDictionary { ["TR"] = PdfName.Get("Identity") },
            },
        };
        using Raster<Rgba8> raster = RenderCoreTests.Render(
            "q /A gs 0.5 g 0 0 5 20 re f Q q /B gs 0.5 g 5 0 5 20 re f Q q /C gs /I gs 0.5 g 10 0 5 20 re f Q 0.5 g 15 0 5 20 re f", resources: resources);

        AssertNear(new Rgba8(64, 64, 64, 255), raster.GetPixel(2, 10));
        AssertNear(new Rgba8(128, 128, 128, 255), raster.GetPixel(7, 10));
        AssertNear(new Rgba8(128, 128, 128, 255), raster.GetPixel(12, 10));
        AssertNear(new Rgba8(128, 128, 128, 255), raster.GetPixel(17, 10));
    }

    [Fact]
    public void ImagesAndShadingsGoThroughTheFunctionToo()
    {
        PdfStream image = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Image"),
            ["Width"] = new PdfInteger(1),
            ["Height"] = new PdfInteger(1),
            ["ColorSpace"] = PdfName.Get("DeviceRGB"),
            ["BitsPerComponent"] = new PdfInteger(8),
        });
        image.SetDecodedBytes([51, 102, 153]);
        PdfDictionary shading = new()
        {
            ["ShadingType"] = new PdfInteger(2),
            ["ColorSpace"] = PdfName.Get("DeviceGray"),
            ["Coords"] = new PdfArray { new PdfReal(0), new PdfReal(0), new PdfReal(20), new PdfReal(0) },
            ["Function"] = new PdfDictionary { ["FunctionType"] = new PdfInteger(2), ["Domain"] = Numbers(0, 1), ["C0"] = Numbers(0.25), ["C1"] = Numbers(0.25), ["N"] = new PdfInteger(1) },
        };
        PdfDictionary resources = State(new PdfDictionary { ["TR"] = Invert() });
        resources["XObject"] = new PdfDictionary { ["Im"] = RenderCoreTests.Add(image) };
        resources["Shading"] = new PdfDictionary { ["Sh"] = RenderCoreTests.Add(shading) };
        using Raster<Rgba8> raster = RenderCoreTests.Render("/T gs q 10 0 0 20 0 0 cm /Im Do Q q 10 0 20 20 re W n /Sh sh Q", resources: resources);

        AssertNear(new Rgba8(204, 153, 102, 255), raster.GetPixel(5, 10));
        AssertNear(new Rgba8(191, 191, 191, 255), raster.GetPixel(15, 10));
    }

    [Fact]
    public void TurningThemOffDrawsColoursAsTheyAre()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, "/T gs 0.2 0.4 0.6 rg 0 0 10 10 re f");

        using Raster<Rgba8> applied = page.Render();
        using Raster<Rgba8> ignored = page.Render(new PdfRenderOptions { ApplyTransferFunctions = false });

        AssertNear(new Rgba8(204, 153, 102, 255), applied.GetPixel(5, 5));
        AssertNear(new Rgba8(51, 102, 153, 255), ignored.GetPixel(5, 5));
    }

    [Fact]
    public void OnPlatesATintGoesThroughItsPlatesFunctionAsAnAdditiveValue()
    {
        using PdfDocument pdf = PdfDocument.Create();

        // The function maps x to 1 - x on additive values, so a 25 % cyan tint prints as 1 - f(0.75) = 75 %.
        PdfPage page = Page(pdf, "/T gs 0.25 0 0 0 k 0 0 10 10 re f");

        using PdfSeparations applied = page.RenderSeparations(cancellationToken: TestContext.Current.CancellationToken);
        using PdfSeparations ignored = page.RenderSeparations(new PdfRenderOptions { ApplyTransferFunctions = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.InRange(applied.CoveragePerPlate["Cyan"], 74, 76);
        Assert.InRange(ignored.CoveragePerPlate["Cyan"], 24, 26);
    }

    [Fact]
    public void AFunctionThatCannotBeReadIsReportedAndLeavesColoursAsTheyAre()
    {
        List<PdfWarning> warnings = [];
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 10, 10));
        page.Dictionary["Resources"] = State(new PdfDictionary { ["TR"] = new PdfArray { Invert() } });
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes("/T gs 0.2 0.4 0.6 rg 0 0 10 10 re f 0 0 5 5 re f")));

        using Raster<Rgba8> raster = page.Render(new PdfRenderOptions { OnWarning = warnings.Add });

        AssertNear(new Rgba8(51, 102, 153, 255), raster.GetPixel(7, 7));
        Assert.Single(warnings, w => w.Message.Contains("transfer function", StringComparison.Ordinal));
    }

    private static PdfPage Page(PdfDocument pdf, string content)
    {
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 10, 10));
        page.Dictionary["Resources"] = State(new PdfDictionary { ["TR"] = Invert() });
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(content)));
        return page;
    }

    private static PdfDictionary State(PdfDictionary state)
    {
        return new PdfDictionary { ["ExtGState"] = new PdfDictionary { ["T"] = state } };
    }

    /// <summary>1 - x.</summary>
    private static PdfDictionary Invert()
    {
        return new PdfDictionary { ["FunctionType"] = new PdfInteger(2), ["Domain"] = Numbers(0, 1), ["C0"] = Numbers(1), ["C1"] = Numbers(0), ["N"] = new PdfInteger(1) };
    }

    /// <summary>x to the power <paramref name="n"/>.</summary>
    private static PdfDictionary Power(double n)
    {
        return new PdfDictionary { ["FunctionType"] = new PdfInteger(2), ["Domain"] = Numbers(0, 1), ["N"] = new PdfReal(n) };
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

    private static void AssertNear(Rgba8 expected, Rgba8 actual)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= 2 && Math.Abs(expected.G - actual.G) <= 2 && Math.Abs(expected.B - actual.B) <= 2 && expected.A == actual.A, $"{actual} against {expected}");
    }
}
