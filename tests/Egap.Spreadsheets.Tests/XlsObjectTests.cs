
namespace Egap.Spreadsheets.Tests;

/// <summary>
/// The objects of an XLS file against Excel's own XLSX of it: <c>Oracle/xlsobjects/objects.xls</c> was made by Excel (see
/// <c>objects.txt</c> and <c>tools/excel-xls-objects-oracle.ps1</c>) with notes, hyperlinks, pictures, shapes, a group,
/// embedded charts, a chart sheet and defined names, and <c>objects.xlsx</c> is Excel's save of that file.
/// </summary>
public sealed class XlsObjectTests
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Oracle", "xlsobjects", name);

    private static Workbook Excel() => Workbook.Load(Path("objects.xlsx"));

    [Fact]
    public void ReadsTheObjectsExcelSees() => AssertSame(Excel(), Workbook.Load(Path("objects.xls")));

    [Fact]
    public void SavesTheObjectsToXlsx()
    {
        using var stream = new MemoryStream();
        Workbook.Load(Path("objects.xls")).Save(stream, new WorkbookSaveOptions { Format = WorkbookFormat.Xlsx });
        stream.Position = 0;
        AssertSame(Excel(), Workbook.Load(stream));
    }

    [Fact]
    public void ReadsEachKindOfObject()
    {
        Workbook book = Workbook.Load(Path("objects.xls"));
        Worksheet data = book.Worksheets["Data"];
        Assert.Equal(3, data.Comments.Count);
        Assert.Equal("Two\nlines", data.Comments[CellAddress.Parse("C4")]!.Text.Split(':', 2)[^1].TrimStart('\n'));
        Assert.Equal(4, data.Hyperlinks.Count);
        Assert.Contains(data.Hyperlinks, h => h.ToolTip == "Example tip");
        Assert.Contains(data.Hyperlinks, h => h.Location == "Data!A1");
        Assert.Equal(10, data.Drawings.Count(d => d.Kind == SheetDrawingKind.Chart));

        Worksheet art = book.Worksheets["Art"];
        Assert.Equal(["image/png", "image/jpeg"], art.Drawings.Where(d => d.Kind == SheetDrawingKind.Picture).Select(d => d.ImageContentType));
        Assert.Contains(art.Drawings, d => d.Kind == SheetDrawingKind.Shape && d.Text == "Box text");
        Assert.Single(art.Drawings, d => d.Kind == SheetDrawingKind.Group);

        ChartSheet page = Assert.Single(book.ChartSheets);
        Assert.Equal("Chart Sheet", page.Name);
        Assert.Contains(book.Names, n => n.Name == "Hidden_Name" && n.Hidden);
        Assert.Contains(book.Names, n => n.Name == "Local" && n.Sheet == data);
    }

    [Fact]
    public void ReadsThePivotTable()
    {
        PivotTable excel = Assert.Single(Excel().Worksheets["Pivot"].PivotTables);
        PivotTable ours = Assert.Single(Workbook.Load(Path("objects.xls")).Worksheets["Pivot"].PivotTables);
        Assert.Equal("SalesPivot", ours.Name);
        Assert.Equal((excel.Name, excel.Range, excel.Source, excel.CanRefresh), (ours.Name, ours.Range, ours.Source, ours.CanRefresh));
        Assert.True(ours.CanRefresh);
    }

    [Fact]
    public void RefreshesThePivotTableAsExcelDoes()
    {
        Workbook excel = Excel();
        Workbook ours = Workbook.Load(Path("objects.xls"));
        excel.Worksheets["Pivot"].PivotTables[0].Refresh();
        ours.Worksheets["Pivot"].PivotTables[0].Refresh();
        Assert.Equal(Cells(excel.Worksheets["Pivot"]), Cells(ours.Worksheets["Pivot"]));
    }

    [Fact]
    public void SavesThePivotTableToXlsx()
    {
        using var stream = new MemoryStream();
        Workbook.Load(Path("objects.xls")).Save(stream, new WorkbookSaveOptions { Format = WorkbookFormat.Xlsx });
        stream.Position = 0;
        Workbook saved = Workbook.Load(stream);
        PivotTable pivot = Assert.Single(saved.Worksheets["Pivot"].PivotTables);
        PivotTable excel = Assert.Single(Excel().Worksheets["Pivot"].PivotTables);
        Assert.Equal((excel.Name, excel.Range, excel.Source, excel.CanRefresh), (pivot.Name, pivot.Range, pivot.Source, pivot.CanRefresh));
        Workbook reference = Excel();
        reference.Worksheets["Pivot"].PivotTables[0].Refresh();
        pivot.Refresh();
        Assert.Equal(Cells(reference.Worksheets["Pivot"]), Cells(saved.Worksheets["Pivot"]));
    }

    [Theory]
    [InlineData("damaged-hyperlink.xls")]
    [InlineData("damaged-chart.xls")]
    [InlineData("damaged-chart-3d.xls")]
    public void ReadsDamagedObjects(string name)
    {
        // Fuzzed copies of objects.xls, with a hyperlink string longer than its record, a chart group beside one without
        // axes and a flat group in a 3D chart: they are read up to a sheet damaged further on, which only a
        // DocumentException may report.
        Exception? error = Record.Exception(() =>
        {
            using var stream = new MemoryStream();
            Workbook.Load(Path(name)).Save(stream, new WorkbookSaveOptions { Format = WorkbookFormat.Xlsx });
        });
        Assert.True(error is null or Documents.DocumentException, error?.ToString());
    }

    [Fact]
    public void KeepsTheFirstPlotWhenAGroupCannotShareTheChart()
    {
        // A stacked column group with a 50 % gap, then a pie group, which cannot share a chart with it and goes with the
        // column plot without changing it.
        static byte[] Format(int order) => [.. new byte[18], (byte)order, 0];
        List<(int, byte[])> records =
        [
            (0x1014, Format(0)), (0x1033, []), (0x1017, [0, 0, 50, 0, 0x02, 0]), (0x1034, []),
            (0x1014, Format(1)), (0x1033, []), (0x1019, [0, 0, 0, 0, 0, 0]), (0x1034, []),
        ];
        Documents.Charts.Chart chart = Assert.IsType<Documents.Charts.Chart>(Xls.XlsChart.Read(records, _ => null));
        Documents.Charts.ChartPlot plot = Assert.Single(chart.Plots);
        Assert.Equal((Documents.Charts.ChartType.Column, Documents.Charts.ChartGrouping.Stacked, 50, 100), (plot.Type, plot.Grouping, plot.GapWidth, plot.Overlap));
    }

    [Fact]
    public void ReadsA3DPieWithAFirstSliceAngle()
    {
        // A pie group turned by 30 degrees with a Chart3D record: a 3D pie, which has no first slice angle of its own.
        List<(int, byte[])> records =
        [
            (0x1014, new byte[20]), (0x1033, []), (0x1019, [30, 0, 0, 0, 0, 0]), (0x103A, new byte[14]), (0x1034, []),
        ];
        Documents.Charts.Chart chart = Assert.IsType<Documents.Charts.Chart>(Xls.XlsChart.Read(records, _ => null));
        Documents.Charts.ChartPlot plot = Assert.Single(chart.Plots);
        Assert.Equal((Documents.Charts.ChartType.Pie, true, (int?)null), (plot.Type, plot.Is3D, plot.FirstSliceAngle));
    }

    private static List<(string, string)> Cells(Worksheet sheet) =>
        [.. sheet.Cells.Where(c => !c.IsEmpty).Select(c => (c.Address.ToString(), c.Value.ToString())).Order()];

    private static void AssertSame(Workbook excel, Workbook ours)
    {
        foreach (DefinedName name in excel.Names)
        {
            DefinedName mine = Assert.Single(ours.Names, n => n.Name.Equals(name.Name, StringComparison.OrdinalIgnoreCase) && n.Sheet?.Name == name.Sheet?.Name);
            Assert.Equal(name.Formula, mine.Formula);
            Assert.Equal(name.Hidden, mine.Hidden);
        }

        Assert.Equal(excel.Names.Count, ours.Names.Count);
        Assert.Equal(excel.ChartSheets.Select(c => (c.Name, c.Chart?.Type, c.Chart?.Series.Count)), ours.ChartSheets.Select(c => (c.Name, c.Chart?.Type, c.Chart?.Series.Count)));
        foreach (Worksheet sheet in excel.Worksheets)
        {
            Worksheet mine = ours.Worksheets[sheet.Name];
            Assert.Equal(sheet.Comments.Select(c => (c.Cell, c.Author, c.Text)), mine.Comments.Select(c => (c.Cell, c.Author, c.Text)));
            Assert.Equal(sheet.Hyperlinks.Select(h => (h.Range.ToString(), h.Address, h.Location, h.ToolTip)).Order(), mine.Hyperlinks.Select(h => (h.Range.ToString(), h.Address, h.Location, h.ToolTip)).Order());
            Assert.Equal(sheet.Drawings.Count, mine.Drawings.Count);
            List<SheetDrawing> left = [.. mine.Drawings];
            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                SheetDrawing match = left.First(d => d.Kind == drawing.Kind && d.From == drawing.From);
                left.Remove(match);
                Assert.Equal(drawing.To, match.To);
                Assert.Equal(drawing.AnchorKind, match.AnchorKind);
                Assert.Equal(drawing.Kind is SheetDrawingKind.Shape ? drawing.Text ?? "" : "", drawing.Kind is SheetDrawingKind.Shape ? match.Text ?? "" : "");
                if (drawing.Kind == SheetDrawingKind.Picture)
                {
                    Assert.Equal(drawing.ImageContentType, match.ImageContentType);
                    Assert.Equal(drawing.ImageData, match.ImageData);
                }

                if (drawing.Kind == SheetDrawingKind.Chart)
                {
                    AssertSame(drawing.Chart!, match.Chart!);
                }
            }
        }
    }

    private static void AssertSame(Documents.Charts.Chart excel, Documents.Charts.Chart ours)
    {
        Assert.Equal(excel.Type, ours.Type);
        Assert.Equal(excel.Title.Text ?? "", ours.Title.Text ?? "");
        Assert.Equal(excel.HasLegend, ours.HasLegend);
        Assert.Equal(excel.Series.Select(s => (s.Values.Reference, s.Categories.Reference, s.NameReference)), ours.Series.Select(s => (s.Values.Reference, s.Categories.Reference, s.NameReference)));
        Assert.Equal(excel.Plots.Select(p => (p.Type, p.Is3D, p.Grouping, p.GapWidth, p.Overlap ?? 0, p.Series.Count)), ours.Plots.Select(p => (p.Type, p.Is3D, p.Grouping, p.GapWidth, p.Overlap ?? 0, p.Series.Count)));
    }
}
