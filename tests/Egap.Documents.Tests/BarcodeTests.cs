using Egami;
using Egami.Barcodes;
using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Barcode pictures in documents: drawn as vectors, kept for Word with a PNG stand-in.</summary>
public sealed class BarcodeTests
{
    [Fact]
    public void ABarcodePictureLaysOutAndReadsBack()
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        builder.Writeln("Order");
        Picture picture = builder.InsertBarcode(Barcode.Code128("ORDER-118"), 200, 60);

        Assert.Equal("image/svg+xml", picture.Image.ContentType);
        Assert.Equal("image/png", picture.Fallback!.ContentType);
        using Raster<Rgba8> page = document.Layout(new LayoutOptions { Fonts = FontSettings.Portable }).Pages[0].Render(dpi: 200);
        Assert.Equal("ORDER-118", page.ReadBarcode()?.Text);

        using Raster<Rgba8> fallback = Raster.Load<Rgba8>(new MemoryStream(picture.Fallback.Bytes.ToArray()));
        Assert.Equal("ORDER-118", fallback.ReadBarcode()?.Text);
    }

    [Fact]
    public void DocxKeepsTheSvgWithItsPngStandIn()
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        builder.InsertBarcode(Barcode.Qr("https://egap.dev"), 72, 72);

        using MemoryStream saved = new();
        DocxFormat.Save(document, saved);
        string xml = System.Text.Encoding.UTF8.GetString(Docx.OpcPackage.Read(new MemoryStream(saved.ToArray()), new DocumentLimits()).GetPart("/word/document.xml")!.Data);
        Assert.Contains("svgBlip", xml, StringComparison.Ordinal);

        saved.Position = 0;
        Picture back = DocxFormat.Load(saved).Descendants<Picture>().Single();
        Assert.Equal("image/svg+xml", back.Image.ContentType);
        Assert.Equal("image/png", back.Fallback?.ContentType);
        Assert.Equal("image/png", back.Clone().Fallback?.ContentType);
    }
}
