using Egap.Documents.Docx;

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
}
