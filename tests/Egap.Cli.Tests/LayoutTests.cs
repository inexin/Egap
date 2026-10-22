using System.Text;

using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class LayoutTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void OptimizeEqualsTheLibraryCall()
    {
        MakePdf("a.pdf", 2);
        Assert.Equal(ExitCodes.Success, _tool.Run("optimize", "a.pdf", "--preset", "ebook", "--subset-fonts", "false", "-o", "out.pdf", "--deterministic").Code);
        using PdfDocument expected = PdfDocument.Open(_tool.PathOf("a.pdf"));
        expected.Optimize(PdfOptimizeOptions.Ebook with { SubsetFonts = false }, TestContext.Current.CancellationToken);
        using MemoryStream bytes = new();
        expected.Save(bytes, PdfSaveOptions.Default with { Deterministic = true });
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(_tool.PathOf("out.pdf")));
    }

    [Fact]
    public void ImposeNUpPutsTwoPagesOnASheet()
    {
        MakePdf("a.pdf", 4);
        Assert.Equal(ExitCodes.Success, _tool.Run("impose", "a.pdf", "--nup", "2x1", "--sheet", "A3", "-o", "out.pdf").Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("out.pdf"));
        Assert.Equal(2, output.Pages.Count);
        Assert.Contains("Page 2", output.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ImposeNeedsOneLayout()
    {
        MakePdf("a.pdf", 1);
        Assert.Equal(ExitCodes.Usage, _tool.Run("impose", "a.pdf", "--booklet", "--grid", "2", "-o", "out.pdf").Code);
    }

    [Fact]
    public void StampsPageNumbersAndRemovesThem()
    {
        MakePdf("a.pdf", 3);
        Assert.Equal(ExitCodes.Success, _tool.Run("stamp", "a.pdf", "--page-numbers", "-o", "n.pdf").Code);
        using (PdfDocument stamped = PdfDocument.Open(_tool.PathOf("n.pdf")))
        {
            Assert.Contains("Page 2 of 3", stamped.Pages[1].ExtractText(), StringComparison.Ordinal);
        }

        Assert.Equal(ExitCodes.Success, _tool.Run("stamp", "n.pdf", "--remove", "-o", "clean.pdf").Code);
        using PdfDocument clean = PdfDocument.Open(_tool.PathOf("clean.pdf"));
        Assert.DoesNotContain("of 3", clean.Pages[1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void BatesNumbersRunOnAcrossFiles()
    {
        MakePdf("in/a.pdf", 2);
        MakePdf("in/b.pdf", 2);
        Assert.Equal(ExitCodes.Success, _tool.Run("stamp", "in/*.pdf", "--bates", "ACME-", "--bates-digits", "4", "-j", "1", "-o", "out/{name}.pdf").Code);
        using PdfDocument second = PdfDocument.Open(_tool.PathOf("out/b.pdf"));
        Assert.Contains("ACME-0004", second.Pages[1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void StampsAWatermarkAnImageAndABarcodeOnChosenPages()
    {
        MakePdf("a.pdf", 2);
        _tool.Write("logo.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        Assert.Equal(ExitCodes.Success, _tool.Run("run", "a.pdf", "stamp", "--watermark", "DRAFT", "--pages", "2", "+", "stamp", "--image", "logo.png", "--width", "20mm", "--anchor", "top-right", "+", "stamp", "--barcode", "qr:https://example.com", "--width", "25mm", "+", "save", "out.pdf").Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("out.pdf"));
        Assert.DoesNotContain("DRAFT", output.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Contains("DRAFT", output.Pages[1].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongBarcodeIsAUsageError()
    {
        MakePdf("a.pdf", 1);
        Assert.Equal(ExitCodes.Usage, _tool.Run("stamp", "a.pdf", "--barcode", "morse:sos", "--width", "1in", "-o", "x.pdf").Code);
    }

    private void MakePdf(string name, int pages)
    {
        List<string> parts = [];
        for (int i = 1; i <= pages; i++)
        {
            string source = $"{Path.GetFileNameWithoutExtension(name)}-{i}.md";
            _tool.Write(source, Encoding.UTF8.GetBytes($"# Page {i}\n"));
            Assert.Equal(ExitCodes.Success, _tool.Run("convert", source, "-o", source + ".pdf").Code);
            parts.Add(source + ".pdf");
        }

        Assert.Equal(ExitCodes.Success, _tool.Run(["merge", .. parts, "-o", name]).Code);
    }
}
