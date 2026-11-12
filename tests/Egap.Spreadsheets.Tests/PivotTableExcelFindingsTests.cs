using System.IO.Compression;
using System.Xml.Linq;

using Egap.Spreadsheets.Printing;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// What Excel does with pivot tables Egap made, found by opening, refreshing and printing them in Excel
/// (<c>tools/excel-pivot-create-gate.ps1</c>): field names, blank and empty items, the cache's item flags, variance, the page
/// fields' place, captions, label indents, the pivot styles and the column widths.
/// </summary>
public sealed class PivotTableExcelFindingsTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly string[] Kinds = ["Light", "Medium", "Dark"];

    [Fact]
    public void FieldsAreNamedByTheHeadersRawTextAndFoundTrimmed()
    {
        Workbook book = Source(["Name ", "Amount"], [["A", 1], ["B", 2]]);
        Worksheet sheet = book.Worksheets[0];
        sheet.PivotTables.Add("E1", "Sheet1!A1:B3", new PivotTableOptions { Rows = { "Name" }, Values = { new("Amount") } });
        XDocument cache = Part(book, "xl/pivotCache/pivotCacheDefinition1.xml");
        Assert.Equal(["Name ", "Amount"], cache.Descendants(Main + "cacheField").Select(f => (string)f.Attribute("name")!));
    }

    [Fact]
    public void AnEmptyTextIsAnItemWithAnEmptyLabel()
    {
        Workbook book = Source(["Name", "Amount"], [["A", 1], ["", 2], [null, 4]]);
        Worksheet sheet = book.Worksheets[0];
        sheet.PivotTables.Add("E1", "Sheet1!A1:B4", new PivotTableOptions { Rows = { "Name" }, Values = { new("Amount") } });

        // The items sort as Excel's: the empty text first, then A, then the blank.
        Assert.Equal(CellValueKind.Empty, sheet[1, 4].Value.Kind);
        Assert.Equal(2.0, sheet[1, 5].Value.Number);
        Assert.Equal("A", sheet[2, 4].Value.ToString());
        Assert.Equal("(blank)", sheet[3, 4].Value.ToString());
        XElement items = Part(book, "xl/pivotCache/pivotCacheDefinition1.xml").Descendants(Main + "sharedItems").First();
        Assert.Contains(items.Elements(Main + "s"), s => (string)s.Attribute("v")! == string.Empty);
    }

    [Fact]
    public void DatesWithNumbersOrBlanksGetTheFlagsExcelOpens()
    {
        Workbook book = Source(["Day", "Mixed", "Amount"], [[45000.0, 45000.0, 1], [null, 3.5, 2], [45001.0, null, 3]]);
        Worksheet sheet = book.Worksheets[0];
        for (int r = 1; r <= 3; r++)
        {
            sheet[r, 0].NumberFormat = "yyyy-mm-dd";
        }

        sheet[1, 1].NumberFormat = "yyyy-mm-dd";
        sheet.PivotTables.Add("F1", "Sheet1!A1:C4", new PivotTableOptions { Rows = { "Day", "Mixed" }, Values = { new("Amount") } });
        XElement[] items = [.. Part(book, "xl/pivotCache/pivotCacheDefinition1.xml").Descendants(Main + "sharedItems")];

        // Dates and blanks: no non-dates.
        Assert.Equal("1", (string?)items[0].Attribute("containsDate"));
        Assert.Equal("0", (string?)items[0].Attribute("containsNonDate"));

        // Dates and numbers: Excel will not open number flags beside the date flag.
        Assert.Equal("1", (string?)items[1].Attribute("containsDate"));
        Assert.Null(items[1].Attribute("containsNumber"));
        Assert.Null(items[1].Attribute("minValue"));
    }

    [Fact]
    public void VarianceOfEqualValuesIsZeroAndNotANumberNever()
    {
        Workbook book = Source(["Name", "Amount"], [["A", 0.1], ["A", 0.1], ["A", 0.1], ["B", 1e9 + 0.3], ["B", 1e9 + 0.3]]);
        Worksheet sheet = book.Worksheets[0];
        sheet.PivotTables.Add("E1", "Sheet1!A1:B6", new PivotTableOptions
        {
            Rows = { "Name" },
            Values = { new("Amount", PivotFunction.StandardDeviation), new("Amount", PivotFunction.VariancePopulation) },
        });
        for (int r = 1; r <= 2; r++)
        {
            Assert.Equal(0.0, sheet[r, 5].Value.Number, 6);
            Assert.True(sheet[r, 5].Value.Number >= 0);
            Assert.True(sheet[r, 6].Value.Number >= 0);
        }
    }

    [Fact]
    public void FiltersCountTheirRowsInTheLocation()
    {
        Workbook book = Source(["Name", "Kind", "Amount"], [["A", "X", 1], ["B", "Y", 2]]);
        book.Worksheets[0].PivotTables.Add("E1", "Sheet1!A1:C3", new PivotTableOptions { Filters = { "Kind" }, Rows = { "Name" }, Values = { new("Amount") } });
        XElement location = Part(book, "xl/pivotTables/pivotTable1.xml").Descendants(Main + "location").Single();
        Assert.Equal("1", (string?)location.Attribute("rowPageCount"));
        Assert.Equal("1", (string?)location.Attribute("colPageCount"));
        Assert.Equal("E3:F6", (string?)location.Attribute("ref"));
    }

    [Fact]
    public void CaptionsAndTrueFalseItemsAreAsExcelWritesThem()
    {
        Workbook book = Source(["Flag", "Amount"], [[true, 1], [false, 2]]);
        Worksheet sheet = book.Worksheets[0];
        sheet.PivotTables.Add("E1", "Sheet1!A1:B3", new PivotTableOptions { Rows = { "Flag" }, Values = { new("Amount") } });
        Assert.Equal(CellValueKind.Text, sheet[1, 4].Value.Kind);
        Assert.Equal("FALSE", sheet[1, 4].Value.ToString());

        // One value and no row fields: the value's name over its total, no labels.
        PivotTable alone = sheet.PivotTables.Add("H1", "Sheet1!A1:B3", new PivotTableOptions { Values = { new("Amount") } });
        Assert.Equal(new RangeAddress(0, 7, 1, 7), alone.Range);
        Assert.Equal("Sum of Amount", sheet[0, 7].Value.ToString());
        Assert.Equal(3.0, sheet[1, 7].Value.Number);

        // Values alone on the rows: "Values" heads them.
        sheet.PivotTables.Add("K1", "Sheet1!A1:B3", new PivotTableOptions { Values = { new("Amount"), new("Amount", PivotFunction.Max) }, ValuesOnRows = true });
        Assert.Equal("Values", sheet[0, 10].Value.ToString());
    }

    [Fact]
    public void CompactLabelsAreLeftAlignedAndIndentedByLevel()
    {
        Workbook book = Source(["Region", "Rep", "Amount"], [["N", "Ann", 1], ["N", "Bob", 2], ["S", "Ann", 3]]);
        Worksheet sheet = book.Worksheets[0];
        sheet.PivotTables.Add("E1", "Sheet1!A1:C4", new PivotTableOptions { Rows = { "Region", "Rep" }, Values = { new("Amount") } });
        Assert.Equal(["N", "Ann", "Bob", "S", "Ann", "Grand Total"], Enumerable.Range(1, 6).Select(r => sheet[r, 4].Value.ToString()));
        Assert.Equal([0, 1, 1, 0, 1, 0], Enumerable.Range(1, 6).Select(r => sheet[r, 4].Style.Alignment.Indent));
        Assert.All(Enumerable.Range(1, 6), r => Assert.Equal(CellHorizontalAlignment.Left, sheet[r, 4].Style.Alignment.Horizontal));

        // Printed, every item starts past the buttons, a button's width a level; the grand total does not.
        PivotFormats formats = PivotFormats.For(sheet)!;
        Assert.Equal([13, 26, 26, 13, 26, null], Enumerable.Range(1, 6).Select(r => formats.LabelIndent(r, 4, sheet[r, 4].Style.Alignment.Indent)));
    }

    [Fact]
    public void TheStyleFormatsTheTablesParts()
    {
        Workbook book = Source(["Region", "Rep", "Amount"], [["N", "Ann", 1], ["N", "Bob", 2], ["S", "Ann", 3]]);
        Worksheet sheet = book.Worksheets[0];
        sheet.PivotTables.Add("E1", "Sheet1!A1:C4", new PivotTableOptions { Rows = { "Region", "Rep" }, Values = { new("Amount") } });
        PivotFormats formats = PivotFormats.For(sheet)!;
        CellStyle Style(int row, int column) => formats.Style(row, column, sheet[row, column].Style);

        // PivotStyleLight16: a bold header with a line under it, bold outer items, a bold grand total with a line over it.
        Assert.True(Style(0, 4).Font.Bold);
        Assert.Equal(CellBorderStyle.Thin, Style(0, 5).Borders.Bottom.Style);
        Assert.True(Style(1, 4).Font.Bold);
        Assert.False(Style(2, 4).Font.Bold);
        Assert.True(Style(6, 5).Font.Bold);
        Assert.NotEqual(CellBorderStyle.None, Style(6, 5).Borders.Top.Style);
        Assert.Equal(sheet[9, 9].Style, Style(9, 9));

        // A cell's own fill wins over the style's.
        sheet[0, 4].Style = sheet[0, 4].Style with { Fill = CellFill.Solid(CellColor.FromRgb(255, 0, 0)) };
        Assert.Equal(CellColor.FromRgb(255, 0, 0), Style(0, 4).Fill.Foreground);

        Assert.Equal(84, Enumerable.Range(1, 28).Sum(n => Kinds.Count(k => PivotStyles.Get("PivotStyle" + k + n) is not null)));
        Assert.Null(PivotStyles.Get("PivotStyleLight29"));
        Assert.Null(PivotFormats.For(Source(["A"], [["x"]]).Worksheets[0]));
    }

    [Fact]
    public void AutoFormattingFitsTheColumnsWithRoomForTheButtons()
    {
        Workbook book = Source(["Region", "Amount"], [["Somewhere rather far away", 1], ["N", 2]]);
        Worksheet sheet = book.Worksheets[0];
        sheet.PivotTables.Add("E1", "Sheet1!A1:B3", new PivotTableOptions { Rows = { "Region" }, Values = { new("Amount") } });
        double labels = sheet.Columns[4].Width ?? 0, values = sheet.Columns[5].Width ?? 0;
        Assert.True(labels > 20, $"label column {labels}");
        Assert.True(values > 10 && values < labels, $"value column {values}");
    }

    private static Workbook Source(string[] headers, object?[][] rows)
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        for (int c = 0; c < headers.Length; c++)
        {
            sheet[0, c].Value = headers[c];
        }

        for (int r = 0; r < rows.Length; r++)
        {
            for (int c = 0; c < rows[r].Length; c++)
            {
                sheet[r + 1, c].Value = rows[r][c] switch
                {
                    null => CellValue.Empty,
                    string text => CellValue.FromText(text),
                    bool flag => CellValue.FromBoolean(flag),
                    object number => CellValue.FromNumber(Convert.ToDouble(number, System.Globalization.CultureInfo.InvariantCulture)),
                };
            }
        }

        return book;
    }

    private static XDocument Part(Workbook book, string name)
    {
        using MemoryStream stream = new();
        book.Save(stream);
        using ZipArchive zip = new(new MemoryStream(stream.ToArray()));
        return XDocument.Load(zip.GetEntry(name)!.Open());
    }
}
