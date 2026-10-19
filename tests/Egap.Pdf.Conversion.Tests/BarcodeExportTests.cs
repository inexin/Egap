using Egami;
using Egami.Barcodes;
using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Pdf.Conversion.Tests;

/// <summary>A barcode picture exported to PDF stays vector paths and reads back.</summary>
public sealed class BarcodeExportTests
{
    [Fact]
    public void ABarcodePictureExportsAsVectors()
    {
        Document document = new();
        new DocumentBuilder(document).InsertBarcode(Barcode.Ean13("7038010069307"), 150, 60);

        using PdfDocument pdf = document.ToPdf(new PdfExportOptions { Layout = new LayoutOptions { Fonts = FontSettings.Portable } });
        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());

        Assert.Empty(reopened.Pages[0].GetImages());
        using Raster<Rgba8> page = reopened.Pages[0].Render(new PdfRenderOptions { Dpi = 200 }, TestContext.Current.CancellationToken);
        Assert.Equal("7038010069307", page.ReadBarcode()?.Text);
    }
}
