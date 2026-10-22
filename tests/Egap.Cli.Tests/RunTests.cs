using System.Text;

using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class RunTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void StepsJoinedByPlusDoWhatTheCommandsDo()
    {
        MakePdf("a.pdf", 3);
        ToolRun run = _tool.Run("run", "a.pdf", "pages", "--rotate", "90:1", "--delete", "2", "+", "metadata", "--title", "Report", "+", "save", "out.pdf", "--deterministic");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("pages", "a.pdf", "--rotate", "90:1", "--delete", "2", "-o", "one.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("metadata", "set", "one.pdf", "--title", "Report", "-o", "two.pdf").Code);
        using PdfDocument piped = PdfDocument.Open(_tool.PathOf("out.pdf"));
        using PdfDocument stepped = PdfDocument.Open(_tool.PathOf("two.pdf"));
        Assert.Equal(stepped.Pages.Count, piped.Pages.Count);
        Assert.Equal(stepped.Pages.Select(p => p.Rotate), piped.Pages.Select(p => p.Rotate));
        Assert.Equal("Report", piped.Metadata.Title);
    }

    [Fact]
    public void SeveralSavesAndEncryption()
    {
        MakePdf("a.pdf", 2);
        _tool.Environment["OWNER"] = "o";
        ToolRun run = _tool.Run("run", "a.pdf", "save", "plain.pdf", "+", "encrypt", "--owner-password-env", "OWNER", "+", "save", "locked.pdf");
        Assert.Equal(ExitCodes.Success, run.Code);
        using PdfDocument plain = PdfDocument.Open(_tool.PathOf("plain.pdf"));
        using PdfDocument locked = PdfDocument.Open(_tool.PathOf("locked.pdf"));
        Assert.False(plain.IsEncrypted);
        Assert.True(locked.IsEncrypted);
    }

    [Fact]
    public void AppendAddsPagesFromOtherFiles()
    {
        MakePdf("a.pdf", 2);
        MakePdf("b.pdf", 3);
        Assert.Equal(ExitCodes.Success, _tool.Run("run", "a.pdf", "append", "b.pdf#2-z", "+", "save", "out.pdf").Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("out.pdf"));
        Assert.Equal(4, output.Pages.Count);
    }

    [Fact]
    public void DryRunPrintsThePlan()
    {
        MakePdf("a.pdf", 1);
        MakePdf("b.pdf", 1);
        ToolRun run = _tool.Run("run", "*.pdf", "--dry-run", "pages", "--rotate", "180", "+", "save", "out/{name}.pdf");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal("steps:\n  pages --rotate 180\n  save out/{name}.pdf\na.pdf -> out/a.pdf\nb.pdf -> out/b.pdf\n", run.Text);
        Assert.False(Directory.Exists(_tool.PathOf("out")));
    }

    [Fact]
    public void AWorkflowRoundTrips()
    {
        MakePdf("a.pdf", 3);
        ToolRun saved = _tool.Run("run", "--save-workflow", "w.json", "--dry-run", "pages", "--rotate", "90:1", "--rotate", "180:2", "--select", "1-2", "+", "save", "out/{name}.pdf", "--linearize");
        Assert.Equal(ExitCodes.Success, saved.Code);
        Assert.Empty(Schema.Check("egap-workflow-1.json", File.ReadAllText(_tool.PathOf("w.json"))));
        Assert.Equal(ExitCodes.Success, _tool.Run("run", "a.pdf", "--workflow", "w.json").Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("out/a.pdf"));
        Assert.Equal([90, 180], output.Pages.Select(p => p.Rotate));
        Assert.True(output.IsLinearized);
    }

    [Theory]
    [InlineData("""{ "egap": 1, "steps": [ { "op": "fly" } ] }""")]
    [InlineData("""{ "egap": 1, "steps": [ { "op": "pages", "spin": "90" } ] }""")]
    [InlineData("""{ "egap": 2, "steps": [] }""")]
    [InlineData("""{ "egap": 1, "steps": [ { "op": "pages", "rotate": "90" } ] }""")]
    public void AWrongWorkflowIsAUsageError(string json)
    {
        MakePdf("a.pdf", 1);
        _tool.Write("w.json", Encoding.UTF8.GetBytes(json));
        Assert.Equal(ExitCodes.Usage, _tool.Run("run", "a.pdf", "--workflow", "w.json").Code);
    }

    [Fact]
    public void AnUnknownStepIsAUsageError()
    {
        MakePdf("a.pdf", 1);
        Assert.Equal(ExitCodes.Usage, _tool.Run("run", "a.pdf", "pages", "--rotate", "90", "+", "fly", "+", "save", "o.pdf").Code);
    }

    [Fact]
    public void BatchesGiveTheSameOutputsAtAnyJobCount()
    {
        for (int i = 0; i < 6; i++)
        {
            MakePdf($"in/f{i}.pdf", 2);
        }

        Assert.Equal(ExitCodes.Success, _tool.Run("run", "in/*.pdf", "-j", "1", "pages", "--rotate", "90", "+", "save", "one/{name}.pdf", "--deterministic").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("run", "in/*.pdf", "-j", "8", "pages", "--rotate", "90", "+", "save", "eight/{name}.pdf", "--deterministic").Code);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(File.ReadAllBytes(_tool.PathOf($"one/f{i}.pdf")), File.ReadAllBytes(_tool.PathOf($"eight/f{i}.pdf")));
        }
    }

    [Fact]
    public void ABatchCarriesOnPastAFailedFile()
    {
        MakePdf("in/a.pdf", 1);
        _tool.Write("in/b.pdf", "not a pdf"u8.ToArray());
        MakePdf("in/c.pdf", 1);
        ToolRun run = _tool.Run("run", "in/*.pdf", "pages", "--rotate", "90", "+", "save", "out/{name}.pdf");
        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Contains("3 files: 2 done, 1 failed", run.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(_tool.PathOf("out/c.pdf")));
    }

    [Fact]
    public void ASaveNeverReplacesItsInputWithoutInPlace()
    {
        MakePdf("a.pdf", 1);
        Assert.Equal(ExitCodes.Usage, _tool.Run("run", "a.pdf", "pages", "--rotate", "90", "+", "save", "a.pdf").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("run", "a.pdf", "--in-place", "pages", "--rotate", "90", "+", "save", "a.pdf").Code);
        using PdfDocument output = PdfDocument.Open(_tool.PathOf("a.pdf"));
        Assert.Equal(90, output.Pages[0].Rotate);
    }

    [Fact]
    public void ListShowsTheSteps()
    {
        ToolRun run = _tool.Run("run", "--list");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("pages: ", run.Text, StringComparison.Ordinal);
        Assert.Contains("save: ", run.Text, StringComparison.Ordinal);
    }

    private void MakePdf(string name, int pages)
    {
        StringBuilder markdown = new();
        for (int i = 1; i <= pages; i++)
        {
            markdown.Append(PageText(i));
        }

        string source = Path.ChangeExtension(name, ".md");
        _tool.Write(source, Encoding.UTF8.GetBytes(markdown.ToString()));
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", source, "-o", name).Code);
        using PdfDocument one = PdfDocument.Open(_tool.PathOf(name));
        if (one.Pages.Count < pages)
        {
            string[] parts = [.. Enumerable.Repeat(name, pages)];
            one.Dispose();
            File.Move(_tool.PathOf(name), _tool.PathOf(name + ".one"));
            Assert.Equal(ExitCodes.Success, _tool.Run(["merge", .. parts.Select(_ => name + ".one#1"), "-o", name]).Code);
        }
    }

    private static string PageText(int page) => $"# Page {page}\n\nText.\n\n";
}
