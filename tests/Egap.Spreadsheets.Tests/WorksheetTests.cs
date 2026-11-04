namespace Egap.Spreadsheets.Tests;

/// <summary>Worksheets in a workbook: names, order, adding and removing.</summary>
public sealed class WorksheetTests
{
    [Fact]
    public void SheetsGetFreeNamesAndKeepTheirOrder()
    {
        Workbook book = Workbook.Create();
        Worksheet data = book.Worksheets.Add("Data");
        Worksheet third = book.Worksheets.Add();
        Worksheet first = book.Worksheets.Insert(0, "Summary");

        Assert.Equal(["Summary", "Sheet1", "Data", "Sheet3"], book.Worksheets.Select(sheet => sheet.Name));
        Assert.Same(data, book.Worksheets["data"]);
        Assert.Equal(3, third.Index);

        book.Worksheets.Move(first, 3);
        Assert.Equal(["Sheet1", "Data", "Sheet3", "Summary"], book.Worksheets.Select(sheet => sheet.Name));
        Assert.Throws<KeyNotFoundException>(() => book.Worksheets["Missing"]);
        Assert.False(book.Worksheets.TryGet("Missing", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A name that is longer than 31 ch")]
    [InlineData("Q1/Q2")]
    [InlineData("What?")]
    [InlineData("[x]")]
    [InlineData("'quoted'")]
    [InlineData("a:b")]
    [InlineData("SHEET1")]
    public void NamesExcelRefusesAreRefused(string name)
    {
        Workbook book = Workbook.Create();

        Assert.Throws<ArgumentException>(() => book.Worksheets.Add(name));
        Assert.Throws<ArgumentException>(() => book.Worksheets.Add("Other").Name = name);
    }

    [Fact]
    public void RenamingToTheSameNameInAnotherCaseIsAllowed()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];

        sheet.Name = "SHEET1";
        Assert.Equal("SHEET1", sheet.Name);
        sheet.Name = "It's fine";
        Assert.Equal("It's fine", sheet.ToString());
    }

    [Fact]
    public void ARemovedSheetCanNoLongerBeUsed()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets.Add("Gone");
        sheet["A1"].Value = "x";

        Assert.True(book.Worksheets.Remove(sheet));
        Assert.False(book.Worksheets.Remove(sheet));
        Assert.Throws<InvalidOperationException>(() => sheet.Workbook);
        Assert.Throws<InvalidOperationException>(() => sheet["A1"].Value);
        Assert.Throws<ArgumentException>(() => book.Worksheets.Move(sheet, 0));
        book.Worksheets.Add("Gone");
    }
}
