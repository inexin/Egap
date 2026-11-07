using System.Xml.Linq;

using Egap.Documents.Charts;

namespace Egap.Spreadsheets.Tests;

/// <summary>Renaming a sheet, with the formulas and results read from Excel: tools in the pull request's description.</summary>
public sealed class SheetRenameTests
{
    private static Workbook Book()
    {
        Workbook book = Workbook.CreateEmpty();
        book.Worksheets.Add("Old");
        book.Worksheets.Add("Other");
        book.Worksheets.Add("Sheet12");
        book.Worksheets.Add("Older");
        return book;
    }

    [Theory]
    [InlineData("New", "Old!A1", "New!A1")]
    [InlineData("New", "old!A1+OLD!$B$2", "New!A1+New!$B$2")]
    [InlineData("New", "Old!A1:B2", "New!A1:B2")]
    [InlineData("New", "SUM(Old!A:A,Old!1:1)", "SUM(New!A:A,New!1:1)")]
    [InlineData("New", "SUM(Old:Other!A1)", "SUM(New:Other!A1)")]
    [InlineData("New", "\"Old!A1\"&Old!A1", "\"Old!A1\"&New!A1")]
    [InlineData("New", "Other!A1", "Other!A1")]
    [InlineData("New", "Sheet12!A1", "Sheet12!A1")]
    [InlineData("New", "Older!A1", "Older!A1")]
    [InlineData("New", "INDIRECT(\"Old!A1\")", "INDIRECT(\"Old!A1\")")]
    [InlineData("New", "Old!nm", "New!nm")]
    [InlineData("New", "Old!A1&\"x\"\"Old!A1\"", "New!A1&\"x\"\"Old!A1\"")]
    [InlineData("My sheet", "Old!A1", "'My sheet'!A1")]
    [InlineData("My sheet", "SUM(Old!A:A,Old!1:1)", "SUM('My sheet'!A:A,'My sheet'!1:1)")]
    [InlineData("My sheet", "SUM(Old:Other!A1)", "SUM('My sheet:Other'!A1)")]
    [InlineData("My sheet", "Old!nm", "'My sheet'!nm")]
    [InlineData("O'Neil", "Old!A1", "'O''Neil'!A1")]
    [InlineData("O'Neil", "SUM(Old:Other!A1)", "SUM('O''Neil:Other'!A1)")]
    [InlineData("A1", "Old!A1", "'A1'!A1")]
    [InlineData("A1", "SUM(Old:Other!A1)", "SUM('A1:Other'!A1)")]
    [InlineData("Sheet1.2", "SUM(Old:Other!A1)", "SUM(Sheet1.2:Other!A1)")]
    [InlineData("TRUE", "Old!A1", "'TRUE'!A1")]
    [InlineData("2024", "SUM(Old:Other!A1)", "SUM('2024:Other'!A1)")]
    public void AFormulaFollowsTheSheetAsInExcel(string name, string before, string after)
    {
        Workbook book = Book();
        book.Worksheets["Other"]["C1"].Formula = before;
        book.Worksheets["Old"].Name = name;
        Assert.Equal(after, book.Worksheets["Other"]["C1"].Formula);
    }

    [Theory]
    [InlineData("'Old'!A1", "New!A1")]
    [InlineData("'Other:Old'!A1", "Other:New!A1")]
    [InlineData("[1]Old!A1", "[1]Old!A1")]
    [InlineData("'[1]Old'!A1", "'[1]Old'!A1")]
    [InlineData("'[1]Old:Other'!A1", "'[1]Old:Other'!A1")]
    [InlineData("Table1[Old!x]", "Table1[Old!x]")]
    [InlineData("Table1[[#All],[Old!x]]+Old!A1", "Table1[[#All],[Old!x]]+New!A1")]
    [InlineData("Table1['[Old!x]", "Table1['[Old!x]")]
    [InlineData("\"Old!A1", "\"Old!A1")]
    [InlineData("'Old", "'Old")]
    [InlineData("'Old'", "'Old'")]
    [InlineData("[Old!A1", "[Old!A1")]
    [InlineData("Old:", "Old:")]
    [InlineData("Old:!A1", "Old:!A1")]
    [InlineData("Old!", "New!")]
    [InlineData("!", "!")]
    [InlineData("", "")]
    public void WhatIsNotThisSheetStays(string before, string after) =>
        Assert.Equal(after, Formulas.SheetRename.Apply(before, "Old", "New"));

    [Fact]
    public void AFormulaStillCalculatesAfterwards()
    {
        Workbook book = Book();
        book.Worksheets["Old"]["A1"].Value = 41;
        Cell cell = book.Worksheets["Other"]["A1"];
        cell.Formula = "Old!A1+1";
        book.Worksheets["Old"].Name = "My sheet";
        book.Calculate();
        Assert.Equal(42d, cell.Value);
    }

    [Fact]
    public void ACalculationBeforeTheRenameDoesNotStick()
    {
        Workbook book = Book();
        book.Worksheets["Old"]["A1"].Value = 41;
        Cell cell = book.Worksheets["Other"]["A1"];
        cell.Formula = "Old!A1+1";
        book.Calculate();
        book.Worksheets["Old"].Name = "New";
        book.Worksheets["New"]["A1"].Value = 1;
        book.Calculate();
        Assert.Equal(2d, cell.Value);
    }

    [Fact]
    public void AFormulaOnTheSheetItselfFollows()
    {
        Workbook book = Book();
        book.Worksheets["Old"]["A1"].Formula = "Old!B1";
        book.Worksheets["Old"].Name = "New";
        Assert.Equal("New!B1", book.Worksheets["New"]["A1"].Formula);
    }

    [Fact]
    public void TheSameNameChangesNothingAndCaseFollows()
    {
        Workbook book = Book();
        book.Worksheets["Other"]["A1"].Formula = "OLD!A1";
        book.Worksheets["Old"].Name = "Old";
        Assert.Equal("OLD!A1", book.Worksheets["Other"]["A1"].Formula);
        book.Worksheets["Old"].Name = "OLDIE";
        book.Worksheets["OLDIE"].Name = "oldie";
        Assert.Equal("oldie!A1", book.Worksheets["Other"]["A1"].Formula);
    }

    [Fact]
    public void ANameKeepsItsOriginAndFollows()
    {
        Workbook book = Book();
        Worksheet old = book.Worksheets["Old"];
        DefinedName name = book.Names.Add("data", "Old!$A$1:$B$2");
        DefinedName local = book.Names.Add("mine", "Old!$A$1", old);
        old.PageSetup.PrintArea = [RangeAddress.Parse("A1:C3")];
        old.Name = "My sheet";
        Assert.Equal("'My sheet'!$A$1:$B$2", name.Formula);
        Assert.Equal("'My sheet'!$A$1", local.Formula);
        Assert.True(name.Typed);
        Assert.Equal([RangeAddress.Parse("A1:C3")], old.PageSetup.PrintArea);
    }

    [Fact]
    public void ANameReadFromAFileStaysRead()
    {
        Workbook book = Book();
        book.Names.Add("data", "Old!$A$1");
        Workbook read = RoundTrip(book);
        read.Worksheets["Old"].Name = "New";
        Assert.Equal("New!$A$1", read.Names["data"].Formula);
        Assert.False(read.Names["data"].Typed);
    }

    [Fact]
    public void RulesTablesAndLinksFollow()
    {
        Workbook book = Book();
        Worksheet other = book.Worksheets["Other"];
        DataValidation validation = other.DataValidations.Add(RangeAddress.Parse("A1"), DataValidationType.List, "Old!$A$1:$A$3");
        ConditionalFormatRule rule = ConditionalFormatRule.Expression("A1>Old!$A$1", new CellStyle());
        other.ConditionalFormats.Add("A1", rule);
        Hyperlink link = other.Hyperlinks.AddInternal(RangeAddress.Parse("B1"), "Old!A1");
        other["E1"].Value = "a";
        other["F1"].Value = "b";
        SheetTable table = other.Tables.Add("E1:F3");
        table.Columns[1].CalculatedFormula = "Old!A1+1";
        table.Columns[1].TotalsRowFormula = "SUM(Old!A1)";
        book.Worksheets["Old"].Name = "New";
        Assert.Equal("New!$A$1:$A$3", validation.Formula1);
        Assert.Equal("A1>New!$A$1", rule.Formulas[0]);
        Assert.Equal("New!A1", link.Location);
        Assert.Equal("New!A1+1", table.Columns[1].CalculatedFormula);
        Assert.Equal("SUM(New!A1)", table.Columns[1].TotalsRowFormula);

        Workbook read = RoundTrip(book);
        Assert.Equal("New!$A$1:$A$3", read.Worksheets["Other"].DataValidations.Find(new CellAddress(0, 0))!.Formula1);
        Assert.Equal("New!A1", read.Worksheets["Other"].Hyperlinks.Find(new CellAddress(0, 1))!.Location);
    }

    [Fact]
    public void AKeptExtensionFollows()
    {
        XNamespace xm = "http://schemas.microsoft.com/office/excel/2006/main";
        Workbook book = Book();
        XElement kept = new("extLst", new XElement("ext", new XElement(xm + "f", "Old!A1")));
        Worksheet other = book.Worksheets["Other"];
        other.Source = new Xlsx.SheetSource(null!, "", 1);
        other.Source.Kept.Add((1, kept));
        book.Worksheets["Old"].Name = "New";
        Assert.Equal("New!A1", (string?)kept.Descendants(xm + "f").Single());
    }

    [Fact]
    public void AChartFollowsAndSavesWhatItNowReads()
    {
        Workbook book = Book();
        Worksheet old = book.Worksheets["Old"];
        old["A1"].Value = "x";
        old["B1"].Value = "y";
        old["A2"].Value = "p";
        old["B2"].Value = 1;
        old["A3"].Value = "q";
        old["B3"].Value = 2;
        old.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("D1:J15"), RangeAddress.Parse("A1:B3"));
        Workbook read = RoundTrip(book);
        read.Worksheets["Old"].Name = "My sheet";
        Workbook again = RoundTrip(read);
        ChartSeries series = again.Worksheets["My sheet"].Drawings[0].Chart!.Plots[0].Series[0];
        Assert.Equal("'My sheet'!$B$2:$B$3", series.Values.Reference);
        Assert.Equal("'My sheet'!$A$2:$A$3", series.Categories.Reference);
        Assert.Equal("'My sheet'!$B$1", series.NameReference);
    }

    private static Workbook RoundTrip(Workbook book)
    {
        MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Workbook.Load(stream);
    }
}
