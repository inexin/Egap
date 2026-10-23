using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class PageCommandTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void TextMatchesExtractText()
    {
        string file = ThreePages();
        ToolRun run = _tool.Run("text", file, "--pages", "2-3");
        Assert.Equal(ExitCodes.Success, run.Code);
        using PdfDocument document = PdfDocument.Open(_tool.PathOf(file));
        string expected = document.Pages[1].ExtractText().TrimEnd('\n') + "\n\f" + document.Pages[2].ExtractText().TrimEnd('\n') + "\n";
        Assert.Equal(expected, run.Text);
        Assert.Contains("Page two", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TextJsonMatchesTheSchema()
    {
        string file = ThreePages();
        ToolRun run = _tool.Run("text", file, "--json", "-p", "1");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Empty(Schema.Check("egap-text-1.json", run.Text));
        Assert.Contains("\"Page\"", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void APageRangeOutsideTheFileFailsIt()
    {
        string file = ThreePages();
        ToolRun run = _tool.Run("text", file, "--pages", "4");
        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Contains("3 pages", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongPageRangeIsAUsageError()
    {
        string file = ThreePages();
        Assert.Equal(ExitCodes.Usage, _tool.Run("text", file, "--pages", "1-q").Code);
    }

    [Fact]
    public void RenderMatchesRenderPagesToFiles()
    {
        string file = ThreePages();
        ToolRun run = _tool.Run("render", file, "-o", "out/", "--format", "png", "--dpi", "36", "--pages", "1,3");
        Assert.Equal(ExitCodes.Success, run.Code);
        using PdfDocument document = PdfDocument.Open(_tool.PathOf(file));
        IReadOnlyList<string> expected = document.RenderPagesToFiles(_tool.PathOf("lib/p-{page}.png"), new PdfRenderOptions { Dpi = 36 }, PdfPageRange.Parse("1,3"), TestContext.Current.CancellationToken);
        string name = Path.GetFileNameWithoutExtension(file);
        Assert.Equal(File.ReadAllBytes(expected[0]), File.ReadAllBytes(_tool.PathOf($"out/{name}-1.png")));
        Assert.Equal(File.ReadAllBytes(expected[1]), File.ReadAllBytes(_tool.PathOf($"out/{name}-3.png")));
        Assert.False(File.Exists(_tool.PathOf($"out/{name}-2.png")));
    }

    [Fact]
    public void RenderAddsThePageToATemplateWithoutOne()
    {
        string file = ThreePages();
        Assert.Equal(ExitCodes.Success, _tool.Run("render", file, "-o", "shot.jpg", "--fit", "100").Code);
        Assert.True(File.Exists(_tool.PathOf("shot-1.jpg")));
        Assert.True(File.Exists(_tool.PathOf("shot-3.jpg")));
    }

    /// <summary>A three-page PDF made from Markdown by egap convert.</summary>
    private string ThreePages()
    {
        _tool.Write("three.md", "# Page one\n\nFirst.\n\n<div style=\"page-break-after: always\"></div>\n\n# Page two\n\nSecond.\n\n<div style=\"page-break-after: always\"></div>\n\n# Page three\n\nThird.\n"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "three.md", "-o", "three.pdf").Code);
        using PdfDocument document = PdfDocument.Open(_tool.PathOf("three.pdf"));
        if (document.Pages.Count != 3)
        {
            // Markdown without page breaks gives one page; build three from it.
            using PdfDocument three = PdfDocument.Merge(document, document, document);
            three.Save(_tool.PathOf("three3.pdf"));
            return "three3.pdf";
        }

        return "three.pdf";
    }
}
