using System.Text;

using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class ReviewTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void CompareExitsZeroForTheSameFileAndOneForAChange()
    {
        MakePdf("a.pdf", "The total is 100 dollars.");
        MakePdf("b.pdf", "The total is 120 dollars.");
        ToolRun same = _tool.Run("compare", "a.pdf", "a.pdf");
        Assert.Equal(ExitCodes.Success, same.Code);
        Assert.Equal("no differences\n", same.Text);

        ToolRun changed = _tool.Run("compare", "a.pdf", "b.pdf", "--json", "--markup", "marked.pdf");
        Assert.Equal(ExitCodes.Failed, changed.Code);
        Assert.Empty(Schema.Check("egap-compare-1.json", changed.Text));
        Assert.Contains("\"120\"", changed.Text, StringComparison.Ordinal);
        Assert.True(File.Exists(_tool.PathOf("marked.pdf")));
    }

    [Fact]
    public void RedactedTextIsGoneFromTextExtraction()
    {
        MakePdf("a.pdf", "Call 555-123-4567 or write to secret@example.com today.");
        ToolRun run = _tool.Run("redact", "a.pdf", "--regex", @"\d{3}-\d{3}-\d{4}", "--text", "secret@example.com", "-o", "out.pdf");
        Assert.Equal(ExitCodes.Success, run.Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("out.pdf"));
        string text = output.Pages[0].ExtractText();
        Assert.DoesNotContain("555", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.Contains("today", text, StringComparison.Ordinal);
        Assert.Single(output.Revisions);
        Assert.DoesNotContain("secret@example.com", Encoding.Latin1.GetString(File.ReadAllBytes(_tool.PathOf("out.pdf"))), StringComparison.Ordinal);
    }

    [Fact]
    public void MarkOnlyLeavesTheTextForReview()
    {
        MakePdf("a.pdf", "Keep this for now.");
        Assert.Equal(ExitCodes.Success, _tool.Run("redact", "a.pdf", "--text", "this", "--mark-only", "-o", "out.pdf").Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("out.pdf"));
        Assert.Contains("this", output.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.NotEmpty(output.Pages[0].Annotations);
    }

    [Fact]
    public void ARectangleRedactsAnArea()
    {
        MakePdf("a.pdf", "Top line.");
        Assert.Equal(ExitCodes.Success, _tool.Run("redact", "a.pdf", "--rect", "1:0,0,10000pt,10000pt", "-o", "out.pdf").Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("out.pdf"));
        Assert.Equal(string.Empty, output.Pages[0].ExtractText().Trim());
    }

    [Fact]
    public void SanitizeKeepsWhatItIsTold()
    {
        MakePdf("a.pdf", "Text.");
        Assert.Equal(ExitCodes.Success, _tool.Run("metadata", "set", "a.pdf", "--title", "Kept", "--in-place").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("sanitize", "a.pdf", "--keep", "metadata", "-o", "kept.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("sanitize", "a.pdf", "-o", "gone.pdf").Code);
        using PdfDocument kept = PdfDocument.Open(_tool.PathOf("kept.pdf"));
        using PdfDocument gone = PdfDocument.Open(_tool.PathOf("gone.pdf"));
        Assert.Equal("Kept", kept.Metadata.Title);
        Assert.Null(gone.Metadata.Title);
        Assert.Equal(ExitCodes.Usage, _tool.Run("sanitize", "a.pdf", "--keep", "secrets", "-o", "x.pdf").Code);
    }

    private void MakePdf(string name, string text)
    {
        string source = Path.ChangeExtension(name, ".md");
        _tool.Write(source, Encoding.UTF8.GetBytes(text + "\n"));
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", source, "-o", name).Code);
    }
}
