using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class PrintTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void PreflightReportsWhatTheLibraryFinds()
    {
        MakePdf("a.pdf");
        ToolRun run = _tool.Run("preflight", "a.pdf", "--json");
        Assert.Empty(Schema.Check("egap-preflight-1.json", run.Text));
        using PdfDocument document = PdfDocument.Open(_tool.PathOf("a.pdf"));
        PdfPreflightReport expected = PdfPreflight.Run(document, PdfPrintProfile.PdfX4, TestContext.Current.CancellationToken);
        Assert.Equal(expected.Passed ? ExitCodes.Success : ExitCodes.Failed, run.Code);
        Assert.Equal(expected.Issues.Count, System.Text.Json.JsonDocument.Parse(run.Text).RootElement.GetProperty("files")[0].GetProperty("issues").GetArrayLength());
    }

    [Fact]
    public void PdfxConvertThenCheck()
    {
        MakePdf("a.pdf");
        Assert.Equal(ExitCodes.Failed, _tool.Run("pdfx", "check", "a.pdf").Code);
        ToolRun convert = _tool.Run("pdfx", "convert", "a.pdf", "--level", "x-4", "--output-condition", "FOGRA39", "-o", "x4.pdf");
        Assert.Equal(ExitCodes.Success, convert.Code);
        ToolRun check = _tool.Run("pdfx", "check", "x4.pdf", "--level", "x-4", "--json");
        Assert.Equal(ExitCodes.Success, check.Code);
        Assert.Empty(Schema.Check("egap-pdfa-1.json", check.Text));
    }

    [Fact]
    public void SeparationsWriteAPlatePerInkAndTheCoverage()
    {
        MakePdf("a.pdf");
        ToolRun run = _tool.Run("separations", "a.pdf", "-o", "plates", "--dpi", "36", "--coverage", "--json");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Empty(Schema.Check("egap-separations-1.json", run.Text));
        foreach (string plate in (string[])["Cyan", "Magenta", "Yellow", "Black", "coverage"])
        {
            Assert.True(File.Exists(_tool.PathOf($"plates/a-1-{plate}.png")), plate);
        }
    }

    [Fact]
    public void ColorConvertsToTheTargetCmyk()
    {
        MakePdf("a.pdf");
        Assert.Equal(ExitCodes.Success, _tool.Run("color", "a.pdf", "--target", "FOGRA39", "-o", "cmyk.pdf").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("color", "a.pdf", "--target", "NOPE", "-o", "x.pdf").Code);
    }

    private void MakePdf(string name)
    {
        _tool.Write("a.md", "# Print me\n\nSome **body** text in color.\n"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "a.md", "-o", name).Code);
    }
}
