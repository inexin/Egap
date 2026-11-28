using System.Text;

using Egami;
using Egami.Barcodes;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;

namespace Egap.Documents.Tests;

/// <summary>Word's DISPLAYBARCODE and MERGEBARCODE fields, drawn by layout and kept by the formats.</summary>
public sealed class BarcodeFieldTests
{
    private static Document WithField(string code, string result = "")
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.MarginLeft = section.PageSetup.MarginRight = section.PageSetup.MarginTop = section.PageSetup.MarginBottom = 72;
        section.Body.AddParagraph("").AddField(code, result);
        return document;
    }

    private static ParagraphContent Content(Document document) =>
        new ParagraphBridge(new FontProvider(document, FontSettings.Portable), document).Build((Paragraph)document.Sections[0].Body.Blocks[0]);

    private static Raster<Rgba8> Render(Document document, double dpi = 300) =>
        document.Layout(new LayoutOptions { Fonts = FontSettings.Portable }).Pages[0].Render(dpi: dpi);

    [Theory]
    [InlineData("DISPLAYBARCODE \"http://www.example.com\" QR \\q 3", "http://www.example.com", BarcodeFormat.Qr)]
    [InlineData("DISPLAYBARCODE \"https://egap.dev\" QR \\q H \\s 150", "https://egap.dev", BarcodeFormat.Qr)]
    [InlineData("DISPLAYBARCODE \"ORDER-118\" CODE128", "ORDER-118", BarcodeFormat.Code128)]
    [InlineData("DISPLAYBARCODE \"2345678\" CODE39 \\d \\t", "2345678", BarcodeFormat.Code39)]
    [InlineData("DISPLAYBARCODE \"A40156B\" NW7 \\d", "40156", BarcodeFormat.Codabar)]
    [InlineData("DISPLAYBARCODE \"490123456789\" EAN13 \\t", "4901234567894", BarcodeFormat.Ean13)]
    [InlineData("DISPLAYBARCODE \"4901234567890\" JAN13 \\x", "4901234567894", BarcodeFormat.Ean13)]
    [InlineData("DISPLAYBARCODE \"4901234\" JAN8", "49012347", BarcodeFormat.Ean8)]
    [InlineData("DISPLAYBARCODE \"01234565\" EAN8 \\t", "01234565", BarcodeFormat.Ean8)]
    [InlineData("DISPLAYBARCODE \"012345678901\" UPCA \\t \\x", "012345678905", BarcodeFormat.UpcA)]
    [InlineData("DISPLAYBARCODE \"01234565\" UPCE", "01234565", BarcodeFormat.UpcE)]
    [InlineData("DISPLAYBARCODE \"1234567890123\" ITF14 \\c STD", "12345678901231", BarcodeFormat.Itf)]
    [InlineData("DISPLAYBARCODE \"1234567890123\" CASE \\h 1440 \\s 50", "12345678901231", BarcodeFormat.Itf)]
    public void EachTypeDrawsASymbolThatReadsBack(string code, string text, BarcodeFormat format)
    {
        Document document = WithField(code);
        Assert.Equal(FieldKind.DisplayBarcode, document.Descendants<FieldStart>().Single().GetField()!.Kind);

        using Raster<Rgba8> page = Render(document);
        BarcodeResult? read = page.ReadBarcode();
        Assert.NotNull(read);
        Assert.Equal(format, read.Format);
        Assert.Contains(text, read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AJapanPostSymbolHasTheManualsBarsAndSize()
    {
        const string data = "263-0023 3-6-21";
        Document document = WithField($"DISPLAYBARCODE \"{data}\" JPPOST");
        BarcodeField.Symbol symbol = BarcodeField.Parse(FieldCode.Parse(document.Descendants<FieldStart>().Single().GetField()!.Code))!;
        Barcode expected = Barcode.JapanPost(data);
        Assert.Equal(expected.Modules.Width, symbol.Barcode.Modules.Width);

        // 0.6 mm a module: the bars are 3.6 mm tall, as the manual has them.
        (float width, float height) = BarcodeField.Size(symbol, null);
        Assert.Equal(3.6 / 25.4 * 72, (height / (expected.Modules.Height + (2 * expected.QuietZone))) * expected.Modules.Height, 0.01);
        Assert.Equal((expected.Modules.Width + (2 * expected.QuietZone)) * 0.6 / 25.4 * 72, width, 0.01);

        // Each bar crosses the symbol's middle (the tracker row): count the dark runs along it on the page.
        using Raster<Rgba8> page = Render(document, dpi: 600);
        int bars = 0;
        for (int x = 0; x < expected.Modules.Width; x++)
        {
            bars += expected.Modules[x, 2] ? 1 : 0;
        }

        int bestRuns = 0;
        for (int y = 0; y < page.Height / 4; y++)
        {
            ReadOnlySpan<Rgba8> row = page.Row(y);
            int runs = 0;
            bool dark = false;
            foreach (Rgba8 pixel in row)
            {
                bool now = pixel.R < 128;
                runs += now && !dark ? 1 : 0;
                dark = now;
            }

            bestRuns = Math.Max(bestRuns, runs);
        }

        Assert.Equal(bars, bestRuns);
    }

    [Fact]
    public void SwitchesSetSizeRotationAndColours()
    {
        BarcodeField.Symbol plain = BarcodeField.Parse(FieldCode.Parse("DISPLAYBARCODE \"ORDER-118\" CODE128"))!;
        BarcodeField.Symbol scaled = BarcodeField.Parse(FieldCode.Parse("DISPLAYBARCODE \"ORDER-118\" CODE128 \\s 200 \\h 720 \\r 1 \\f 0xFF0000 \\b 0x00FF00"))!;
        Assert.Equal(plain.Module * 2, scaled.Module, 0.001);
        Assert.Equal(72f, scaled.BarHeight!.Value, 0.001);
        Assert.Equal(1, scaled.Rotation);
        Assert.Equal(1f, scaled.Foreground.R);
        Assert.Equal(0f, scaled.Foreground.B);
        Assert.Equal(1f, scaled.Background!.Value.G);

        Document document = WithField("DISPLAYBARCODE \"ORDER-118\" CODE128 \\r 1 \\f 0x800000 \\b 0x00FF00");
        using Raster<Rgba8> page = Render(document);
        Assert.Equal("ORDER-118", page.ReadBarcode()?.Text);
        bool red = false, green = false;
        for (int y = 0; y < page.Height && !(red && green); y++)
        {
            foreach (Rgba8 pixel in page.Row(y))
            {
                red |= pixel is { R: > 100, G: < 40, B: < 40 };
                green |= pixel is { R: < 60, G: > 200, B: < 60 };
            }
        }

        Assert.True(red && green);
    }

    [Fact]
    public void AQrCodeHeightSetsItsSide()
    {
        BarcodeField.Symbol symbol = BarcodeField.Parse(FieldCode.Parse("DISPLAYBARCODE \"abc\" QR \\h 1440"))!;
        (float width, float height) = BarcodeField.Size(symbol, null);
        Assert.Equal(72, height, 0.01);
        Assert.Equal(72, width, 0.01);
    }

    [Fact]
    public void DataTheTypeCannotHoldKeepsTheStoredResult()
    {
        Assert.Null(BarcodeField.Parse(FieldCode.Parse("DISPLAYBARCODE \"ABC\" EAN13")));
        Assert.Null(BarcodeField.Parse(FieldCode.Parse("DISPLAYBARCODE \"4901234567890\" EAN13")));
        Assert.Null(BarcodeField.Parse(FieldCode.Parse("DISPLAYBARCODE \"x\" POSTNET")));
        Document document = WithField("DISPLAYBARCODE \"ABC\" EAN13", "Error! Not a valid barcode.");
        Assert.Empty(Content(document).InlineObjects);
    }

    [Fact]
    public void AStoredResultIsReplacedByTheSymbol()
    {
        Document document = WithField("DISPLAYBARCODE \"ORDER-118\" CODE128 \\t", "stale text");
        Assert.IsType<Picture>(Assert.Single(Content(document).InlineObjects));
        using Raster<Rgba8> page = Render(document);
        Assert.Equal("ORDER-118", page.ReadBarcode()?.Text);
    }

    [Fact]
    public void DocxRtfAndPdfKeepOrDrawTheField()
    {
        const string code = " DISPLAYBARCODE \"https://egap.dev\" QR \\q 3 ";
        Document document = WithField(code);

        using MemoryStream docx = new();
        DocxFormat.Save(document, docx);
        string xml = Encoding.UTF8.GetString(Docx.OpcPackage.Read(new MemoryStream(docx.ToArray()), new DocumentLimits()).GetPart("/word/document.xml")!.Data);
        Assert.Contains("DISPLAYBARCODE", xml, StringComparison.Ordinal);
        docx.Position = 0;
        Document fromDocx = DocxFormat.Load(docx);
        Assert.Equal(FieldKind.DisplayBarcode, fromDocx.Descendants<FieldStart>().Single().GetField()!.Kind);
        using (Raster<Rgba8> page = Render(fromDocx))
        {
            Assert.Equal("https://egap.dev", page.ReadBarcode()?.Text);
        }

        using MemoryStream rtf = new();
        RtfFormat.Save(document, rtf);
        Assert.Contains("DISPLAYBARCODE", Encoding.ASCII.GetString(rtf.ToArray()), StringComparison.Ordinal);
        rtf.Position = 0;
        Document fromRtf = RtfFormat.Load(rtf);
        using (Raster<Rgba8> page = Render(fromRtf))
        {
            Assert.Equal("https://egap.dev", page.ReadBarcode()?.Text);
        }

        using MemoryStream pdf = new();
        document.SaveAsPdf(pdf, new PdfExportOptions { Layout = new LayoutOptions { Fonts = FontSettings.Portable } });
        using Egap.Pdf.PdfDocument opened = Egap.Pdf.PdfDocument.Open(pdf.ToArray());
        using Raster<Rgba8> rendered = opened.Pages[0].Render(new Egap.Pdf.PdfRenderOptions { Dpi = 300 }, TestContext.Current.CancellationToken);
        Assert.Equal("https://egap.dev", rendered.ReadBarcode()?.Text);
    }

    [Fact]
    public void AMergeBarcodeBecomesADisplayBarcodeOfItsValue()
    {
        Document template = WithField(" MERGEBARCODE Code QR \\q 2 \\s 120 ");
        Assert.Equal(FieldKind.MergeBarcode, template.Descendants<FieldStart>().Single().GetField()!.Kind);

        Document letter = template.MailMerge([MergeData.From(new Dictionary<string, object?> { ["Code"] = "https://egap.dev/\"quoted\"" })]).Single();
        Field field = letter.Descendants<FieldStart>().Single().GetField()!;
        Assert.Equal(FieldKind.DisplayBarcode, field.Kind);
        FieldCode code = FieldCode.Parse(field.Code);
        Assert.Equal(["https://egap.dev/\"quoted\"", "QR"], code.Arguments);
        Assert.Equal("2", code.Switch("\\Q"));
        Assert.Equal("120", code.Switch("\\S"));
        using Raster<Rgba8> page = Render(letter);
        Assert.Equal("https://egap.dev/\"quoted\"", page.ReadBarcode()?.Text);

        Document empty = template.MailMerge([MergeData.From(new Dictionary<string, object?>())]).Single();
        Assert.Empty(empty.Descendants<FieldStart>());
    }
}
