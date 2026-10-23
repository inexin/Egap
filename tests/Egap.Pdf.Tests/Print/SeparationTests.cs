using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Print;

/// <summary>Ink plates from the multi-ink renderer, and total ink coverage, on scenes worked out by hand.</summary>
public sealed class SeparationTests
{
    private static PdfSeparations Plates(byte[] file, double dpi = 72, PdfSeparationOptions? options = null)
    {
        using PdfDocument pdf = PdfDocument.Open(new MemoryStream(file));
        return pdf.Pages[0].RenderSeparations(new PdfRenderOptions { Dpi = dpi }, options, TestContext.Current.CancellationToken);
    }

    private static byte At(PdfSeparations plates, string ink, int x, int y) => plates.Plates.Single(p => p.Name == ink).Raster.GetPixel(x, y).L;

    [Fact]
    public void FlatCmykGoesStraightOntoTheProcessPlates()
    {
        using PdfSeparations plates = Plates(PrintSamples.FlatInk().Bytes);

        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black"], plates.Plates.Select(p => p.Name));
        Assert.All(plates.Plates, p => Assert.False(p.IsSpot));
        Assert.Equal(230, At(plates, "Cyan", 10, 50));
        Assert.Equal(204, At(plates, "Magenta", 10, 50));
        Assert.Equal(194, At(plates, "Yellow", 10, 50));
        Assert.Equal(204, At(plates, "Black", 10, 50));
        Assert.Equal(128, At(plates, "Cyan", 80, 80));
        Assert.Equal(0, At(plates, "Black", 80, 20));
    }

    [Fact]
    public void CoverageIsTheSumOfThePlates()
    {
        using PdfSeparations plates = Plates(PrintSamples.FlatInk().Bytes);

        Assert.InRange(plates.MaxCoverage, 325, 327);
        Assert.InRange(plates.ShareOver(300), 0.49, 0.51);
        Assert.Equal(0, plates.ShareOver(330));
        Assert.InRange(plates.CoveragePerPlate["Cyan"], 0.9 * 50 + 0.5 * 25 - 1, 0.9 * 50 + 0.5 * 25 + 1);
        using Raster<Rgba8> map = plates.CoverageMap(300);
        Assert.True(map.GetPixel(10, 50).R > 150 && map.GetPixel(10, 50).G == 0, "over the limit is red");
        Assert.Equal(new Rgba8(255, 255, 255, 255), map.GetPixel(80, 20));
    }

    [Fact]
    public void SpotsGetPlatesOfTheirOwnAndOverprintKeepsWhatIsBelow()
    {
        using PdfSeparations plates = Plates(PrintSamples.Spots().Bytes);

        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "PANTONE 186 C", "Gold"], plates.Plates.Select(p => p.Name));
        Assert.True(plates.Plates.Single(p => p.Name == "Gold").IsSpot);

        // Full spot at (50, 600)-(150, 700) knocks out the process plates; y = 792 - 650 from the top.
        Assert.Equal(255, At(plates, "PANTONE 186 C", 100, 142));
        Assert.Equal(128, At(plates, "PANTONE 186 C", 250, 142));
        Assert.Equal(0, At(plates, "Cyan", 100, 142));

        // The overprinting spot over the 0.2/0.3 CMYK fill at (100, 520)-(200, 580) leaves the CMYK there.
        Assert.Equal(255, At(plates, "PANTONE 186 C", 150, 242));
        Assert.Equal(51, At(plates, "Cyan", 150, 242));
        Assert.InRange(At(plates, "Magenta", 150, 242), 76, 77);

        // DeviceN Cyan 0.5, Magenta 0.2, Gold 1 at (50, 300)-(250, 450).
        Assert.Equal(128, At(plates, "Cyan", 100, 400));
        Assert.Equal(51, At(plates, "Magenta", 100, 400));
        Assert.Equal(255, At(plates, "Gold", 100, 400));

        // The All registration rule paints every plate.
        Assert.All(plates.Plates, p => Assert.True(p.Raster.GetPixel(450, 692).L > 200, p.Name));
    }

    [Fact]
    public void ADisabledSpotPrintsThroughItsAlternate()
    {
        using PdfSeparations plates = Plates(PrintSamples.Spots().Bytes, options: new PdfSeparationOptions { DisabledSpots = ["PANTONE 186 C"] });

        Assert.DoesNotContain(plates.Plates, p => p.Name == "PANTONE 186 C");

        // Its alternate is 0/100/81/4 at full tint.
        Assert.Equal(255, At(plates, "Magenta", 100, 142));
        Assert.Equal(207, At(plates, "Yellow", 100, 142));
        Assert.Equal(10, At(plates, "Black", 100, 142));
    }

    [Fact]
    public void SpotsToProcessMakesOnlyProcessPlates()
    {
        using PdfSeparations plates = Plates(PrintSamples.Spots().Bytes, options: new PdfSeparationOptions { SpotsToProcess = true });

        Assert.Equal(4, plates.Plates.Count);
        Assert.Equal(255, At(plates, "Magenta", 100, 142));
    }

    [Fact]
    public void RgbConvertsThroughThePressProfileAndGrayIsBlackInk()
    {
        using PdfSeparations plates = Plates(PrintSamples.Mixed().Bytes);

        // Page 1: 100 % K, then 50 % gray as black ink only.
        Assert.Equal(255, At(plates, "Black", 100, 150));
        Assert.Equal(0, At(plates, "Cyan", 100, 150));
        Assert.InRange(At(plates, "Black", 100, 350), 127, 128);
        Assert.Equal(0, At(plates, "Cyan", 100, 350));

        using PdfDocument pdf = PdfDocument.Open(new MemoryStream(PrintSamples.Mixed().Bytes));
        using PdfSeparations rgb = pdf.Pages[1].RenderSeparations(cancellationToken: TestContext.Current.CancellationToken);

        // Pure red through FOGRA39 is mostly magenta and yellow.
        Assert.True(At(rgb, "Magenta", 100, 150) > 200 && At(rgb, "Yellow", 100, 150) > 200 && At(rgb, "Cyan", 100, 150) < 30);
    }

    [Fact]
    public void TheOutputIntentNamesTheProcessInksAndTransparencyBlendsOnThePlates()
    {
        using (PdfSeparations affinity = Plates(PrintSamples.Affinity().Bytes, 36))
        {
            Assert.Equal(4, affinity.Plates.Count);
            Assert.InRange(affinity.MaxCoverage, 300, 330);
        }

        // Multiply of 100 % magenta over a 50 % alpha cyan square over 50 % K: the plates add, as on press.
        using PdfSeparations plates = Plates(PrintSamples.Transparency().Bytes);
        Assert.Equal(128, At(plates, "Black", 60, 300));
        Assert.Equal(128, At(plates, "Cyan", 120, 300));
        Assert.Equal(255, At(plates, "Magenta", 250, 200));
        Assert.Equal(128, At(plates, "Cyan", 250, 200));
    }

    [Fact]
    public void SpotsInFunctionShadingsAndPatternStrokesAndTextKeepTheirPlate()
    {
        using PdfSeparations plates = Plates(PrintSamples.PlateGaps());

        Assert.Contains(plates.Plates, p => p.Name == "Gold");

        // The function shading's tint is x over its domain: about half at x = 150, y = 792 - 650; no process ink.
        Assert.InRange(At(plates, "Gold", 150, 142), 120, 136);
        Assert.InRange(At(plates, "Gold", 240, 142), 235, 255);
        Assert.Equal(0, At(plates, "Magenta", 150, 142));
        Assert.Equal(0, At(plates, "Yellow", 240, 142));

        // The pattern stroke at y = 792 - 400.
        Assert.Equal(255, At(plates, "Gold", 150, 392));
        Assert.Equal(0, At(plates, "Magenta", 150, 392));
        Assert.Equal(0, At(plates, "Yellow", 150, 392));

        // The pattern-filled "H": Gold where it covers, and no process ink anywhere in its box.
        int gold = 0, process = 0;
        for (int y = 792 - 210; y < 792 - 150; y++)
        {
            for (int x = 50; x < 110; x++)
            {
                gold = Math.Max(gold, At(plates, "Gold", x, y));
                process = Math.Max(process, Math.Max(At(plates, "Magenta", x, y), At(plates, "Yellow", x, y)));
            }
        }

        Assert.Equal(255, gold);
        Assert.Equal(0, process);
    }
}
