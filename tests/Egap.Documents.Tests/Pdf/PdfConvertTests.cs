using Egami;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf;

/// <summary>One-line conversions and HTML stamps.</summary>
public sealed class PdfConvertTests : IDisposable
{
    private static readonly PdfConvertOptions Isolated = new()
    {
        Export = new PdfExportOptions { Layout = new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false } } },
    };

    private readonly string folder = Path.Combine(Path.GetTempPath(), "egap-convert-" + Guid.NewGuid().ToString("N"));

    public PdfConvertTests()
    {
        Directory.CreateDirectory(folder);
    }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void DocxAndImageFilesConvert()
    {
        Document word = new();
        word.AddSection().Body.AddParagraph("From Word");
        string docx = Path.Combine(folder, "letter.docx");
        DocxFormat.Save(word, docx);
        string png = Path.Combine(folder, "scan.png");
        File.WriteAllBytes(png, Png());

        using PdfDocument fromWord = Reopen(PdfConvert.FromFile(docx, Isolated with { Conformance = PdfAConformance.PdfA2b }));
        Assert.Contains("From Word", fromWord.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Equal(PdfAConformance.PdfA2b, fromWord.Conformance);

        using PdfDocument fromImage = PdfConvert.FromFile(png, Isolated with { PageSize = PdfPageSize.A4 });
        Assert.Equal(PdfPageSize.A4.Width, fromImage.Pages[0].MediaBox.Right, 1);
        using PdfDocument fromImages = PdfConvert.FromImages([png, png]);
        Assert.Equal(2, fromImages.Pages.Count);
    }

    [Fact]
    public void HtmlBandsAndFilesNameTheHtmlPackageWhenItIsMissing()
    {
        Document word = new();
        word.AddSection().Body.AddParagraph("From Word");
        string docx = Path.Combine(folder, "banded.docx");
        DocxFormat.Save(word, docx);
        string html = Path.Combine(folder, "page.html");
        File.WriteAllText(html, "<p>x</p>");

        NotSupportedException band = Assert.Throws<NotSupportedException>(() => PdfConvert.FromFile(docx, Isolated with { Header = PageBand.Html("<b>Head</b>") }));
        NotSupportedException file = Assert.Throws<NotSupportedException>(() => PdfConvert.FromFile(html, Isolated));

        Assert.Contains("HtmlFormat.Register()", band.Message, StringComparison.Ordinal);
        Assert.Contains("Egap.Documents.Html", file.Message, StringComparison.Ordinal);
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using (pdf)
        {
            using MemoryStream saved = new();
            pdf.Save(saved);
            return PdfDocument.Open(saved.ToArray());
        }
    }

    private static byte[] Png()
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(8, 8, new Rgb8(0, 128, 0));
        using MemoryStream png = new();
        raster.Save(png, "png");
        return png.ToArray();
    }
}
