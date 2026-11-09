using System.Xml.Linq;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Filters against Excel: <c>tools/excel-autofilter-oracle.ps1</c> set a filter on each sheet of <c>Oracle/autofilter.xlsx</c>
/// and recorded in <c>Oracle/autofilter.excel.tsv</c> the rows Excel hid; Egap shows every row and applies the filter again.
/// </summary>
public sealed class AutoFilterTests
{
    private static readonly string[][] Excel = [.. File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Oracle", "autofilter.excel.tsv")).Skip(1).Select(line => line.Split('\t'))];

    private static readonly string Book = Path.Combine(AppContext.BaseDirectory, "Oracle", "autofilter.xlsx");

    public static TheoryData<string> Cases()
    {
        TheoryData<string> cases = [];
        foreach (string[] line in Excel)
        {
            cases.Add(line[0]);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void HidesTheRowsExcelHides(string name)
    {
        string[] line = Array.Find(Excel, l => l[0] == name)!;
        Workbook book = Workbook.Load(Book);
        Worksheet sheet = book.Worksheets[name];
        AutoFilter filter = sheet.AutoFilter ?? sheet.Tables.Single().AutoFilter!;
        Assert.Equal(line[2], Hidden(sheet));
        for (int row = 1; row <= 15; row++)
        {
            sheet.Rows[row].Hidden = false;
        }

        int shown = filter.Apply();
        Assert.Equal(line[2], Hidden(sheet));
        Assert.Equal(15 - (line[2].Length == 0 ? 0 : line[2].Split(',').Length), shown);
    }

    [Fact]
    public void ShowAllClearsTheCriteriaAndShowsTheRows()
    {
        Workbook book = Workbook.Load(Book);
        Worksheet sheet = book.Worksheets["c1"];
        AutoFilter filter = sheet.AutoFilter!;
        Assert.True(filter.HasCriteria);
        filter.ShowAll();
        Assert.False(filter.HasCriteria);
        Assert.Equal("", Hidden(sheet));
        Assert.Equal(RangeAddress.Parse("A1:F16"), filter.Range);

        using MemoryStream saved = new();
        book.Save(saved);
        saved.Position = 0;
        Worksheet again = Workbook.Load(saved).Worksheets["c1"];
        Assert.False(again.AutoFilter!.HasCriteria);
        Assert.Equal(15, again.AutoFilter.Apply());
    }

    [Fact]
    public void ASheetWithoutAFilterHasNone()
    {
        Workbook book = Workbook.CreateEmpty();
        Assert.Null(book.Worksheets.Add("S").AutoFilter);
    }

    [Theory]
    [InlineData("today", "2026-11-08", "2026-11-09")]
    [InlineData("thisWeek", "2026-11-08", "2026-11-15")]
    [InlineData("lastMonth", "2026-10-01", "2026-11-01")]
    [InlineData("nextQuarter", "2027-01-01", "2027-04-01")]
    [InlineData("yearToDate", "2026-01-01", "2026-11-09")]
    public void DatePeriodsCountFromToday(string type, string first, string end)
    {
        (DateTime First, DateTime End) period = AutoFilter.Period(type, new DateTime(2026, 11, 8))!.Value;
        Assert.Equal(DateTime.Parse(first, System.Globalization.CultureInfo.InvariantCulture), period.First);
        Assert.Equal(DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture), period.End);
    }

    private static string Hidden(Worksheet sheet) =>
        string.Join(',', Enumerable.Range(1, 15).Where(r => sheet.Rows[r].Hidden).Select(r => r + 1));
}
