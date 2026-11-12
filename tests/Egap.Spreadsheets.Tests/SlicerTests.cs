using System.IO.Compression;
using System.Xml.Linq;

using Egami;
using Egami.Pixels;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Slicers: those Excel made in <c>Oracle/slicers.xlsx</c> (<c>tools/excel-slicer-oracle.ps1</c>) read as Excel left them, and
/// those made in code filter their pivot tables and save as the parts Excel writes.
/// </summary>
public sealed class SlicerTests
{
    private static readonly string Book = Path.Combine(AppContext.BaseDirectory, "Oracle", "slicers.xlsx");

    private static readonly XNamespace X14 = "http://schemas.microsoft.com/office/spreadsheetml/2009/9/main";

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [Fact]
    public void ExcelsSlicersReadAsExcelLeftThem()
    {
        Workbook book = Workbook.Load(Book);
        Slicer[] slicers = [.. book.Worksheets["Compact"].Drawings.Where(d => d.Kind == SheetDrawingKind.Slicer).Select(d => d.Slicer!)];
        Assert.Equal(["Region", "Year", "Product"], slicers.Select(s => s.Name));

        Slicer region = slicers[0];
        Assert.Equal("Region", region.Field);
        Assert.Null(region.StyleName);
        Assert.Equal(1, region.ColumnCount);
        Assert.True(region.ShowHeader);
        Assert.Equal(["East", "North", "South", "West", "(blank)"], region.Items);
        Assert.Equal(region.Items, region.SelectedItems);

        Slicer year = slicers[1];
        Assert.Equal("Sales year", year.Caption);
        Assert.Equal("SlicerStyleDark2", year.StyleName);
        Assert.Equal(2, year.ColumnCount);
        Assert.Equal(["2019", "2020", "2021"], year.Items);
        Assert.Equal(["2019", "2021"], year.SelectedItems);

        Slicer product = slicers[2];
        Assert.False(product.ShowHeader);
        Assert.Equal(["Compact", "Outline"], product.PivotTables.Select(t => t.Name));
        Assert.Equal(["Apples", "Pears", "Plums", "(blank)"], product.SelectedItems);
    }

    [Fact]
    public void ARefreshKeepsExcelsSelectionAndTheSlicerParts()
    {
        Workbook book = Workbook.Load(Book);
        string before = Cells(book.Worksheets["Compact"]);
        book.RefreshPivotTables();
        Assert.Equal(before, Cells(book.Worksheets["Compact"]));

        Workbook saved = RoundTrip(book);
        Slicer year = saved.Worksheets["Compact"].Drawings.Single(d => d.Name == "Year").Slicer!;
        Assert.Equal(["2019", "2021"], year.SelectedItems);
        Assert.Equal(3, saved.Worksheets["Compact"].Drawings.Count(d => d.Kind == SheetDrawingKind.Slicer));
    }

    [Fact]
    public void SelectingItemsFiltersEveryConnectedTable()
    {
        Workbook book = Workbook.Load(Book);
        Slicer product = book.Worksheets["Compact"].Drawings.Single(d => d.Name == "Product").Slicer!;
        string outline = Cells(book.Worksheets["Outline"]);
        product.Select(["Pears"]);
        Assert.Equal(["Pears"], product.SelectedItems);
        Assert.DoesNotContain("Apples", Cells(book.Worksheets["Compact"]), StringComparison.Ordinal);
        Assert.DoesNotContain("Apples", Cells(book.Worksheets["Outline"]), StringComparison.Ordinal);
        Assert.NotEqual(outline, Cells(book.Worksheets["Outline"]));
        Assert.Contains("Pears", Cells(book.Worksheets["Compact"]), StringComparison.Ordinal);

        Workbook saved = RoundTrip(book);
        Assert.Equal(["Pears"], saved.Worksheets["Compact"].Drawings.Single(d => d.Name == "Product").Slicer!.SelectedItems);

        product.SelectAll();
        Assert.Equal(product.Items, product.SelectedItems);
        Assert.Throws<ArgumentException>(() => product.Select(["Durian"]));
        Assert.Throws<ArgumentException>(() => product.Select([]));
    }

    [Fact]
    public void AMadeSlicerFiltersItsTableAndSavesAsExcelWritesIt()
    {
        Workbook book = SalesBook(out PivotTable table);
        Worksheet report = table.Worksheet;
        SheetDrawing drawing = report.Drawings.AddSlicer(table, "region", RangeAddress.Parse("E2:G12"));
        Slicer slicer = drawing.Slicer!;
        Assert.Equal(SheetDrawingKind.Slicer, drawing.Kind);
        Assert.Equal("Region", slicer.Name);
        Assert.Equal(["East", "North", "South"], slicer.Items);
        Assert.Equal(slicer.Items, slicer.SelectedItems);

        slicer.Select(["North", "South"]);
        Assert.Equal(["North", "South", "Grand Total"], Enumerable.Range(1, 3).Select(r => report[r, 0].Value.ToString()));
        Assert.Equal(17.0, report[3, 1].Value.Number);

        // A second slicer of a field off the table's axes filters it too.
        SheetDrawing month = report.Drawings.AddSlicer(table, "Month", RangeAddress.Parse("I2:K12"));
        month.Slicer!.Select(["Jan"]);
        month.Slicer.StyleName = "SlicerStyleDark2";
        month.Slicer.ColumnCount = 2;
        Assert.Equal(["North", "South", "Grand Total"], Enumerable.Range(1, 3).Select(r => report[r, 0].Value.ToString()));
        Assert.Equal(6.0, report[3, 1].Value.Number);

        using MemoryStream stream = new();
        book.Save(stream);
        using ZipArchive zip = new(new MemoryStream(stream.ToArray()));
        XDocument Part(string name) => XDocument.Load(zip.GetEntry(name)!.Open());
        XDocument types = Part("[Content_Types].xml");
        Assert.Equal(2, types.Root!.Elements().Count(e => (string?)e.Attribute("ContentType") == "application/vnd.ms-excel.slicerCache+xml"));
        Assert.Single(types.Root!.Elements(), e => (string?)e.Attribute("ContentType") == "application/vnd.ms-excel.slicer+xml");

        XDocument cache = Part("xl/slicerCaches/slicerCache1.xml");
        Assert.Equal("Slicer_Region", (string?)cache.Root!.Attribute("name"));
        Assert.Equal("2", (string?)cache.Root.Element(X14 + "pivotTables")!.Element(X14 + "pivotTable")!.Attribute("tabId"));
        XElement tabular = cache.Root.Element(X14 + "data")!.Element(X14 + "tabular")!;
        Assert.Equal(2, tabular.Element(X14 + "items")!.Elements().Count(i => (string?)i.Attribute("s") == "1"));

        XDocument definition = Part("xl/pivotCache/pivotCacheDefinition1.xml");
        Assert.Equal((string?)tabular.Attribute("pivotCacheId"), (string?)definition.Descendants(X14 + "pivotCacheDefinition").Single().Attribute("pivotCacheId"));

        XDocument workbook = Part("xl/workbook.xml");
        Assert.Equal(2, workbook.Descendants(X14 + "slicerCache").Count());
        Assert.Contains(workbook.Descendants(Main + "definedName"), n => (string?)n.Attribute("name") == "Slicer_Month" && n.Value == "#N/A");

        XDocument slicers = Part("xl/slicers/slicer1.xml");
        XElement monthXml = slicers.Root!.Elements(X14 + "slicer").Single(s => (string?)s.Attribute("name") == "Month");
        Assert.Equal("SlicerStyleDark2", (string?)monthXml.Attribute("style"));
        Assert.Equal("2", (string?)monthXml.Attribute("columnCount"));
        XDocument sheet = Part("xl/worksheets/sheet2.xml");
        Assert.Single(sheet.Descendants(X14 + "slicerList"));

        Workbook reread = Workbook.Load(new MemoryStream(stream.ToArray()));
        Slicer[] read = [.. reread.Worksheets["Report"].Drawings.Select(d => d.Slicer).OfType<Slicer>()];
        Assert.Equal(["Region", "Month"], read.Select(s => s.Name));
        Assert.Equal(["North", "South"], read[0].SelectedItems);
        Assert.Equal(["Jan"], read[1].SelectedItems);
        Assert.Equal("Report", read[1].PivotTables.Single().Worksheet.Name);
    }

    [Fact]
    public void ASlicerConnectsAnotherTableOfItsCache()
    {
        Workbook book = Workbook.Load(Book);
        Slicer region = book.Worksheets["Compact"].Drawings.Single(d => d.Name == "Region").Slicer!;
        region.Select(["East"]);
        PivotTable outline = book.Worksheets["Outline"].PivotTables.Single();
        Assert.Contains("North", Cells(book.Worksheets["Outline"]), StringComparison.Ordinal);
        region.Connect(outline);
        Assert.Equal(2, region.PivotTables.Count);
        Assert.DoesNotContain("North", Cells(book.Worksheets["Outline"]), StringComparison.Ordinal);

        Workbook saved = RoundTrip(book);
        Assert.Equal(2, saved.Worksheets["Compact"].Drawings.Single(d => d.Name == "Region").Slicer!.PivotTables.Count);

        Workbook made = SalesBook(out PivotTable first);
        PivotTable table = made.Worksheets["Report"].PivotTables.Add(new CellAddress(20, 0), made.Worksheets["Data"].Range("A1:C7"), new PivotTableOptions { Rows = { "Month" }, Values = { new PivotValueField("Units") } });
        Slicer slicer = made.Worksheets["Report"].Drawings.AddSlicer(first, "Region", RangeAddress.Parse("E2:G12")).Slicer!;
        Assert.Throws<ArgumentException>(() => slicer.Connect(table));
        Assert.Throws<ArgumentException>(() => made.Worksheets["Report"].Drawings.AddSlicer(first, "Nope", RangeAddress.Parse("A1:B2")));
    }

    [Fact]
    public void SlicersAreNamedAsExcelNamesThem()
    {
        Workbook book = SalesBook(out PivotTable table);
        Worksheet report = table.Worksheet;
        Slicer first = report.Drawings.AddSlicer(table, "Region", RangeAddress.Parse("E2:G12")).Slicer!;
        Slicer second = report.Drawings.AddSlicer(table, "Region", RangeAddress.Parse("I2:K12")).Slicer!;
        Assert.Equal("Region", first.Name);
        Assert.Equal("Region 1", second.Name);

        // Two slicers of a field share its cache, and so its selection, as Excel's do.
        first.Select(["South"]);
        Assert.Equal(["South"], second.SelectedItems);
        Assert.DoesNotContain(book.Names, n => n.Name == "Slicer_Region1");
        Workbook saved = RoundTrip(book);
        Slicer[] read = [.. saved.Worksheets["Report"].Drawings.Select(d => d.Slicer).OfType<Slicer>()];
        Assert.Equal(["Region", "Region 1"], read.Select(s => s.Name));
        Assert.All(read, s => Assert.Equal(["South"], s.SelectedItems));
    }

    [Fact]
    public void APrintedSlicerShowsItsSelectedButtons()
    {
        Workbook book = Workbook.Load(Book);
        Worksheet sheet = book.Worksheets["Compact"];
        int all = SelectedPixels(sheet);
        Assert.True(all > 500, $"{all} selected pixels");

        // Region's five buttons, then one.
        sheet.Drawings.Single(d => d.Name == "Region").Slicer!.Select(["East"]);
        int one = SelectedPixels(sheet);
        Assert.InRange(one, all / 5, all * 3 / 5);
    }

    /// <summary>The pixels of a selected button of the default style: accent 1 lightened 60 %, #83CCEB in the Office theme.</summary>
    private static int SelectedPixels(Worksheet sheet)
    {
        using Raster<Rgba8> page = sheet.Paginate().Pages[0].Render(72);
        int count = 0;
        for (int y = 0; y < page.Height; y++)
        {
            foreach (Rgba8 pixel in page.Row(y))
            {
                count += Math.Abs(pixel.R - 0x83) < 6 && Math.Abs(pixel.G - 0xCC) < 6 && Math.Abs(pixel.B - 0xEB) < 6 ? 1 : 0;
            }
        }

        return count;
    }

    private static Workbook SalesBook(out PivotTable table)
    {
        Workbook book = Workbook.Create();
        Worksheet data = book.Worksheets[0];
        data.Name = "Data";
        (string Region, string Month, int Units)[] rows = [("North", "Jan", 1), ("South", "Feb", 2), ("North", "Feb", 3), ("East", "Jan", 4), ("South", "Jan", 5), ("North", "Mar", 6)];
        data[0, 0].Value = "Region";
        data[0, 1].Value = "Month";
        data[0, 2].Value = "Units";
        for (int r = 0; r < rows.Length; r++)
        {
            data[r + 1, 0].Value = rows[r].Region;
            data[r + 1, 1].Value = rows[r].Month;
            data[r + 1, 2].Value = rows[r].Units;
        }

        Worksheet report = book.Worksheets.Add("Report");
        table = report.PivotTables.Add(new CellAddress(0, 0), data.Range("A1:C7"), new PivotTableOptions { Rows = { "Region" }, Values = { new PivotValueField("Units") } });
        return book;
    }

    private static string Cells(Worksheet sheet) => string.Join('\n', sheet.PivotTables.Select(t =>
        string.Join('|', Enumerable.Range(t.Range.First.Row, t.Range.RowCount).SelectMany(r =>
            Enumerable.Range(t.Range.First.Column, t.Range.ColumnCount).Select(c => sheet[r, c].Value.ToString())))));

    private static Workbook RoundTrip(Workbook book)
    {
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Workbook.Load(stream);
    }
}
