using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Pivot tables made in code: each is made again, with the options <c>tools/excel-pivot-oracle.ps1</c> gave Excel, from the
/// source of <c>Oracle/pivots.xlsx</c>, and must fill the same cells as Excel's, then save as parts a reload reads back.
/// </summary>
public sealed class PivotTableCreateTests
{
    private static readonly string Book = Path.Combine(AppContext.BaseDirectory, "Oracle", "pivots.xlsx");

    private static readonly CultureInfo Norwegian = CultureInfo.GetCultureInfo("nb-NO");

    public static TheoryData<string> Sheets() => ["Compact", "CompactColumns", "Outline", "Tabular", "Functions", "Dates", "ValuesOnly"];

    [Theory]
    [MemberData(nameof(Sheets))]
    public void AMadeTableFillsTheCellsExcelDoes(string sheet)
    {
        Workbook book = Load();
        Worksheet excel = book.Worksheets[sheet];
        Worksheet made = book.Worksheets.Add("Made");
        PivotTable table = made.PivotTables.Add(First(excel, sheet), "Data!A1:G61", Options(sheet));
        Assert.Equal(excel.PivotTables.Single().Range, table.Range);
        Assert.Equal(Cells(excel), Cells(made));

        Workbook saved = RoundTrip(book);
        Assert.Equal(Cells(excel), Cells(saved.Worksheets["Made"]));
        PivotTable reread = saved.Worksheets["Made"].PivotTables.Single();
        Assert.Equal("PivotTable1", reread.Name);
        Assert.Equal("Data!A1:G61", reread.Source);
        Assert.True(reread.CanRefresh);
    }

    [Theory]
    [MemberData(nameof(Sheets))]
    public void AMadeTableRefreshesAfterReloadToTheSameCells(string sheet)
    {
        Workbook book = Load();
        Worksheet made = book.Worksheets.Add("Made");
        made.PivotTables.Add(First(book.Worksheets[sheet], sheet), "Data!A1:G61", Options(sheet));
        Workbook saved = RoundTrip(book);
        saved.Culture = Norwegian;
        string before = Cells(saved.Worksheets["Made"]);
        Assert.Equal(12, saved.RefreshPivotTables());
        Assert.Equal(before, Cells(RoundTrip(saved).Worksheets["Made"]));
    }

    [Fact]
    public void ANewBookGetsItsPivotPartsAndTheOldOnesStay()
    {
        Workbook book = Load();
        Worksheet made = book.Worksheets.Add("Made");
        made.PivotTables.Add("B3", "Data!A1:G61", Options("Compact"), "Sales");
        made.PivotTables.Add("H3", "Data!A1:G61", Options("Dates"));
        using MemoryStream stream = new();
        book.Save(stream);
        using ZipArchive zip = new(new MemoryStream(stream.ToArray()));
        string[] parts = [.. zip.Entries.Select(e => e.FullName)];
        Assert.Equal(13, parts.Count(p => p.StartsWith("xl/pivotTables/pivotTable", StringComparison.Ordinal) && p.EndsWith(".xml", StringComparison.Ordinal)));
        Assert.Equal(3, parts.Count(p => p.StartsWith("xl/pivotCache/pivotCacheDefinition", StringComparison.Ordinal) && p.EndsWith(".xml", StringComparison.Ordinal)));

        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XDocument workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        int[] ids = [.. workbook.Descendants(main + "pivotCache").Select(c => (int)c.Attribute("cacheId")!)];
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(3, ids.Length);

        Workbook reread = Workbook.Load(new MemoryStream(stream.ToArray()));
        Assert.Equal(["Sales", "PivotTable1"], reread.Worksheets["Made"].PivotTables.Select(t => t.Name));
        Assert.Equal(new CellAddress(2, 1), reread.Worksheets["Made"].PivotTables["sales"].Range.First);
    }

    [Fact]
    public void AMadeTableInANewBookFollowsItsSource()
    {
        Workbook book = Workbook.Create();
        Worksheet data = book.Worksheets[0];
        data.Name = "Data";
        string[] regions = ["North", "South", "North", "East", "South", "North"];
        data[0, 0].Value = "Region";
        data[0, 1].Value = "Units";
        for (int r = 0; r < regions.Length; r++)
        {
            data[r + 1, 0].Value = regions[r];
            data[r + 1, 1].Value = r + 1;
        }

        Worksheet report = book.Worksheets.Add("Report");
        PivotTableOptions options = new() { Rows = { "region" }, Values = { new PivotValueField("Units") { NumberFormat = "0.0" } } };
        PivotTable table = report.PivotTables.Add(new CellAddress(0, 0), data.Range("A1:B7"), options);
        Assert.Equal(new RangeAddress(0, 0, 4, 1), table.Range);
        Assert.Equal("Row Labels", report[0, 0].Value.ToString());
        Assert.Equal("Sum of Units", report[0, 1].Value.ToString());
        Assert.Equal(["East", "North", "South", "Grand Total"], Enumerable.Range(1, 4).Select(r => report[r, 0].Value.ToString()));
        Assert.Equal([4.0, 10.0, 7.0, 21.0], Enumerable.Range(1, 4).Select(r => report[r, 1].Value.Number));

        data[7, 0].Value = "West";
        data[7, 1].Value = 9;
        table.Refresh();
        Assert.Equal("Data!A1:B7", table.Source);

        Workbook saved = RoundTrip(book);
        Worksheet again = saved.Worksheets["Report"];
        Assert.Equal("0.0", again[1, 1].NumberFormat);
        Assert.Equal(21.0, again[4, 1].Value.Number);
    }

    [Fact]
    public void AnExcelTableSourceIsReadByName()
    {
        Workbook book = Workbook.Create();
        Worksheet data = book.Worksheets[0];
        data[0, 0].Value = "Item";
        data[0, 1].Value = "Cost";
        data[1, 0].Value = "Pen";
        data[1, 1].Value = 2;
        data[2, 0].Value = "Ink";
        data[2, 1].Value = 5;
        data.Tables.Add("A1:B3", "Costs");
        PivotTableOptions options = new() { Rows = { "Item" }, Values = { new PivotValueField("Cost", PivotFunction.Max) { Name = "Most" } } };
        PivotTable table = data.PivotTables.Add("D1", "Costs", options);
        Assert.Equal("Costs", table.Source);
        Assert.Equal("Most", data[0, 4].Value.ToString());
        Assert.Equal(5.0, data[3, 4].Value.Number);
    }

    [Fact]
    public void BadOptionsAreRefusedAndNothingIsMade()
    {
        Workbook book = Load();
        Worksheet made = book.Worksheets.Add("Made");
        Assert.Throws<ArgumentException>(() => made.PivotTables.Add("A1", "Data!A1:G61", new PivotTableOptions { Rows = { "Nope" } }));
        Assert.Throws<ArgumentException>(() => made.PivotTables.Add("A1", "Data!A1:G61", new PivotTableOptions { Rows = { "Region" }, Columns = { "Region" } }));
        Assert.Throws<ArgumentException>(() => made.PivotTables.Add("A1", "Data!A1:G61", new PivotTableOptions()));
        Assert.Throws<ArgumentException>(() => made.PivotTables.Add("A1", "Nowhere", Options("Compact")));
        Assert.Throws<ArgumentException>(() => made.PivotTables.Add("A1", "Data!A1:G1", Options("Compact")));
        made.PivotTables.Add("A1", "Data!A1:G61", Options("Compact"), "Sales");
        Assert.Throws<ArgumentException>(() => made.PivotTables.Add("K1", "Data!A1:G61", Options("Compact"), "sales"));
        Assert.Throws<InvalidOperationException>(() => made.PivotTables.Add("B2", "Data!A1:G61", Options("Compact")));
        Assert.Single(made.PivotTables);
        Assert.Equal(12, RoundTrip(book).Worksheets.Sum(s => s.PivotTables.Count));
    }

    private static PivotTableOptions Options(string sheet) => sheet switch
    {
        "Compact" => new() { Rows = { "Region", "Product" }, Values = { new("Revenue") } },
        "CompactColumns" => new() { Rows = { "Region" }, Columns = { "Year" }, Values = { new("Units"), new("Revenue", PivotFunction.Average) } },
        "Outline" => new() { Layout = PivotTableLayout.Outline, Rows = { "Region", "Rep" }, Filters = { "Product" }, Values = { new("Units", PivotFunction.Count) } },
        "Tabular" => new()
        {
            Layout = PivotTableLayout.Tabular,
            Rows = { "Region", "Product" },
            Columns = { "Year" },
            Values = { new("Revenue"), new("Units", PivotFunction.Max) },
            ValuesOnRows = true,
        },
        "Functions" => new()
        {
            Rows = { "Product" },
            Values =
            {
                new("Units") { Name = "sum of Units" },
                new("Units", PivotFunction.Count) { Name = "count of Units" },
                new("Units", PivotFunction.Average) { Name = "average of Units" },
                new("Units", PivotFunction.Max) { Name = "max of Units" },
                new("Units", PivotFunction.Min) { Name = "min of Units" },
                new("Units", PivotFunction.Product) { Name = "product of Units" },
                new("Units", PivotFunction.CountNumbers) { Name = "countNums of Units" },
                new("Units", PivotFunction.StandardDeviation) { Name = "stdDev of Units" },
                new("Units", PivotFunction.StandardDeviationPopulation) { Name = "stdDevp of Units" },
                new("Units", PivotFunction.Variance) { Name = "var of Units" },
                new("Units", PivotFunction.VariancePopulation) { Name = "varp of Units" },
            },
        },
        "Dates" => new() { Layout = PivotTableLayout.Tabular, Rows = { "Day" }, Columns = { "Rep" }, Values = { new("Revenue", PivotFunction.Min) } },
        "ValuesOnly" => new() { Values = { new("Units"), new("Revenue") } },
        _ => throw new ArgumentOutOfRangeException(nameof(sheet)),
    };

    /// <summary>Where Excel's table starts on a sheet: its filters' first cell, a blank row above the table, or its own.</summary>
    private static CellAddress First(Worksheet sheet, string name)
    {
        CellAddress first = sheet.PivotTables.Single().Range.First;
        int filters = Options(name).Filters.Count;
        return filters == 0 ? first : new CellAddress(first.Row - filters - 1, first.Column);
    }

    private static Workbook Load()
    {
        Workbook book = Workbook.Load(Book);
        book.Culture = Norwegian;
        return book;
    }

    private static Workbook RoundTrip(Workbook book)
    {
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Workbook.Load(stream);
    }

    private static string Cells(Worksheet sheet)
    {
        StringBuilder text = new();
        for (int row = 0; row < 80; row++)
        {
            for (int column = 0; column < 14; column++)
            {
                CellValue value = sheet[row, column].Value;
                if (!value.IsEmpty)
                {
                    string shown = value.Kind == CellValueKind.Number
                        ? value.Number.ToString("G12", CultureInfo.InvariantCulture)
                        : value.ToString();
                    text.Append(new CellAddress(row, column)).Append('=').Append(shown).Append('\n');
                }
            }
        }

        return text.ToString();
    }
}
