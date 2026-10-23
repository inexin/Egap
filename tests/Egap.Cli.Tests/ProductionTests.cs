using System.Text;

using Egap.Documents;
using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class ProductionTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void MergeDataWritesOneFileForAllRecordsOrOnePerRecord()
    {
        MakeTemplate("letter.docx");
        _tool.Write("people.json", """[ { "Name": "Ada", "City": "London" }, { "Name": "Grace", "City": "Arlington" } ]"""u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("merge-data", "letter.docx", "--data", "people.json", "-o", "all.pdf").Code);
        using (PdfDocument all = PdfDocument.Open(_tool.PathOf("all.pdf")))
        {
            string text = string.Concat(all.Pages.Select(p => p.ExtractText()));
            Assert.Contains("Dear Ada of London", text, StringComparison.Ordinal);
            Assert.Contains("Dear Grace of Arlington", text, StringComparison.Ordinal);
        }

        _tool.Write("people.csv", "Name,City\n\"Lovelace, Ada\",London\nGrace,Arlington\n"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("merge-data", "letter.docx", "--data", "people.csv", "--each", "--name-field", "City", "-o", "out/{name}.pdf").Code);
        using PdfDocument one = PdfDocument.Open(_tool.PathOf("out/London.pdf"));
        Assert.Contains("Dear Lovelace, Ada of London", one.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.True(File.Exists(_tool.PathOf("out/Arlington.pdf")));
    }

    [Fact]
    public void ABrokenCsvIsAUsageError()
    {
        MakeTemplate("letter.docx");
        _tool.Write("bad.csv", "Name,City\nAda\n"u8.ToArray());
        Assert.Equal(ExitCodes.Usage, _tool.Run("merge-data", "letter.docx", "--data", "bad.csv", "-o", "x.pdf").Code);
    }

    [Fact]
    public void CleanTurnsTextIntoOutlinesAndGray()
    {
        _tool.Write("a.md", "# Heading\n\nBody text.\n"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "a.md", "-o", "a.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("clean", "a.pdf", "--grayscale", "--text-to-outlines", "-o", "out.pdf").Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("out.pdf"));
        Assert.Equal(string.Empty, output.Pages[0].ExtractText().Trim());
        Assert.Equal(ExitCodes.Usage, _tool.Run("clean", "a.pdf", "-o", "x.pdf").Code);
    }

    [Fact]
    public void AnOcrLayerMakesAPageSearchable()
    {
        using (PdfDocument blank = PdfDocument.Create())
        {
            blank.Pages.AddBlank();
            blank.Save(_tool.PathOf("scan.pdf"));
        }

        _tool.Write("scan-1.hocr", Encoding.UTF8.GetBytes("""
            <html><body><div class="ocr_page" title="bbox 0 0 612 792">
            <span class="ocr_line" title="bbox 72 72 300 96"><span class="ocrx_word" title="bbox 72 72 160 96">Hello</span> <span class="ocrx_word" title="bbox 170 72 300 96">scanner</span></span>
            </div></body></html>
            """));
        ToolRun run = _tool.Run("ocr-layer", "scan.pdf", "--hocr", "scan-{page}.hocr", "-o", "searchable.pdf");
        Assert.Equal(ExitCodes.Success, run.Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("searchable.pdf"));
        Assert.Contains("scanner", output.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    /// <summary>A DOCX template: "Dear «Name» of «City»."</summary>
    private void MakeTemplate(string name)
    {
        Document document = new();
        DocumentBuilder builder = new(document);
        builder.Write("Dear ");
        builder.InsertField("MERGEFIELD Name", "«Name»");
        builder.Write(" of ");
        builder.InsertField("MERGEFIELD City", "«City»");
        builder.Write(".");
        document.Save(_tool.PathOf(name));
    }
}
