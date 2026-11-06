using System.Text.Json;

using Egap.Pdf;
using Egap.Spreadsheets;

namespace Egap.Cli.Tests;

/// <summary><c>egap convert</c>, <c>calc</c>, <c>cells</c> and <c>info</c> on workbooks.</summary>
public sealed class WorkbookCommandTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    /// <summary>Two sheets, saved with their formulas not calculated unless asked.</summary>
    private string Book(string name = "book.xlsx", bool calculate = true)
    {
        Workbook book = Workbook.Create();
        Worksheet data = book.Worksheets[0];
        data.Name = "Data";
        data["A1"].Value = 1;
        data["A2"].Value = 2;
        data["A3"].Formula = "SUM(A1:A2)";
        data["B1"].Value = "text";
        data["B2"].Formula = "1/0";
        Worksheet other = book.Worksheets.Add("Other sheet");
        other["A1"].Value = 5;
        if (calculate)
        {
            book.CalculateFull();
        }

        book.Save(_tool.PathOf(name));
        return name;
    }

    [Fact]
    public void InfoListsSheetsSizesAndFormulaErrors()
    {
        string file = Book();
        ToolRun run = _tool.Run("info", file);
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("book.xlsx: XLSX workbook, 2 sheets, 0 defined names", run.Text, StringComparison.Ordinal);
        Assert.Contains("Data: A1:B3, 3 x 2, 2 formulas, errors: 1 #DIV/0!", run.Text, StringComparison.Ordinal);
        Assert.Contains("Other sheet: A1, 1 x 1, 0 formulas", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void InfoJsonMatchesItsSchema()
    {
        ToolRun run = _tool.Run("info", Book(), "--json");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Empty(Schema.Check("egap-info-1.json", run.Text));
        using JsonDocument json = JsonDocument.Parse(run.Text);
        JsonElement sheet = json.RootElement.GetProperty("files")[0].GetProperty("sheets")[0];
        Assert.Equal("Data", sheet.GetProperty("name").GetString());
        Assert.Equal(1, sheet.GetProperty("formulaErrors").GetProperty("#DIV/0!").GetInt32());
    }

    [Fact]
    public void CalcSavesTheResultsAndWarnsOfErrors()
    {
        string file = Book(calculate: false);
        ToolRun run = _tool.Run("calc", file, "-o", "out.xlsx");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains("1 #DIV/0!", run.Error, StringComparison.Ordinal);
        Worksheet saved = Workbook.Load(_tool.PathOf("out.xlsx")).Worksheets[0];
        Assert.Equal(3, saved["A3"].Value.Number);
        Assert.Equal(CellError.DivisionByZero, saved["B2"].Value.Error);
    }

    [Fact]
    public void CalcWritesOnlyWorkbooks()
    {
        Assert.Equal(ExitCodes.Usage, _tool.Run("calc", Book(), "-o", "out.pdf").Code);
    }

    [Fact]
    public void CellsGiveARangeAsTabSeparatedText()
    {
        ToolRun run = _tool.Run("cells", Book(), "--range", "A1:B2");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal("1\ttext\n2\t#DIV/0!\n", run.Text);
    }

    [Fact]
    public void CellsReadAQuotedSheetAndAWholeSheet()
    {
        string file = Book();
        Assert.Equal("5\n", _tool.Run("cells", file, "--range", "'Other sheet'!A1").Text);
        Assert.Equal("5\n", _tool.Run("cells", file, "--range", "Other sheet").Text);
    }

    [Fact]
    public void CellsJsonMatchesItsSchemaAndGivesFormulas()
    {
        ToolRun run = _tool.Run("cells", Book(), "--range", "A2:B3", "--formulas", "--json");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Empty(Schema.Check("egap-cells-1.json", run.Text));
        using JsonDocument json = JsonDocument.Parse(run.Text);
        JsonElement file = json.RootElement.GetProperty("files")[0];
        Assert.Equal(2, file.GetProperty("rows")[0][0].GetDouble());
        Assert.Equal("#DIV/0!", file.GetProperty("rows")[0][1].GetProperty("error").GetString());
        Assert.Equal("SUM(A1:A2)", file.GetProperty("formulas")[1][0].GetString());
    }

    [Fact]
    public void CellsStopAtTheLimitAndSaySo()
    {
        ToolRun run = _tool.Run("cells", Book(), "--range", "A1:B3", "--max-cells", "4", "--json");
        using JsonDocument json = JsonDocument.Parse(run.Text);
        JsonElement file = json.RootElement.GetProperty("files")[0];
        Assert.True(file.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, file.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public void CellsCalculateOnlyWhenAsked()
    {
        string file = Book(calculate: false);
        Assert.Equal("\n", _tool.Run("cells", file, "--range", "A3").Text);
        Assert.Equal("3\n", _tool.Run("cells", file, "--range", "A3", "--calculate").Text);
    }

    [Theory]
    [InlineData("Nowhere!A1")]
    [InlineData("not a range")]
    public void CellsRefuseWhatTheWorkbookDoesNotHave(string range)
    {
        Assert.Equal(ExitCodes.Usage, _tool.Run("cells", Book(), "--range", range).Code);
    }

    [Fact]
    public void ConvertPrintsAWorkbookToPdf()
    {
        ToolRun run = _tool.Run("convert", Book(), "-o", "out.pdf");
        Assert.Equal(ExitCodes.Success, run.Code);
        using PdfDocument pdf = PdfDocument.Open(_tool.PathOf("out.pdf"));
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public void ConvertWritesOneImagePerPage()
    {
        ToolRun run = _tool.Run("convert", Book(), "-o", "out/{name}-{page}.png", "--dpi", "50");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.True(File.Exists(_tool.PathOf("out/book-1.png")));
        Assert.True(File.Exists(_tool.PathOf("out/book-2.png")));
    }

    [Fact]
    public void ConvertWritesOneCsvPerSheet()
    {
        ToolRun run = _tool.Run("convert", Book(), "-o", "out.csv");
        Assert.Equal(ExitCodes.Success, run.Code);
        string[] files = [.. Directory.GetFiles(_tool.Folder, "out*.csv").Select(Path.GetFileName).Order(StringComparer.Ordinal)!];
        Assert.Equal(2, files.Length);
        Assert.Contains(files, name => name.Contains("Data", StringComparison.Ordinal));
        Assert.Contains("text", File.ReadAllText(_tool.PathOf(files.First(name => name.Contains("Data", StringComparison.Ordinal)))), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertRefusesManySheetsOnStandardOutput()
    {
        Assert.Equal(ExitCodes.Usage, _tool.Run("convert", Book(), "-o", "-", "--to", "csv").Code);
    }

    [Theory]
    [InlineData("html", "<table")]
    [InlineData("md", "|")]
    public void ConvertWritesAWorkbookAsADocument(string extension, string expected)
    {
        ToolRun run = _tool.Run("convert", Book(), "-o", "out." + extension);
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Contains(expected, File.ReadAllText(_tool.PathOf("out." + extension)), StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertReadsCsvIntoAWorkbook()
    {
        _tool.Write("in.csv", "a,b\n1,2\n"u8.ToArray());
        ToolRun run = _tool.Run("convert", "in.csv", "-o", "out.xlsx");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal(2, Workbook.Load(_tool.PathOf("out.xlsx")).Worksheets[0]["B2"].Value.Number);
    }

    [Fact]
    public void ConvertPutsADocumentsTablesOnSheets()
    {
        _tool.Write("in.html", "<table><tr><td>x</td><td>7</td></tr></table>"u8.ToArray());
        ToolRun run = _tool.Run("convert", "in.html", "-o", "out.xlsx");
        Assert.Equal(ExitCodes.Success, run.Code);
        Assert.Equal("x", Workbook.Load(_tool.PathOf("out.xlsx")).Worksheets[0]["A1"].Value.Text);
    }
}
