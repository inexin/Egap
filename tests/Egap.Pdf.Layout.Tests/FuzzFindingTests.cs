using Egap.Documents;

namespace Egap.Pdf.Layout.Tests;

/// <summary>Files the layout fuzz target (Build Plan task 17) found, each kept as a test.</summary>
public sealed class FuzzFindingTests
{
    /// <summary>A running footer set partly below the page's bottom edge gave a negative footer distance.</summary>
    [Fact]
    public void AFooterBelowThePageEdgeConverts()
    {
        using PdfDocument pdf = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "files", "fuzz", "layout-footer-below-page.pdf"));

        Egap.Documents.Document model = PdfToDocument.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(model.Sections, s => Assert.True(s.PageSetup.FooterDistance is null or >= 0));
        DocxFormat.Save(model, Stream.Null);
    }

    /// <summary>A path with coordinates too large for single precision made the renderer throw while drawing a figure.</summary>
    [Fact]
    public void APathTooLargeToDrawIsLeftOut()
    {
        using PdfDocument pdf = PdfDocument.Open(Path.Combine(AppContext.BaseDirectory, "files", "fuzz", "layout-huge-path.pdf"));

        foreach (ReconstructionMode mode in (ReconstructionMode[])[ReconstructionMode.Flow, ReconstructionMode.Exact])
        {
            DocxFormat.Save(PdfToDocument.Convert(pdf, new PdfToDocumentOptions { Mode = mode }, TestContext.Current.CancellationToken), Stream.Null);
        }

        using Egami.Raster<Egami.Pixels.Rgba8> page = pdf.Pages[0].Render(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(page.Width > 0);
    }
}
