using System.Text;
using System.Text.Json;

using Egap.Documents;
using Egap.Pdf;

namespace Egap.Cli.Tests;

/// <summary><c>egap compare</c> on documents, and <c>egap convert --revisions</c>.</summary>
public sealed class DocumentCompareTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    private void Md(string name, string text) => _tool.Write(name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void TwoDocumentsCompareIntoTrackedChanges()
    {
        Md("v1.md", "# Terms\n\nThe party shall pay within thirty days.\n\nThis line goes.\n");
        Md("v2.md", "# Terms\n\nThe party shall pay within ten days.\n");

        ToolRun run = _tool.Run("compare", "v1.md", "v2.md", "--output", "redline.docx", "--author", "Legal");

        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Contains("body: replaced \"thirty\" with \"ten\"", run.Text, StringComparison.Ordinal);
        Assert.Contains("body: deleted \"This line goes.", run.Text, StringComparison.Ordinal);
        Document result = Document.Load(_tool.PathOf("redline.docx"));
        Assert.All(result.Revisions, r => Assert.Equal("Legal", r.Author));
        result.Revisions.AcceptAll();
        Assert.Equal(Document.Load(_tool.PathOf("v2.md")).GetText().Trim(), result.GetText().Trim());
    }

    [Fact]
    public void TheSameDocumentHasNoDifferences()
    {
        Md("v1.md", "Hello there.\n");
        ToolRun run = _tool.Run("compare", "v1.md", "v1.md");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal("no differences\n", run.Text);
    }

    [Fact]
    public void JsonFollowsTheSchema()
    {
        Md("v1.md", "One two three four.\n\nKeep this paragraph.\n");
        Md("v2.md", "One two five four.\n\nKeep this paragraph.\n\nA new paragraph.\n");

        ToolRun run = _tool.Run("compare", "v1.md", "v2.md", "--json", "--granularity", "character");

        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Empty(Schema.Check("egap-document-compare-1.json", run.Text));
        using JsonDocument json = JsonDocument.Parse(run.Text);
        Assert.False(json.RootElement.GetProperty("identical").GetBoolean());
        Assert.True(json.RootElement.GetProperty("statistics").GetProperty("insertedWords").GetInt32() >= 3);
        Assert.Contains(json.RootElement.GetProperty("changes").EnumerateArray(), c => c.GetProperty("kind").GetString() == "inserted");
    }

    [Theory]
    [InlineData("redline.rtf")]
    [InlineData("redline.odt")]
    public void OutputWritesRtfAndOdtWithTrackedChanges(string output)
    {
        Md("v1.md", "Alpha bravo charlie.\n");
        Md("v2.md", "Alpha delta charlie.\n");

        Assert.Equal(ExitCodes.Failed, _tool.Run("compare", "v1.md", "v2.md", "--output", output).Code);

        Document result = Document.Load(_tool.PathOf(output));
        Assert.NotEmpty(result.Revisions);
        result.Revisions.RejectAll();
        Assert.Contains("bravo", result.GetText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("redline.pdf")]
    [InlineData("redline.html")]
    [InlineData("redline.md")]
    public void RedlineShowsBothSides(string output)
    {
        Md("v1.md", "Alpha bravo charlie.\n");
        Md("v2.md", "Alpha delta charlie.\n");

        Assert.Equal(ExitCodes.Failed, _tool.Run("compare", "v1.md", "v2.md", "--redline", output).Code);

        string text = output.EndsWith(".pdf", StringComparison.Ordinal) ? PdfText(output) : File.ReadAllText(_tool.PathOf(output));
        Assert.Contains("bravo", text, StringComparison.Ordinal);
        Assert.Contains("delta", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPdfsCompareAsDocumentsOnRequest()
    {
        Md("a.md", "The total is 100 dollars.\n");
        Md("b.md", "The total is 120 dollars.\n");
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "a.md", "b.md", "-o", "{name}.pdf").Code);

        ToolRun run = _tool.Run("compare", "a.pdf", "b.pdf", "--as-documents", "--output", "changes.docx", "--json");

        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Empty(Schema.Check("egap-document-compare-1.json", run.Text));
        Document result = Document.Load(_tool.PathOf("changes.docx"));
        result.Revisions.AcceptAll();
        Assert.Contains("120", result.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void APdfBesideADocumentRunsTheDocumentCompare()
    {
        Md("a.md", "The total is 100 dollars.\n");
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "a.md", "-o", "a.pdf").Code);
        Md("b.md", "The total is 120 dollars.\n");

        ToolRun run = _tool.Run("compare", "a.pdf", "b.md", "--json");

        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Empty(Schema.Check("egap-document-compare-1.json", run.Text));
    }

    [Fact]
    public void OptionsForTheOtherCompareAreUsageErrors()
    {
        Md("a.md", "One.\n");
        Md("b.md", "Two.\n");
        Assert.Equal(ExitCodes.Usage, _tool.Run("compare", "a.md", "b.md", "--dpi", "96").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("compare", "a.md", "b.md", "--output", "out.pdf").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("compare", "a.md", "b.md", "--granularity", "letter").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "a.md", "b.md", "-o", "{name}.pdf").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("compare", "a.pdf", "b.pdf", "--no-moves").Code);
    }

    [Fact]
    public void SwitchesTurnPartsOfTheCompareOff()
    {
        Md("a.md", "Hello World.\n");
        Md("b.md", "hello   world.\n");
        Assert.Equal(ExitCodes.Failed, _tool.Run("compare", "a.md", "b.md").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("compare", "a.md", "b.md", "--no-case", "--no-whitespace").Code);
    }

    [Theory]
    [InlineData("final", "delta", "bravo")]
    [InlineData("original", "bravo", "delta")]
    public void ConvertSettlesTrackedChanges(string view, string kept, string gone)
    {
        Md("v1.md", "Alpha bravo charlie.\n");
        Md("v2.md", "Alpha delta charlie.\n");
        Assert.Equal(ExitCodes.Failed, _tool.Run("compare", "v1.md", "v2.md", "--output", "redline.docx").Code);

        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "redline.docx", "--revisions", view, "-o", "out.docx").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "redline.docx", "--revisions", view, "-o", "out.pdf").Code);

        Document settled = Document.Load(_tool.PathOf("out.docx"));
        Assert.Empty(settled.Revisions);
        Assert.Contains(kept, settled.GetText(), StringComparison.Ordinal);
        Assert.DoesNotContain(gone, settled.GetText(), StringComparison.Ordinal);
        Assert.Contains(kept, PdfText("out.pdf"), StringComparison.Ordinal);
        Assert.DoesNotContain(gone, PdfText("out.pdf"), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertPrintsMarkup()
    {
        Md("v1.md", "Alpha bravo charlie.\n");
        Md("v2.md", "Alpha delta charlie.\n");
        Assert.Equal(ExitCodes.Failed, _tool.Run("compare", "v1.md", "v2.md", "--output", "redline.docx").Code);

        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "redline.docx", "--revisions", "markup", "-o", "out.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "redline.docx", "--revisions", "markup", "-o", "out.docx").Code);

        Assert.Contains("bravo", PdfText("out.pdf"), StringComparison.Ordinal);
        Assert.Contains("delta", PdfText("out.pdf"), StringComparison.Ordinal);
        Assert.NotEmpty(Document.Load(_tool.PathOf("out.docx")).Revisions);
        Assert.Equal(ExitCodes.Usage, _tool.Run("convert", "redline.docx", "--revisions", "markup", "-o", "out.txt").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("convert", "redline.docx", "--revisions", "all", "-o", "out.pdf").Code);
    }

    private string PdfText(string name)
    {
        using PdfDocument pdf = PdfDocument.Open(_tool.PathOf(name));
        return string.Concat(pdf.Pages.Select(p => p.ExtractText()));
    }
}
