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

    [Theory]
    [InlineData("AB", 2.71)]
    [InlineData("AB\nCD", 6.29)]
    [InlineData("AB\n", 3.86)]
    [InlineData("AB\nCD\nEF", 9.43)]
    [InlineData("ABCDEFGH\nX", 12)]
    public void ALineBreakInTextThatDoesNotWrapIsAMissingGlyph(string text, double excel)
    {
        // Excel 365's AutoFit of Calibri 11 text without wrapping: each line break as wide as the font's missing glyph. Excel's
        // ColumnWidth leaves out the five pixels of padding the saved width has.
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText(text);
        Assert.Equal(Math.Round(excel * 7), Math.Round(sheet.Columns[0].AutoFit()!.Value * 7) - 5);
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

    [Fact]
    public void WrapsTextAsExcelDoes()
    {
        // tools/excel-wrap-oracle.ps1: twelve texts in six fonts at seventeen column widths, and the rows Excel fitted them to.
        string[][] cases = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "wrap.excel.tsv"))
            .Where(line => !line.StartsWith('#')).Select(line => line.Split('\t'))];
        List<string> misses = [];
        foreach (string[] line in cases)
        {
            string[] font = line[0].Split(',');
            double size = double.Parse(font[1], CultureInfo.InvariantCulture);
            bool bold = font[2] == "1";
            int column = int.Parse(line[1], CultureInfo.InvariantCulture);
            int lineHeight = Printing.CharacterWidths.LineHeight(font[0], size, bold, false)!.Value;
            Workbook book = Workbook.Create();
            Worksheet sheet = book.Worksheets[0];
            sheet.Columns[0].Width = Math.Truncate(column * 256.0 / 7) / 256;
            sheet["A1"].Value = CellValue.FromText(line[3]);
            sheet["A1"].Style = sheet["A1"].Style with
            {
                Font = sheet["A1"].Style.Font with { Name = font[0], Size = size, Bold = bold },
                Alignment = sheet["A1"].Style.Alignment with { WrapText = true },
            };
            int excel = int.Parse(line[2], CultureInfo.InvariantCulture) / lineHeight;
            int egap = (int)Math.Round(sheet.Rows[0].AutoFit() / 0.75 / lineHeight);
            if (excel != egap)
            {
                misses.Add($"{line[0]} {column} px: Excel {excel} lines, Egap {egap}: {line[3]}");
            }
        }

        // Excel breaks a few texts in columns narrower than a word, and at runs of spaces, otherwise.
        Assert.True(misses.Count <= 12, string.Join(Environment.NewLine, misses));
    }

    [Fact]
    public void ABorderWiderThanAPixelAddsToTheRowsBesideIt()
    {
        // Measured in Excel: a medium line under a row adds a pixel to it, a thick line one to the rows on both sides.
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int row = 0; row < 6; row++)
        {
            sheet[row, 0].Value = CellValue.FromText("text");
        }

        sheet[1, 0].Style = sheet[1, 0].Style with { Borders = CellBorders.None with { Bottom = new CellBorder(CellBorderStyle.Medium) } };
        sheet[4, 0].Style = sheet[4, 0].Style with { Borders = CellBorders.None with { Bottom = new CellBorder(CellBorderStyle.Thick) } };
        Assert.Equal([15, 15.75, 15, 15, 15.75, 15.75], Enumerable.Range(0, 6).Select(row => sheet.Rows[row].AutoFit()));
    }

    [Fact]
    public void AnEmptyFormattedCellHoldsItsRowToItsFont()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("text");
        sheet["C1"].Style = sheet["C1"].Style with { Font = sheet["C1"].Style.Font with { Size = 14 } };
        sheet["C2"].Style = sheet["C1"].Style;
        Assert.Equal(18.75, sheet.Rows[0].AutoFit());
        Assert.Equal(18.75, sheet.Rows[1].AutoFit());
    }

    [Fact]
    public void AWrappedColumnWidensUntilItsTextFitsTheLinesItsRowShows()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        const string text = "Agencies must NOT alter the format of this worksheet.";
        sheet["A1"].Value = CellValue.FromText(text);
        sheet["A1"].Style = sheet["A1"].Style with { Alignment = sheet["A1"].Style.Alignment with { WrapText = true } };
        sheet["B1"].Value = CellValue.FromText(text);
        Assert.Equal(sheet.Columns["B"].AutoFit(), sheet.Columns["A"].AutoFit());

        // A narrow column in a row three lines high widens only as far as wraps the text into three lines.
        sheet.Rows[0].Height = 45;
        sheet.Columns["A"].Width = 8;
        Assert.InRange(sheet.Columns["A"].AutoFit()!.Value, 8, sheet.Columns["B"].Width!.Value / 2);
    }

    [Fact]
    public void AFilterButtonWidensItsHeader()
    {
        // Measured in Excel with Calibri 11: "Name" takes 44 pixels, and 60 as a table's header beside its filter button.
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText("Name");
        sheet["A2"].Value = CellValue.FromText("a");
        sheet["C1"].Value = CellValue.FromText("Name");
        sheet.Tables.Add("A1:A2");
        Assert.Equal(60, Pixels(sheet.Columns["A"].AutoFit()!.Value));
        Assert.Equal(44, Pixels(sheet.Columns["C"].AutoFit()!.Value));
    }

    [Theory]
    [InlineData("Arial", false, "Total", 40)]
    [InlineData("Arial", true, "Segment", 66)]
    [InlineData("Times New Roman", true, "Gas Daily", 65)]
    public void ItalicTextReachesPastItsLastCharacter(string font, bool bold, string text, int excel)
    {
        // Measured in Excel at 10 points: italics take four pixels more than their characters' widths.
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = CellValue.FromText(text);
        sheet["A1"].Style = sheet["A1"].Style with { Font = sheet["A1"].Style.Font with { Name = font, Size = 10, Bold = bold, Italic = true } };
        Assert.Equal(excel, Pixels(sheet.Columns["A"].AutoFit()!.Value));
    }

    private static int Pixels(double width) => (int)Math.Truncate(((256 * width) + Math.Truncate(128.0 / 7)) / 256 * 7);

    /// <summary>
    /// The pixels Egap may differ from Excel by. Excel measures each glyph to a fraction of a pixel and Egap to whole pixels,
    /// so one pixel either way. More where Egap measures the font from its outlines, as the table lacks it (Calibri at 36
    /// points), and for the built-in percent format, which Excel shows as "26 %" in the oracle's nb-NO locale
    /// and Egap as "26%".
    /// </summary>
    private static int Tolerance(string sheet, string column) => (sheet, column) switch
    {
        (_, "Y") => 2,
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
