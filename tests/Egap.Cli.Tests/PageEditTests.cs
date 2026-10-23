using System.Text;

using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class PageEditTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void MergeTakesEachFilesOwnPages()
    {
        Pages("a.pdf", "A", 3);
        Pages("b.pdf", "B", 4);
        ToolRun run = _tool.Run("merge", "a.pdf", "b.pdf#2-z:even", "a.pdf#z", "-o", "all.pdf");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal(["A1", "A2", "A3", "B3", "A3"], Labels("all.pdf"));
    }

    [Fact]
    public void ARealFileNameWithAHashWins()
    {
        Pages("x#1.pdf", "X", 2);
        Assert.Equal(ExitCodes.Success, _tool.Run("merge", "x#1.pdf", "-o", "out.pdf").Code);
        Assert.Equal(["X1", "X2"], Labels("out.pdf"));
    }

    [Fact]
    public void MergeNeverWritesOverAnInput()
    {
        Pages("a.pdf", "A", 1);
        Assert.Equal(ExitCodes.Usage, _tool.Run("merge", "a.pdf", "a.pdf", "-o", "a.pdf").Code);
    }

    [Fact]
    public void SplitPerPageEveryAndRange()
    {
        Pages("a.pdf", "A", 5);
        Assert.Equal(ExitCodes.Success, _tool.Run("split", "a.pdf", "-o", "one/").Code);
        Assert.Equal(["A4"], Labels("one/a-4.pdf"));
        Assert.Equal(ExitCodes.Success, _tool.Run("split", "a.pdf", "-o", "two/{name}_{page:00}.pdf", "--every", "2").Code);
        Assert.Equal(["A5"], Labels("two/a_03.pdf"));
        Assert.Equal(ExitCodes.Success, _tool.Run("split", "a.pdf", "-o", "r.pdf", "--range", "z-1", "--range", "2").Code);
        Assert.Equal(["A5", "A4", "A3", "A2", "A1"], Labels("r-1.pdf"));
        Assert.Equal(["A2"], Labels("r-2.pdf"));
    }

    [Fact]
    public void PagesSelectsDeletesRotatesAndInserts()
    {
        Pages("a.pdf", "A", 4);
        ToolRun run = _tool.Run("pages", "a.pdf", "-o", "out.pdf", "--select", "4,1-3", "--delete", "2", "--rotate", "90:1", "--blank", "end");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal(["A4", "A2", "A3", string.Empty], Labels("out.pdf"));
        using PdfDocument document = PdfDocument.Open(_tool.PathOf("out.pdf"));
        Assert.Equal(90, document.Pages[0].Rotate);
        Assert.Equal(0, document.Pages[1].Rotate);
    }

    [Fact]
    public void PagesCopiesAPageChosenTwice()
    {
        Pages("a.pdf", "A", 2);
        Assert.Equal(ExitCodes.Success, _tool.Run("pages", "a.pdf", "-o", "out.pdf", "--select", "1,1,2").Code);
        Assert.Equal(["A1", "A1", "A2"], Labels("out.pdf"));
    }

    [Fact]
    public void PagesSetsLabelsAndWorksInPlace()
    {
        Pages("a.pdf", "A", 4);
        Assert.Equal(ExitCodes.Success, _tool.Run("pages", "a.pdf", "--in-place", "--label", "1:r", "--label", "3:D:p-").Code);
        using PdfDocument document = PdfDocument.Open(_tool.PathOf("a.pdf"));
        Assert.Equal(["i", "ii", "p-1", "p-2"], Enumerable.Range(0, 4).Select(document.Pages.GetLabel));
    }

    [Fact]
    public void AnEditThatBreaksASignatureStopsWithoutTheFlag()
    {
        string signed = _tool.Copy("signatures/pyhanko-bb-rsa.pdf");
        ToolRun run = _tool.Run("pages", signed, "-o", "out.pdf", "--rotate", "90", "--rewrite");
        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.Contains("--allow-invalidating-signatures", run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_tool.PathOf("out.pdf")));
        Assert.Equal(ExitCodes.Success, _tool.Run("pages", signed, "-o", "out.pdf", "--rotate", "90", "--rewrite", "--allow-invalidating-signatures").Code);
    }

    [Fact]
    public void AnEditNeedsSomethingToDo()
    {
        Pages("a.pdf", "A", 1);
        Assert.Equal(ExitCodes.Usage, _tool.Run("pages", "a.pdf", "-o", "out.pdf").Code);
    }

    /// <summary>A PDF of <paramref name="count"/> pages, each showing its own label such as A1.</summary>
    private void Pages(string name, string prefix, int count)
    {
        List<string> parts = [];
        for (int i = 1; i <= count; i++)
        {
            string page = $"part-{prefix}{i}.pdf";
            Assert.Equal(ExitCodes.Success, _tool.Run("convert", Markdown($"{prefix}{i}"), "-o", page).Code);
            parts.Add(page);
        }

        Assert.Equal(ExitCodes.Success, _tool.Run(["merge", .. parts, "-o", name]).Code);
    }

    private string Markdown(string text)
    {
        string name = $"page-{text}.md";
        _tool.Write(name, Encoding.UTF8.GetBytes($"# {text}\n"));
        return name;
    }

    private string[] Labels(string name)
    {
        using PdfDocument document = PdfDocument.Open(_tool.PathOf(name));
        return [.. document.Pages.Select(page => page.ExtractText().Trim())];
    }
}
