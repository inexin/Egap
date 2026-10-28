using System.Text;

using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class ConvertTests : IDisposable
{
    private const string Html = "<html><head><title>Report</title><style>h1 { color: #336 }</style></head><body><h1>Quarterly report</h1><p>Sales rose by a tenth.</p><img src=\"pixel.png\"><img src=\"http://example.invalid/remote.png\"></body></html>";

    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Theory]
    [InlineData("report.html")]
    [InlineData("report.md")]
    public void ToPdfEqualsTheLibraryCallByteForByte(string name)
    {
        WriteInputs();
        ToolRun run = _tool.Run("convert", name, "-o", "out.pdf", "--page-size", "A4", "--margins", "2cm", "--footer", "Page {page} of {total-pages}", "--deterministic");
        Assert.Equal(ExitCodes.Success, run.Code);

        PdfConvertOptions options = new()
        {
            PageSize = PdfPageSize.A4,
            Margins = PageMargins.All(Length.Cm(2)),
            Footer = PageBand.Text(center: "Page {page} of {total-pages}"),
        };
        using PdfDocument expected = PdfConvert.FromFile(_tool.PathOf(name), options);
        using MemoryStream bytes = new();
        expected.Save(bytes, PdfSaveOptions.Default with { Deterministic = true });
        Assert.Equal(bytes.ToArray(), File.ReadAllBytes(_tool.PathOf("out.pdf")));
    }

    [Fact]
    public void ARemoteImageIsLeftOutWithoutAllowNetwork()
    {
        WriteInputs();
        ToolRun run = _tool.Run("convert", "report.html", "-o", "out.pdf", "--allow-path", "assets");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("add --allow-network", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyInputsNeedAFolderOrATemplate()
    {
        WriteInputs();
        Assert.Equal(ExitCodes.Usage, _tool.Run("convert", "report.html", "report.md", "-o", "out.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "report.html", "report.md", "-o", "out/{name}-{ext}.pdf").Code);
        Assert.True(File.Exists(_tool.PathOf("out/report-html.pdf")));
        Assert.True(File.Exists(_tool.PathOf("out/report-md.pdf")));
    }

    [Theory]
    [InlineData("md", "Quarterly report")]
    [InlineData("txt", "Sales rose by a tenth.")]
    [InlineData("html", "Quarterly report")]
    public void FromPdfToFlowFormats(string format, string expected)
    {
        WriteInputs();
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "report.md", "-o", "report.pdf").Code);
        ToolRun run = _tool.Run("convert", "report.pdf", "-o", "back." + format);
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains(expected, File.ReadAllText(_tool.PathOf("back." + format)), StringComparison.Ordinal);
    }

    [Fact]
    public void HybridPdfsConvertBackThroughTheirSource()
    {
        WriteInputs();
        Assert.Equal(ExitCodes.Usage, _tool.Run("convert", "report.md", "-o", "a.pdf", "--embed-source", "--pdfa", "2b").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "report.md", "-o", "report.pdf", "--embed-source").Code);
        using (PdfDocument pdf = PdfDocument.Open(_tool.PathOf("report.pdf")))
        {
            Assert.Equal("Source", Assert.Single(pdf.Attachments).Relationship);
        }

        ToolRun back = _tool.Run("convert", "report.pdf", "-o", "back.md");
        Assert.Equal(ExitCodes.Success, back.Code);
        Assert.Contains("# Quarterly report", File.ReadAllText(_tool.PathOf("back.md")), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "report.pdf", "-o", "pages.md", "--ignore-embedded-source").Code);
        Assert.Contains("Quarterly report", File.ReadAllText(_tool.PathOf("pages.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void BetweenFlowFormats()
    {
        WriteInputs();
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "report.md", "-o", "report.docx").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "report.docx", "--to", "md", "-o", "-").Code);
    }

    [Fact]
    public void AnUnknownTargetIsAUsageError()
    {
        WriteInputs();
        ToolRun run = _tool.Run("convert", "report.md", "-o", "report.xyz");
        Assert.Equal(ExitCodes.Usage, run.Code);
        Assert.Contains("pdf, docx, html, md, txt", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfaWritesTheClaim()
    {
        WriteInputs();
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "report.md", "-o", "a.pdf", "--pdfa", "2b").Code);
        using PdfDocument pdf = PdfDocument.Open(_tool.PathOf("a.pdf"));
        Assert.Equal(PdfAConformance.PdfA2b, pdf.Conformance);
    }

    private void WriteInputs()
    {
        _tool.Write("report.html", Encoding.UTF8.GetBytes(Html));
        _tool.Write("report.md", "# Quarterly report\n\nSales rose by a tenth.\n"u8.ToArray());
        _tool.Write("pixel.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));
        Directory.CreateDirectory(_tool.PathOf("assets"));
    }
}
