using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Print;

/// <summary>Overprint preview: the page as it prints, proofed from its plates.</summary>
public sealed class OverprintPreviewTests
{
    [Fact]
    public void OverprintedYellowOverCyanShowsGreen()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Resources = new PdfDictionary { ["ExtGState"] = new PdfDictionary { ["OP"] = new PdfDictionary { ["op"] = PdfBoolean.True, ["OP"] = PdfBoolean.True, ["OPM"] = new PdfInteger(1) } } };
        page.SetContent(PdfContent.Parse("1 0 0 0 k 0 0 200 200 re f /OP gs 0 0 1 0 k 50 50 100 100 re f"u8));

        using Raster<Rgba8> screen = page.Render(new PdfRenderOptions { Dpi = 36 }, TestContext.Current.CancellationToken);
        using Raster<Rgba8> press = page.Render(new PdfRenderOptions { Dpi = 36, SimulateOverprint = true }, TestContext.Current.CancellationToken);

        // At (50, 346) from the top: the yellow square. On screen yellow knocks out the cyan; on press both inks print.
        Rgba8 shown = screen.GetPixel(50, 346), printed = press.GetPixel(50, 346);
        Assert.True(shown.R > 200 && shown.G > 200 && shown.B < 80, $"screen {shown}");
        Assert.True(printed.R < 80 && printed.G > 120 && printed.B < 120, $"press {printed}");
        Assert.Equal(255, printed.A);

        // Away from the overprint both agree: cyan.
        Rgba8 a = screen.GetPixel(10, 390), b = press.GetPixel(10, 390);
        Assert.True(Math.Abs(a.R - b.R) < 25 && Math.Abs(a.G - b.G) < 25 && Math.Abs(a.B - b.B) < 25, $"{a} against {b}");
    }

    [Fact]
    public void OverprintedSpotsAddToTheInksBelow()
    {
        using PdfDocument pdf = PdfDocument.Open(new MemoryStream(PrintSamples.Spots().Bytes));
        PdfPage page = pdf.Pages[0];

        using Raster<Rgba8> screen = page.Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        using Raster<Rgba8> press = page.Render(new PdfRenderOptions { Dpi = 72, SimulateOverprint = true }, TestContext.Current.CancellationToken);

        // The overprinting PANTONE 186 C square over the 0.2/0.3 CMYK fill prints darker than the spot alone.
        Rgba8 shown = screen.GetPixel(150, 242), printed = press.GetPixel(150, 242);
        Assert.True(printed.R + printed.G + printed.B < shown.R + shown.G + shown.B - 5 && printed.R < shown.R - 10, $"screen {shown}, press {printed}");
        Assert.Equal(screen.Width, press.Width);
        Assert.Equal(screen.Height, press.Height);
    }
}
