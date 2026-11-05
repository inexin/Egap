using System.Xml.Linq;

namespace Egap.Spreadsheets.Tests;

public class CalculationTests
{
    private static Worksheet Sheet() => Workbook.Create().Worksheets[0];

    [Fact]
    public void CalculateGivesFormulasTheirResults()
    {
        Worksheet sheet = Sheet();
        sheet["A1"].Value = 2;
        sheet["B1"].Formula = "A1*3";
        sheet["C1"].Formula = "B1+1";
        sheet["D1"].Formula = "\"n=\"&C1";

        CalculationResult result = sheet.Workbook.Calculate();

        Assert.Equal(3, result.Calculated);
        Assert.Equal(6, sheet["B1"].Value.Number);
        Assert.Equal(7, sheet["C1"].Value.Number);
        Assert.Equal("n=7", sheet["D1"].Value.Text);
        Assert.Empty(result.CircularReferences);
        Assert.True(result.Converged);
    }

    [Fact]
    public void OnlyFormulasReadingAChangeAreCalculatedAgain()
    {
        Worksheet sheet = Sheet();
        sheet["A1"].Value = 1;
        sheet["A2"].Value = 10;
        sheet["B1"].Formula = "A1*2";
        sheet["C1"].Formula = "B1+1";
        sheet["B2"].Formula = "A2*2";
        sheet["D1"].Formula = "SUM(A1:A2)";
        Workbook book = sheet.Workbook;
        book.Calculate();

        Assert.Equal(0, book.Calculate().Calculated);

        sheet["A1"].Value = 5;
        CalculationResult result = book.Calculate();
        Assert.Equal(3, result.Calculated);
        Assert.Equal(11, sheet["C1"].Value.Number);
        Assert.Equal(15, sheet["D1"].Value.Number);
        Assert.Equal(20, sheet["B2"].Value.Number);

        Assert.Equal(4, book.CalculateFull().Calculated);
    }

    [Fact]
    public void ChangedFormulasAreReadAgain()
    {
        Worksheet sheet = Sheet();
        sheet["A1"].Value = 1;
        sheet["A2"].Value = 2;
        sheet["B1"].Formula = "A1";
        sheet["C1"].Formula = "B1*10";
        sheet.Workbook.Calculate();

        sheet["B1"].Formula = "A2";
        sheet.Workbook.Calculate();
        Assert.Equal(20, sheet["C1"].Value.Number);

        // B1 no longer reads A1.
        sheet["A1"].Value = 100;
        Assert.Equal(0, sheet.Workbook.Calculate().Calculated);

        sheet["B1"].Value = 3;
        sheet.Workbook.Calculate();
        Assert.Equal(30, sheet["C1"].Value.Number);
    }

    [Fact]
    public void VolatileFormulasAreCalculatedEveryTime()
    {
        // RAND and NOW are volatile: their formulas and those reading them run again.
        Worksheet sheet = Sheet();
        sheet["A1"].Formula = "RAND()";
        sheet["B1"].Formula = "A1*0+1";
        sheet["C1"].Formula = "5";
        sheet["E1"].Formula = "IFERROR(NOW()>0,TRUE)";
        sheet.Workbook.Calculate();

        CalculationResult result = sheet.Workbook.Calculate();

        Assert.Equal(3, result.Calculated);
        Assert.Equal(1, sheet["B1"].Value.Number);
        Assert.True(sheet["E1"].Value.Boolean);
    }

    [Fact]
    public void CircularReferencesAreReportedWithIterationOff()
    {
        Worksheet sheet = Sheet();
        sheet["A1"].Formula = "B1+1";
        sheet["B1"].Formula = "A1/2";
        sheet["C1"].Formula = "1+1";

        CalculationResult result = sheet.Workbook.Calculate();

        IReadOnlyList<Cell> ring = Assert.Single(result.CircularReferences);
        Assert.Equal(["A1", "B1"], ring.Select(c => c.Address.ToString()));
        Assert.Equal(0, result.Iterations);
        Assert.Equal(2, sheet["C1"].Value.Number);
    }

    [Fact]
    public void CircularReferencesIterateWhenAsked()
    {
        Worksheet sheet = Sheet();
        Workbook book = sheet.Workbook;
        book.Calculation.Iterate = true;
        sheet["A1"].Formula = "B1+1";
        sheet["B1"].Formula = "A1/2";
        sheet["C1"].Formula = "A1*10";

        CalculationResult result = book.Calculate();

        Assert.True(result.Converged);
        Assert.InRange(result.Iterations, 2, book.Calculation.MaxIterations);
        Assert.Equal(2, sheet["A1"].Value.Number, 0.01);
        Assert.Equal(1, sheet["B1"].Value.Number, 0.01);
        Assert.Equal(20, sheet["C1"].Value.Number, 0.1);
    }

    [Fact]
    public void IterationStopsAtTheLimit()
    {
        Worksheet sheet = Sheet();
        Workbook book = sheet.Workbook;
        book.Calculation.Iterate = true;
        book.Calculation.MaxIterations = 10;
        sheet["A1"].Formula = "A1+1";

        CalculationResult result = book.Calculate();

        Assert.False(result.Converged);
        Assert.Equal(10, result.Iterations);
        Assert.Equal(10, sheet["A1"].Value.Number);
    }

    [Fact]
    public void NamesAreFollowedToTheCellsTheyStandFor()
    {
        Worksheet sheet = Sheet();
        Workbook book = sheet.Workbook;
        sheet["A1"].Value = 0.25;
        sheet["A2"].Value = 100;
        book.Names.Add("Rate", "Sheet1!$A$1");
        book.Names.Add("Taxed", "Sheet1!$A$2*Rate");
        sheet["B1"].Formula = "A2*Rate";
        sheet["B2"].Formula = "Taxed+1";
        book.Calculate();
        Assert.Equal(25, sheet["B1"].Value.Number);

        sheet["A1"].Value = 0.5;
        Assert.Equal(2, book.Calculate().Calculated);
        Assert.Equal(50, sheet["B1"].Value.Number);
        Assert.Equal(51, sheet["B2"].Value.Number);

        // A new name may change what any formula reads: everything is calculated again.
        book.Names.Add("Other", "1");
        Assert.Equal(2, book.Calculate().Calculated);
    }

    [Fact]
    public void FormulasOnOtherSheetsAndTablesAreFollowed()
    {
        Workbook book = Workbook.Create();
        Worksheet first = book.Worksheets[0];
        Worksheet second = book.Worksheets.Add("Second");
        first["A1"].Value = "Name";
        first["B1"].Value = "Score";
        first["A2"].Value = "Ann";
        first["B2"].Value = 3;
        first["A3"].Value = "Bo";
        first["B3"].Value = 4;
        first.Tables.Add("A1:B3", "Scores");
        second["A1"].Formula = "SUM(Scores[Score])";
        second["A2"].Formula = "Sheet1!B2*2";
        book.Calculate();
        Assert.Equal(7, second["A1"].Value.Number);

        first["B3"].Value = 10;
        Assert.Equal(1, book.Calculate().Calculated);
        Assert.Equal(13, second["A1"].Value.Number);
    }

    [Fact]
    public void ManyIndependentFormulasCalculateInParallel()
    {
        Worksheet sheet = Sheet();
        for (int row = 0; row < 2000; row++)
        {
            sheet[row, 0].Value = row;
            sheet[row, 1].Formula = $"A{row + 1}*2";
            sheet[row, 2].Formula = $"B{row + 1}+1";
        }

        CalculationResult result = sheet.Workbook.Calculate();

        Assert.Equal(4000, result.Calculated);
        for (int row = 0; row < 2000; row++)
        {
            Assert.Equal(row * 2 + 1, sheet[row, 2].Value.Number);
        }

        sheet.Workbook.Calculation.Concurrent = false;
        Assert.Equal(4000, sheet.Workbook.CalculateFull().Calculated);
        Assert.Equal(3999, sheet[1999, 2].Value.Number);
    }

    [Fact]
    public void ALoadedWorkbookKeepsItsResultsUntilSomethingChanges()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet sheet = book.Worksheets["Data"];

        Assert.Equal(0, book.Calculate().Calculated);
        Assert.Equal(22.5, sheet["D3"].Value.Number);

        sheet["B3"].Value = 3;
        book.Calculate();
        Assert.Equal(27, sheet["D3"].Value.Number);
        Assert.Equal(39.5, sheet["D4"].Value.Number);
    }

    [Fact]
    public void SharedFormulasAreCalculatedAndShownForEachCell()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet sheet = book.Worksheets["Data"];

        Assert.Equal("B2*C2", sheet["D2"].Formula);
        Assert.Equal("B3*C3", sheet["D3"].Formula);

        book.CalculateFull();
        Assert.Equal(12.5, sheet["D2"].Value.Number);
        Assert.Equal(22.5, sheet["D3"].Value.Number);

        // Changing the first cell leaves the other its formula.
        sheet["D2"].Formula = "B2+C2";
        Assert.Equal("B3*C3", sheet["D3"].Formula);
        sheet["C3"].Value = 2;
        book.Calculate();
        Assert.Equal(11.25, sheet["D2"].Value.Number);
        Assert.Equal(5, sheet["D3"].Value.Number);

        (Workbook loaded, _) = XlsxSeed.RoundTrip(book);
        Assert.Equal("B3*C3", loaded.Worksheets["Data"]["D3"].Formula);
        Assert.Equal(5, loaded.Worksheets["Data"]["D3"].Value.Number);
    }

    [Fact]
    public void ArrayFormulasFillTheirRange()
    {
        Worksheet sheet = Sheet();
        for (int row = 0; row < 3; row++)
        {
            sheet[row, 0].Value = row + 1;
            sheet[row, 1].Value = row + 4;
        }

        sheet.Range("C1:C4").SetArrayFormula("=A1:A3*B1:B3");
        sheet.Range("E1:F2").SetArrayFormula("A1:A2");
        sheet["G1"].Formula = "SUM(C1:C3)";
        sheet.Workbook.Calculate();

        Assert.Equal([4, 10, 18], Enumerable.Range(0, 3).Select(row => sheet[row, 2].Value.Number));
        Assert.Equal(CellError.NotAvailable, sheet["C4"].Value.Error);
        Assert.Equal([1, 1, 2, 2], sheet.Range("E1:F2").Select(c => c.Value.Number));
        Assert.Equal(32, sheet["G1"].Value.Number);
        Assert.Equal("A1:A3*B1:B3", sheet["C1"].Formula);
        Assert.Null(sheet["C2"].Formula);
        Assert.Equal(RangeAddress.Parse("C1:C4"), sheet["C3"].ArrayRange?.Address);
        Assert.Null(sheet["D1"].ArrayRange);

        // A cell the array writes is read by G1: changing what the array reads reaches G1.
        sheet["A3"].Value = 4;
        sheet.Workbook.Calculate();
        Assert.Equal(24, sheet["C3"].Value.Number);
        Assert.Equal(38, sheet["G1"].Value.Number);

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(sheet.Workbook);
        Worksheet again = loaded.Worksheets[0];
        Assert.Equal(RangeAddress.Parse("C1:C4"), again["C2"].ArrayRange?.Address);
        Assert.Equal(24, again["C3"].Value.Number);
        XNamespace ns = XlsxSeed.Main;
        XElement formula = XlsxSeed.Xml(XlsxSeed.Parts(package), "xl/worksheets/sheet1.xml").Descendants(ns + "f").First();
        Assert.Equal("array", (string?)formula.Attribute("t"));
        Assert.Equal("C1:C4", (string?)formula.Attribute("ref"));

        again.Workbook.CalculateFull();
        Assert.Equal(24, again["C3"].Value.Number);

        // A new value in the first cell ends the array formula.
        sheet["C1"].Value = 0;
        Assert.Null(sheet["C2"].ArrayRange);
    }

    [Fact]
    public void SetArrayFormulaRefusesBrokenFormulas()
    {
        Worksheet sheet = Sheet();

        Assert.Throws<ArgumentNullException>(() => sheet.Range("A1:A2").SetArrayFormula(null!));
        Assert.Throws<ArgumentException>(() => sheet.Range("A1:A2").SetArrayFormula("="));
        Assert.Throws<FormulaException>(() => sheet.Range("A1:A2").SetArrayFormula("SUM("));
    }

    [Fact]
    public void SheetChangesStartTheCalculationAgain()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Formula = "Other!A1+1";
        book.Calculate();
        Assert.Equal(CellError.Reference, sheet["A1"].Value.Error);

        Worksheet other = book.Worksheets.Add("Other");
        other["A1"].Value = 41;
        book.Calculate();
        Assert.Equal(42, sheet["A1"].Value.Number);
    }

    [Fact]
    public void TheGuidesExamplesGiveWhatTheySay()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = 2;
        sheet["B1"].Formula = "A1*3";
        sheet["C1"].Formula = "B1+1";
        book.Calculate();
        Assert.Equal(7, sheet["C1"].Value.Number);

        sheet["A1"].Value = 5;
        Assert.Equal(2, book.Calculate().Calculated);
        sheet.Range("D1:D3").SetArrayFormula("A1*{1;2;3}");
        book.Calculate();
        Assert.Equal([5, 10, 15], sheet.Range("D1:D3").Select(c => c.Value.Number));
        Assert.Equal(RangeAddress.Parse("D1:D3"), sheet["D2"].ArrayRange?.Address);

        Workbook ring = Workbook.Create();
        Worksheet cells = ring.Worksheets[0];
        ring.Calculation.Iterate = true;
        ring.Calculation.MaxIterations = 100;
        ring.Calculation.MaxChange = 0.0001;
        cells["A1"].Formula = "B1+1";
        cells["B1"].Formula = "A1/2";
        Assert.True(ring.Calculate().Converged);
        Assert.Equal(2, cells["A1"].Value.Number, 0.001);
    }

    [Fact]
    public void AnArrayFormulaOverTheWholeSheetIsLeftAsItIs()
    {
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["xl/worksheets/sheet1.xml"] = seed["xl/worksheets/sheet1.xml"].Replace("t=\"array\" ref=\"D4\"", "t=\"array\" ref=\"D4:XFD1048576\"", StringComparison.Ordinal);
        Workbook book = Workbook.Load(XlsxSeed.Package(seed));
        Worksheet sheet = book.Worksheets["Data"];

        book.CalculateFull();

        Assert.Equal(35, sheet["D4"].Value.Number);
        Assert.Equal(RangeAddress.Parse("D4:XFD1048576"), sheet["E5"].ArrayRange?.Address);
    }
}
