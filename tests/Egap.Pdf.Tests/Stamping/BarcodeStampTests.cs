using Egami;
using Egami.Barcodes;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Stamping;

/// <summary>Barcode stamps: every symbology Egami.Barcodes makes, drawn as vectors, read back from the rendered page.</summary>
public sealed class BarcodeStampTests
{
    public static TheoryData<string> Symbologies => ["qr", "datamatrix", "pdf417", "aztec", "code128", "code39", "itf", "ean13", "ean8", "upca", "upce"];

    [Theory]
    [MemberData(nameof(Symbologies))]
    public void EverySymbologyReadsBackAtThreeSizes(string name)
    {
        (Barcode barcode, string text, bool linear) = Make(name);
        foreach (double size in (double[])[1, 2, 4])
        {
            (double width, double height) = linear ? (110 * size, 40 * size) : name == "pdf417" ? (150 * size, 50 * size) : (40 * size, 40 * size);
            using PdfDocument pdf = PdfDocument.Create();
            pdf.Pages.AddBlank();
            PdfStampResult result = pdf.Stamp(PdfStamp.Barcode(barcode, width, height) with { Anchor = StampAnchor.Center });

            Assert.Equal(1, result.Pages);
            using Raster<Rgba8> page = pdf.Pages[0].Render(new PdfRenderOptions { Dpi = 200 }, TestContext.Current.CancellationToken);
            BarcodeResult? read = page.ReadBarcode();
            Assert.True(read is not null, $"{name} at {size}x not read");
            Assert.Equal(text, read.Text);
        }
    }

    [Fact]
    public void TheStampIsVectorPathsInItsColour()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank();
        pdf.Stamp(PdfStamp.Barcode(Barcode.Qr("https://egap.dev"), 60, 60) with { Color = new Rgba8(0, 0, 128, 255) });

        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        Assert.Empty(reopened.Pages[0].GetImages());
        using Raster<Rgba8> page = reopened.Pages[0].Render(new PdfRenderOptions { Dpi = 150 }, TestContext.Current.CancellationToken);
        Assert.Equal("https://egap.dev", page.ReadBarcode()?.Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStamp.Barcode(Barcode.Qr("x"), 0, 10));
    }

    private static (Barcode, string, bool) Make(string name) => name switch
    {
        "qr" => (Barcode.Qr("Egap 1.5"), "Egap 1.5", false),
        "datamatrix" => (Barcode.DataMatrix("ORDER-118"), "ORDER-118", false),
        "pdf417" => (Barcode.Pdf417("Invoice 2026-118"), "Invoice 2026-118", false),
        "aztec" => (Barcode.Aztec("Ticket 42"), "Ticket 42", false),
        "code128" => (Barcode.Code128("ORDER-118"), "ORDER-118", true),
        "code39" => (Barcode.Code39("EGAP-15"), "EGAP-15", true),
        "itf" => (Barcode.Itf("12345678"), "12345678", true),
        "ean13" => (Barcode.Ean13("7038010069307"), "7038010069307", true),
        "ean8" => (Barcode.Ean8("96385074"), "96385074", true),
        "upca" => (Barcode.UpcA("036000291452"), "036000291452", true),
        _ => (Barcode.UpcE("01234565"), "01234565", true),
    };
}
