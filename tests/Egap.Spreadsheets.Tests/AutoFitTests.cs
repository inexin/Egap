using System.Globalization;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// AutoFit against Excel: <c>tools/excel-autofit-oracle.ps1</c> filled <c>Oracle/autofit.xlsx</c> and
/// <c>Oracle/autofit-arial.xlsx</c> with a case per column and per row, had Excel AutoFit each, and recorded in
/// <c>Oracle/autofit.excel.tsv</c> the width each column took (as the file stores it) and the height each row took.
/// </summary>
public sealed class AutoFitTests
{
    private static readonly string[][] Excel = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "autofit.excel.tsv"))
        .Where(line => !line.StartsWith('#')).Select(line => line.Split('\t'))];

    private static readonly CultureInfo Locale = CultureInfo.GetCultureInfo(File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "autofit.excel.tsv")).First().Split(", ")[1].Split(' ')[0]);

    public static TheoryData<string, string> Columns() => Cases("cols");

    public static TheoryData<string, string> Rows() => Cases("rows");

    private static TheoryData<string, string> Cases(string kind)
    {
        TheoryData<string, string> cases = [];
        foreach (string[] line in Excel.Where(l => l[0].StartsWith(kind, StringComparison.Ordinal)))
        {
            cases.Add(line[0], line[1]);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public void FitsColumnsAsExcelDoes(string sheetName, string column)
    {
        string[] line = Array.Find(Excel, l => l[0] == sheetName && l[1] == column)!;
        Worksheet sheet = Load(sheetName);
        double? width = sheet.Columns[column].AutoFit();
        if (line[2].Length == 0)
        {
            // Excel writes no width for a column it left alone or fitted to the default width.
            Assert.True(width is null || Math.Abs((width.Value * 7) - Math.Truncate((double.Parse(line[3], CultureInfo.InvariantCulture) * 7) + 5)) < 1, $"{sheetName}!{column}: Egap {width}");
            return;
        }

        double excel = double.Parse(line[2], CultureInfo.InvariantCulture);
        int pixels = (int)Math.Round(Math.Abs(excel - width!.Value) * 7);
        Assert.True(pixels <= Tolerance(sheetName, column), $"{sheetName}!{column}: Excel {excel}, Egap {width}, {pixels} px");
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void FitsRowsAsExcelDoes(string sheetName, string row)
    {
        string[] line = Array.Find(Excel, l => l[0] == sheetName && l[1] == row)!;
        Worksheet sheet = Load(sheetName);
        double height = sheet.Rows[int.Parse(row, CultureInfo.InvariantCulture) - 1].AutoFit();
        Assert.Equal(double.Parse(line[3], CultureInfo.InvariantCulture), height);
    }

    [Fact]
    public void ARowOfTheNormalFontFollowsTheDefaultAgain()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("text");
        sheet.Rows[0].Height = 30;
        sheet.Rows[0].AutoFit();
        Assert.Null(sheet.Rows[0].Height);

        sheet["A2"].Value = CellValue.FromText("big");
        sheet["A2"].Style = sheet["A2"].Style with { Font = sheet["A2"].Style.Font with { Size = 20 } };
        Assert.Equal(26.25, sheet.Rows[1].AutoFit());
        Assert.Null(sheet.Rows[1].Height);

        using MemoryStream saved = new();
        book.Save(saved);
        saved.Position = 0;
        Assert.Equal(26.25, Workbook.Load(saved).Worksheets[0].Rows[1].AutoFit());
    }

    [Fact]
    public void ARangeFitsOnlyToItsOwnCells()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("a much longer text than the others");
        sheet["A2"].Value = CellValue.FromText("short");
        sheet.Range("A2:A3").AutoFitColumns();
        double shortWidth = sheet.Columns[0].Width!.Value;
        sheet.Columns[0].AutoFit();
        Assert.True(sheet.Columns[0].Width > shortWidth);
    }

    [Fact]
    public void AMergeDownOneColumnCountsAndOneAcrossDoesNot()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("a text much wider than the column");
        sheet.MergedCells.Add(RangeAddress.Parse("A1:A3"));
        sheet["B1"].Value = CellValue.FromText("a text much wider than the column");
        sheet.MergedCells.Add(RangeAddress.Parse("B1:C1"));
        Assert.True(sheet.Columns["A"].AutoFit() > 20);
        Assert.Null(sheet.Columns["B"].AutoFit());
    }

    [Fact]
    public void ARowStyledInASmallerFontTakesThatFontsHeight()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        CellStyle small = sheet["A1"].Style with { Font = sheet["A1"].Style.Font with { Name = "Arial", Size = 8 } };
        sheet.Rows[0].Style = small;
        sheet["A1"].Value = CellValue.FromText("small");
        sheet["A1"].Style = small;
        Assert.Equal(11.25, sheet.Rows[0].AutoFit());
    }

    [Fact]
    public void AZoomedSheetFitsAsExcelMeasuresAtTheZoom()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        // Measured on a GovDocs sheet at 75 %: Excel fits a column of years in Arial 10 to 41 pixels, not the 35 it takes at 100 %.
        sheet["A1"].Value = CellValue.FromNumber(1962);
        sheet["A1"].Style = sheet["A1"].Style with { Font = sheet["A1"].Style.Font with { Name = "Arial", Size = 10 } };
        Assert.Equal(35, Math.Round(sheet.Columns["A"].AutoFit()!.Value * 7));
        sheet.View.ZoomScale = 75;
        Assert.Equal(41, Math.Round(sheet.Columns["A"].AutoFit()!.Value * 7));
    }

    /// <summary>
    /// The pixels Egap may differ from Excel by. Excel measures each glyph to a fraction of a pixel and Egap to whole pixels,
    /// so one pixel either way. More where Egap measures the font from its outlines, as the table lacks it (Calibri at 36
    /// points, Arial italic), and for the built-in percent format, which Excel shows as "26 %" in the oracle's nb-NO locale
    /// and Egap as "26%".
    /// </summary>
    private static int Tolerance(string sheet, string column) => (sheet, column) switch
    {
        (_, "Y") => 2,
        ("cols-arial", "Z") => 4,
        (_, "L") => 4,
        _ => 1,
    };

    private static Worksheet Load(string sheetName)
    {
        Workbook book = Workbook.Load(Path.Combine(AppContext.BaseDirectory, "Oracle", sheetName.EndsWith("-arial", StringComparison.Ordinal) ? "autofit-arial.xlsx" : "autofit.xlsx"));
        book.Culture = Locale;
        return book.Worksheets[sheetName];
    }
}
