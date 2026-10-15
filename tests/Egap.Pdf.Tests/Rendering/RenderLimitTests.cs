using System.Text;

using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>Limits, warnings, cancellation and strict mode (Phase 4 task 17).</summary>
public sealed class RenderLimitTests
{
    private static PdfPage Page(PdfDocument pdf, string content)
    {
        PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, 20, 20));
        page.SetContent(PdfContent.Parse(Encoding.ASCII.GetBytes(content)));
        return page;
    }

    [Fact]
    public void ProblemsAreReportedAndStrictModeThrowsAtTheFirst()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, "/Missing Do 0 0 10 10 re f");
        List<PdfWarning> warnings = [];
        using Raster<Rgba8> raster = page.Render(new PdfRenderOptions { OnWarning = warnings.Add });

        Assert.Contains(warnings, w => w.Message.Contains("/Missing", StringComparison.Ordinal));
        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(5, 15));
        Assert.Throws<PdfException>(() => page.Render(new PdfRenderOptions { Strict = true }));
    }

    [Fact]
    public void TooManyOperatorsLeaveTheRestOut()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, "0 0 10 10 re f 1 0 0 rg 10 0 10 10 re f");
        List<PdfWarning> warnings = [];
        using Raster<Rgba8> raster = page.Render(new PdfRenderOptions { MaxOperations = 2, OnWarning = warnings.Add });

        Assert.Equal(new Rgba8(0, 0, 0, 255), raster.GetPixel(5, 15));
        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(15, 15));
        Assert.Single(warnings, w => w.Message.Contains("content operators", StringComparison.Ordinal));
        PdfLimitException e = Assert.Throws<PdfLimitException>(() => page.Render(new PdfRenderOptions { MaxOperations = 2, Strict = true }));
        Assert.Equal("MaxOperations", e.Limit);
    }

    [Fact]
    public void OutputsPastTheLimitThrowBeforeAllocating()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, "");

        PdfLimitException e = Assert.Throws<PdfLimitException>(() => page.Render(new PdfRenderOptions { Dpi = 7200, MaxOutputPixels = 1_000_000 }));
        Assert.Equal("MaxOutputPixels", e.Limit);
        using Raster<Rgba8> tile = page.Render(new PdfRenderOptions { Dpi = 7200, MaxOutputPixels = 1_000_000, Region = new Egami.Drawing.RectF(0, 0, 500, 500) });
        Assert.Equal(500, tile.Width);
    }

    [Fact]
    public void PagesTooLargeForAnyRasterAreALimitNotAnArgument()
    {
        // Found by the render fuzz target: a media box (or user unit) that makes the page miles wide.
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, "0 0 10 10 re f");
        page.MediaBox = new PdfRectangle(0, 0, 1e30, 20);

        Assert.Throws<PdfLimitException>(() => page.Render());
        Assert.Throws<PdfLimitException>(() => page.ToPicture());
    }

    [Fact]
    public void AnEmptyColourSpaceArrayDrawsInTheDefault()
    {
        // Found by the render fuzz target: /CS0 [] in the resources threw from the walker.
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, "/CS0 cs 0 0 10 10 re f");
        page.Resources = new PdfDictionary { ["ColorSpace"] = new PdfDictionary { ["CS0"] = new PdfArray() } };

        using Raster<Rgba8> raster = page.Render();
        Assert.Equal(20, raster.Width);
    }

    [Fact]
    public void LargeImagesAreLeftOut()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, "q 20 0 0 20 0 0 cm BI /W 4 /H 4 /CS /G /BPC 8 ID 0000000000000000 EI Q");
        List<PdfWarning> warnings = [];
        using Raster<Rgba8> raster = page.Render(new PdfRenderOptions { MaxImagePixels = 15, OnWarning = warnings.Add });

        Assert.Equal(new Rgba8(255, 255, 255, 255), raster.GetPixel(10, 10));
        Assert.Contains(warnings, w => w.Message.Contains("past the limit", StringComparison.Ordinal));
    }

    [Fact]
    public void ARenderCanBeCancelled()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = Page(pdf, "0 0 10 10 re f");
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => page.Render(cancellationToken: cancelled.Token));
    }
}
