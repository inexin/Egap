using System.Globalization;

namespace Egap.Spreadsheets.Tests;

/// <summary>The formula compiler and evaluator, through <see cref="Worksheet.Evaluate"/>: values, errors and references as Excel has them.</summary>
public sealed class FormulaEvaluatorTests
{
    private static Worksheet Sheet()
    {
        Workbook book = Workbook.Create();
        book.Culture = CultureInfo.InvariantCulture;
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = 1;
        sheet["A2"].Value = 2;
        sheet["A3"].Value = 3;
        sheet["B1"].Value = "text";
        sheet["B2"].Value = true;
        sheet["B3"].Value = CellError.NotAvailable;
        return sheet;
    }

    private static CellValue Eval(string formula, string? cell = null) =>
        Sheet().Evaluate(formula, cell is null ? null : CellAddress.Parse(cell));

    [Theory]
    [InlineData("1+2*3", 7)]
    [InlineData("(1+2)*3", 9)]
    [InlineData("-2^2", 4)]
    [InlineData("2^3^2", 64)]
    [InlineData("2*-3", -6)]
    [InlineData("10%", 0.1)]
    [InlineData("50%%", 0.005)]
    [InlineData("=+-+1", -1)]
    [InlineData("\"1\"+1", 2)]
    [InlineData("\" 2.5 \"*2", 5)]
    [InlineData("\"50%\"*2", 1)]
    [InlineData("\"(3)\"+0", -3)]
    [InlineData("\"1,000\"+0", 1000)]
    [InlineData("\"2024-03-15\"+0", 45366)]
    [InlineData("\"12:00\"+0", 0.5)]
    [InlineData("TRUE+TRUE", 2)]
    [InlineData("A9+1", 1)]
    [InlineData("0.5-0.4-0.1", 0)]
    [InlineData("SUM(A1:A3)", 6)]
    [InlineData("SUM(A1:A3,\"4\",TRUE,,{10,20})", 41)]
    [InlineData("SUM((A1,A3))", 4)]
    [InlineData("SUM(A1:A3 A2:C2)", 2)]
    [InlineData("SUM({1,2;3,4}*2)", 20)]
    [InlineData("SUM({1,2}+{10;20})", 66)]
    [InlineData("SUM(IF({1,0,1},{1,2,3},10))", 14)]
    [InlineData("SUM(IFERROR({1,#N/A,3},10))", 14)]
    [InlineData("CHOOSE(2,A1,A2,A3)", 2)]
    [InlineData("SUM(CHOOSE(1,A1:A3,A1))", 6)]
    [InlineData("ROW(B7)+COLUMN(C1:D1)", 10)]
    [InlineData("IF(TRUE,1,1/0)", 1)]
    [InlineData("IF(TRUE,,1)", 0)]
    [InlineData("IFERROR(1/0,7)", 7)]
    [InlineData("IFNA(NA(),7)", 7)]
    [InlineData("DATE(1900,2,29)", 60)]
    [InlineData("DATE(1900,3,1)", 61)]
    [InlineData("DATE(2024,3,15)", 45366)]
    [InlineData("DATE(2024,14,1)", 45689)]
    [InlineData("DATE(2024,1,0)", 45291)]
    [InlineData("DATE(118,1,1)", 43101)]
    public void NumbersComeOutAsInExcel(string formula, double expected)
    {
        CellValue value = Eval(formula);

        Assert.Equal(CellValueKind.Number, value.Kind);
        Assert.Equal(expected, value.Number, 1e-12);
    }

    [Theory]
    [InlineData("1/0", CellError.DivisionByZero)]
    [InlineData("0^0", CellError.Number)]
    [InlineData("0^-1", CellError.DivisionByZero)]
    [InlineData("(-8)^0.5", CellError.Number)]
    [InlineData("1E308*10", CellError.Number)]
    [InlineData("\"x\"+1", CellError.Value)]
    [InlineData("\"\"+1", CellError.Value)]
    [InlineData("B1+1", CellError.Value)]
    [InlineData("B3+1/0", CellError.NotAvailable)]
    [InlineData("SUM(A1:B3)+SUM(B3)", CellError.NotAvailable)]
    [InlineData("SUM(A1,\"x\")", CellError.Value)]
    [InlineData("SUM(A1:A2 B5)", CellError.Null)]
    [InlineData("IFNA(1/0,7)", CellError.DivisionByZero)]
    [InlineData("IF(\"maybe\",1,2)", CellError.Value)]
    [InlineData("IF(1/0,1,2)", CellError.DivisionByZero)]
    [InlineData("AND(B1)", CellError.Value)]
    [InlineData("NoSuchName", CellError.Name)]
    [InlineData("NOSUCHFUNCTION(1/0)", CellError.Name)]
    [InlineData("_xludf.MyFunction(1)", CellError.Name)]
    [InlineData("Missing!A1", CellError.Reference)]
    [InlineData("[1]Sheet1!A1", CellError.Reference)]
    [InlineData("A1#", CellError.Reference)]
    [InlineData("CHOOSE(4,1,2,3)", CellError.Value)]
    [InlineData("ROW(1)", CellError.Value)]
    [InlineData("NOT()", CellError.Value)]
    [InlineData("DATE(10000,1,1)", CellError.Number)]
    [InlineData("DATE(1900,1,-1)", CellError.Number)]
    [InlineData("A1:A3", CellError.Value)]
    [InlineData("SUM({1,2}+{1,2,3})", CellError.NotAvailable)]
    public void ErrorsAreValues(string formula, CellError expected)
    {
        Assert.Equal(CellValue.FromError(expected), Eval(formula, "D9"));
    }

    [Theory]
    [InlineData("\"a\"=\"A\"", true)]
    [InlineData("\"a\"<\"B\"", true)]
    [InlineData("1<\"0\"", true)]
    [InlineData("\"z\"<TRUE", true)]
    [InlineData("FALSE<TRUE", true)]
    [InlineData("0.1+0.2=0.3", true)]
    [InlineData("1=1.0000000000000002", true)]
    [InlineData("1=1.00000000000001", false)]
    [InlineData("A9=0", true)]
    [InlineData("A9=\"\"", true)]
    [InlineData("A9=FALSE", true)]
    [InlineData("\"1\"=1", false)]
    [InlineData("AND(A1:A3,TRUE)", true)]
    [InlineData("AND(A1,0)", false)]
    [InlineData("OR(B1:B2)", true)]
    [InlineData("AND(TRUE,)", false)]
    [InlineData("NOT(\"false\")", true)]
    [InlineData("IF(FALSE,1)", false)]
    public void ComparisonsFollowExcel(string formula, bool expected)
    {
        Assert.Equal(CellValue.FromBoolean(expected), Eval(formula));
    }

    [Theory]
    [InlineData("SUM(IF({1,0},1,IF({1,0},2,1+(2+3))))", 7)]
    [InlineData("SUM(IF({1,0},IF({0,1},1+(2+3),4),IF({1,0},2,1+(2+3))))", 10)]
    [InlineData("SUM(IFERROR({1,#N/A},IFERROR({#N/A,2},1+(2+3))))", 3)]
    [InlineData("SUM(IFNA({#N/A,1},IF({1,0},2,3)))", 3)]
    [InlineData("SUM(IF({1,0},IFERROR({#N/A,1},1+(2+3)),1))", 7)]
    public void ArrayConditionsRunBothBranches(string formula, double expected) => Assert.Equal(expected, Eval(formula).Number);

    [Fact]
    public void DeeplyNestedArrayConditionsFitTheirStack()
    {
        const int Depth = 60;
        string ifs = string.Concat(Enumerable.Repeat("IF({1,0},1,", Depth)) + "1+(2+(3+4))" + new string(')', Depth);
        Assert.Equal(11, Eval($"SUM({ifs})").Number);

        string errors = string.Concat(Enumerable.Repeat("IFERROR({#N/A,1},", Depth)) + "1+(2+{3,4})" + new string(')', Depth);
        Assert.Equal(7, Eval($"SUM({errors})").Number);
    }

    [Fact]
    public void TextComesOutAsInExcel()
    {
        Assert.Equal("a1TRUE", Eval("\"a\"&A1&TRUE&A9").Text);
        Assert.Equal("0.333333333333333", Eval("1/3&\"\"").Text);
        Assert.Equal("1E+20", Eval("1E20&\"\"").Text);
        Assert.Equal("text", Eval("B1").Text);
    }

    [Fact]
    public void RangesMeetTheFormulasCell()
    {
        Assert.Equal(2, Eval("A1:A3", "C2").Number);
        Assert.Equal(3, Eval("A1:A3+1", "C2").Number);
        Assert.Equal(2, Eval("@A1:A3", "C2").Number);
        Assert.Equal(2, Eval("_xlfn.SINGLE(A1:A3)", "C2").Number);
        Assert.Equal(1, Eval("A1:C1", "A7").Number);
        Assert.Equal(CellError.Value, Eval("A1:A3", "C9").Error);
        Assert.Equal(5, Eval("ROW()", "C5").Number);
        Assert.Equal(3, Eval("COLUMN()", "C5").Number);

        // An empty cell shows 0, as a formula pointing at one does.
        Assert.Equal(0, Eval("A9").Number);
    }

    [Fact]
    public void SheetsAndThreeDimensionalReferencesResolve()
    {
        Workbook book = Workbook.Create();
        for (int i = 1; i <= 3; i++)
        {
            Worksheet sheet = i == 1 ? book.Worksheets[0] : book.Worksheets.Add();
            sheet.Name = $"Q {i}";
            sheet["A1"].Value = i * 10;
        }

        Worksheet first = book.Worksheets[0];

        Assert.Equal(60, first.Evaluate("SUM('Q 1:Q 3'!A1)").Number);
        Assert.Equal(50, first.Evaluate("SUM('Q 3:Q 2'!A1)").Number);
        Assert.Equal(20, first.Evaluate("'Q 2'!A1").Number);
        Assert.Equal(CellError.Value, first.Evaluate("'Q 1:Q 3'!A1").Error);
    }

    [Fact]
    public void NamesStandForTheirFormulas()
    {
        Worksheet sheet = Sheet();
        Workbook book = sheet.Workbook;
        book.Names.Add("Rate", "Sheet1!$A$2");
        book.Names.Add("Values", "Sheet1!$A$1:$A$3");
        book.Names.Add("Twice", "Rate*2");
        book.Names.Add("Above", "Sheet1!A1048576");
        book.Names.Add("Loop", "Loop+1");
        book.Names.Add("Rate", "100", sheet);

        Assert.Equal(100, sheet.Evaluate("Rate").Number);
        Assert.Equal(6, sheet.Evaluate("SUM(Values)").Number);
        Assert.Equal(200, sheet.Evaluate("Twice").Number);
        Assert.Equal(2, sheet.Evaluate("Above", CellAddress.Parse("A3")).Number);
        Assert.Equal(CellError.Name, sheet.Evaluate("Loop").Error);
    }

    [Fact]
    public void TablesAreReachedByStructuredReferences()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "Name";
        sheet["B1"].Value = "Score";
        string[] names = ["Ann", "Bo", "Cy"];
        for (int i = 0; i < 3; i++)
        {
            sheet.Cells[i + 1, 0].Value = names[i];
            sheet.Cells[i + 1, 1].Value = i + 1;
        }

        SheetTable table = sheet.Tables.Add("A1:B4", "Scores");

        Assert.Equal(6, sheet.Evaluate("SUM(Scores[Score])").Number);
        Assert.Equal(6, sheet.Evaluate("SUM(Scores)").Number);
        Assert.Equal(6, sheet.Evaluate("SUM(Scores[[#All],[Score]])").Number);
        Assert.Equal("Score", sheet.Evaluate("Scores[[#Headers],[Score]]").Text);
        Assert.Equal(2, sheet.Evaluate("Scores[@Score]", CellAddress.Parse("C3")).Number);
        Assert.Equal(2, sheet.Evaluate("[@Score]*1", CellAddress.Parse("B3")).Number);
        Assert.Equal("Bo", sheet.Evaluate("Scores[[#This Row],[Name]]", CellAddress.Parse("D3")).Text);
        Assert.Equal(CellError.Value, sheet.Evaluate("Scores[@Score]", CellAddress.Parse("C9")).Error);
        Assert.Equal(CellError.Reference, sheet.Evaluate("Scores[#Totals]").Error);
        Assert.Equal(CellError.Reference, sheet.Evaluate("Scores[Nope]").Error);
        Assert.Equal(CellError.Reference, sheet.Evaluate("Nope[Score]").Error);

        table.ShowTotalsRow = true;
        Assert.Equal(3, sheet.Evaluate("SUM(Scores[Score])").Number);
        Assert.Equal(3, sheet.Evaluate("Scores[[#Totals],[Score]]").Number);
    }

    [Fact]
    public void FormulaCellsGiveTheirLastResults()
    {
        Worksheet sheet = Sheet();
        sheet["C1"].Formula = "A1*100";

        // Evaluate reads other cells as they are; the formula in C1 has no result until the workbook is calculated.
        Assert.Equal(0, sheet.Evaluate("C1").Number);
        Assert.Equal(1, sheet.Evaluate("A1").Number);
    }

    [Fact]
    public void DatesFollowTheWorkbooksDateSystem()
    {
        Worksheet sheet = Sheet();
        sheet.Workbook.Date1904 = true;

        Assert.Equal(0, sheet.Evaluate("DATE(1904,1,1)").Number);
        Assert.Equal(45366 - 1462, sheet.Evaluate("DATE(2024,3,15)").Number);
        Assert.Equal(45366 - 1462, sheet.Evaluate("\"2024-03-15\"+0").Number);
        Assert.Equal(CellError.Number, sheet.Evaluate("DATE(1903,12,31)").Error);
    }

    [Fact]
    public void EvaluateRefusesBrokenFormulas()
    {
        Worksheet sheet = Sheet();

        Assert.Throws<FormulaException>(() => sheet.Evaluate("SUM(A1"));
        Assert.Throws<ArgumentNullException>(() => sheet.Evaluate(null!));
    }
}
