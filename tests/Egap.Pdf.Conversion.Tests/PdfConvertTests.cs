using Egami;
using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Pdf.Conversion.Tests;

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
    public void HtmlBecomesAPdfOnTheChosenPage()
    {
        using PdfDocument pdf = Reopen(PdfConvert.FromHtml("<h1>Invoice</h1><p>Total: 120 EUR</p>", Isolated with
        {
            PageSize = PdfPageSize.A5,
            Margins = PageMargins.All(Length.Mm(30)),
        }));

        PdfPage page = Assert.Single(pdf.Pages);
        Assert.Equal(PdfPageSize.A5.Width, page.MediaBox.Right, 1);
        Assert.Equal(PdfPageSize.A5.Height, page.MediaBox.Top, 1);
        Assert.Contains("Total: 120 EUR", page.ExtractText(), StringComparison.Ordinal);
        PdfTextLine heading = page.GetText().Lines.First(l => l.Text.Contains("Invoice", StringComparison.Ordinal));
        Assert.InRange(heading.Box.Left, Length.Mm(30).Points, Length.Mm(30).Points + 7);   // the body's own 8px margin
    }

    [Fact]
    public void MarkdownAndFilesConvertToPdfFiles()
    {
        string md = Path.Combine(folder, "notes.md");
        File.WriteAllText(md, "# Notes\n\n| A | B |\n| - | - |\n| 1 | 2 |\n\n![logo](logo.png)\n");
        File.WriteAllBytes(Path.Combine(folder, "logo.png"), Png());
        string output = Path.Combine(folder, "notes.pdf");

        PdfConvert.FromFile(md, output, Isolated);

        using PdfDocument pdf = PdfDocument.Open(output);
        Assert.Contains("Notes", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Single(pdf.Pages[0].GetImages());

        using PdfDocument fromText = Reopen(PdfConvert.FromMarkdown("*Hello*", Isolated));
        Assert.Contains("Hello", fromText.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlFilesReadImagesOnlyFromTheirOwnFolder()
    {
        string inner = Path.Combine(folder, "site");
        Directory.CreateDirectory(inner);
        File.WriteAllBytes(Path.Combine(inner, "in.png"), Png());
        File.WriteAllBytes(Path.Combine(folder, "out.png"), Png());
        string html = Path.Combine(inner, "page.html");
        File.WriteAllText(html, "<p><img src='in.png'></p><p><img src='../out.png'></p>");

        using PdfDocument pdf = PdfConvert.FromFile(html, Isolated);
        Assert.Single(pdf.Pages[0].GetImages());

        using PdfDocument noFolder = PdfConvert.FromHtml("<img src='in.png'>", Isolated);
        Assert.Empty(noFolder.Pages[0].GetImages());
        using PdfDocument withFolder = PdfConvert.FromHtml("<img src='in.png'>", Isolated with { BaseDirectory = inner });
        Assert.Single(withFolder.Pages[0].GetImages());
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
    public void HtmlStampsDrawLaidOutHtmlAtTheAnchor()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank(PdfPageSize.A4.ToRectangle());
        pdf.Pages.AddBlank(PdfPageSize.A4.ToRectangle());

        PdfStamp stamp = PdfStamp.Html("<p style='font-size:20pt;color:#c00'>PAID</p>", Length.Mm(60), Isolated) with { Anchor = StampAnchor.TopLeft };
        PdfStampResult result = pdf.Stamp(stamp);

        Assert.Equal(2, result.Pages);
        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        PdfTextLine line = Assert.Single(reopened.Pages[1].GetText().Lines);
        Assert.Equal("PAID", line.Text.Trim());
        Assert.InRange(line.Box.Left, 0, 10);
        Assert.InRange(line.Box.Top, PdfPageSize.A4.Height - 40, PdfPageSize.A4.Height);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStamp.Html("x", 0));
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
