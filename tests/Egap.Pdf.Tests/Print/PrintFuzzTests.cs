namespace Egap.Pdf.Tests.Print;

/// <summary>Inputs the print fuzz targets found: separations, preflight and conversion must not throw on them.</summary>
public sealed class PrintFuzzTests
{
    [Theory]
    [InlineData("empty-spot-name.pdf")]
    [InlineData("tall-image.pdf")]
    [InlineData("calgray-gamma-object.pdf")]
    public void HostileFilesSeparatePreflightAndConvert(string name)
    {
        using PdfDocument pdf = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "files", "fuzz", name));
        foreach (PdfPage page in pdf.Pages.Take(3))
        {
            try
            {
                using PdfSeparations plates = page.RenderSeparations(new PdfRenderOptions { Dpi = 9 }, cancellationToken: TestContext.Current.CancellationToken);
            }
            catch (PdfLimitException)
            {
                // A page or image past a limit is refused, as it should be.
            }
        }

        _ = PdfPreflight.Run(pdf, PdfPrintProfile.SheetfedCoated with { InkCoverageDpi = 6 }, TestContext.Current.CancellationToken);
        _ = PdfColorConverter.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);
    }
}
